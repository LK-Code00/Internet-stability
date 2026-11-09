using System.Net;
using System.Net.NetworkInformation;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace NetStability.App.Services;

public sealed record NetworkSnapshot(
    string AdapterName,
    string AdapterDescription,
    string AdapterAdvancedSettings,
    string ConnectionType,
    string LinkSpeed,
    int InterfaceIndex,
    string Gateway,
    int Mtu,
    string TcpAutoTuningLevel,
    string VpnAdapters,
    string[] DnsServers,
    int? WifiSignal,
    int? WifiChannel,
    int? SameChannelNetworks,
    string RadioType,
    string DriverVersion,
    string PowerManagement,
    string ActivePowerSchemeGuid,
    int? WifiPowerModeAc)
{
    public static NetworkSnapshot Empty { get; } = new("(não identificado)", "", "", "Desconhecido", "", 0,
        "", 0, "Desconhecido", "", [], null, null, null, "", "", "Não disponível", "", null);
}

public sealed class NetworkSnapshotService
{
    private const string SnapshotScript = @"
$ErrorActionPreference = 'SilentlyContinue'
$physical = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up')
$routes = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric, InterfaceMetric)
$adapter = $null; $route = $null
foreach ($r in $routes) {
  $candidate = $physical | Where-Object ifIndex -eq $r.InterfaceIndex | Select-Object -First 1
  if ($candidate) { $adapter = $candidate; $route = $r; break }
}
if (-not $adapter) { $adapter = $physical | Select-Object -First 1; $route = $routes | Select-Object -First 1 }
$idx = if ($adapter) { [int]$adapter.ifIndex } else { 0 }
if ($idx -gt 0) {
  $localRoute = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -InterfaceIndex $idx -ErrorAction SilentlyContinue | Sort-Object RouteMetric,InterfaceMetric | Select-Object -First 1
  if ($localRoute) { $route = $localRoute }
}
$ipif = if ($idx -gt 0) { Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1 } else { $null }
$dns = @(); if ($idx -gt 0) { $dns = @(Get-DnsClientServerAddress -InterfaceIndex $idx -ErrorAction SilentlyContinue | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ }) }
$tcp = Get-NetTCPSetting -SettingName Internet -ErrorAction SilentlyContinue | Select-Object -First 1
$power = 'Não disponível'
if ($adapter) { try { $p = Get-NetAdapterPowerManagement -Name $adapter.Name -ErrorAction Stop; $power = 'SelectiveSuspend=' + [string]$p.SelectiveSuspend + '; DeviceSleepOnDisconnect=' + [string]$p.DeviceSleepOnDisconnect } catch {} }
$driverVersion = ''; if ($adapter) { $d = Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue | Where-Object DeviceName -eq $adapter.InterfaceDescription | Select-Object -First 1; if ($d) { $driverVersion = [string]$d.DriverVersion } }
$advanced = @(); if ($adapter) { $advanced = @(Get-NetAdapterAdvancedProperty -Name $adapter.Name -ErrorAction SilentlyContinue | Select-Object DisplayName,DisplayValue,RegistryKeyword,RegistryValue,ValidDisplayValues) }
$advancedJson = ConvertTo-Json -InputObject $advanced -Compress -Depth 4
$powerRoot = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes'
$powerScheme = ''; $wifiAc = $null
try {
  $powerScheme = [string](Get-ItemProperty -Path $powerRoot -Name ActivePowerScheme -ErrorAction Stop).ActivePowerScheme
  $wifiSetting = Join-Path (Join-Path (Join-Path $powerRoot $powerScheme) '19cbb8fa-5279-450e-9fac-8a3d5fedd0c1') '12bbebe6-58d6-4636-95bb-3217ef867c1a'
  $powerValue = Get-ItemProperty -Path $wifiSetting -Name ACSettingIndex -ErrorAction Stop
  $wifiAc = [int]$powerValue.ACSettingIndex
} catch {}
$vpn = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Up' -and ($_.Name + ' ' + $_.InterfaceDescription) -match '(?i)VPN|WireGuard|Wintun|OpenVPN|TAP-Windows|TUN' } | ForEach-Object { $_.Name })
$wifi = (& netsh.exe wlan show interfaces 2>$null | Out-String)
$near = (& netsh.exe wlan show networks mode=bssid 2>$null | Out-String)
[ordered]@{
 AdapterName = [string]$adapter.Name
 AdapterDescription = [string]$adapter.InterfaceDescription
 AdapterAdvancedSettings = $advancedJson
 LinkSpeed = [string]$adapter.LinkSpeed
 InterfaceIndex = $idx
 Gateway = [string]$route.NextHop
 Mtu = [int]$ipif.NlMtu
 TcpAutoTuningLevel = [string]$tcp.AutoTuningLevelLocal
 VpnAdapters = ($vpn -join ', ')
 DnsServers = @($dns)
 WifiRaw = $wifi
 NearbyRaw = $near
 RadioType = ''
 DriverVersion = $driverVersion
 PowerManagement = $power
 ActivePowerSchemeGuid = $powerScheme
 WifiPowerModeAc = $wifiAc
} | ConvertTo-Json -Compress -Depth 4
";

    public async Task<NetworkSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await PowerShell.RunAsync(SnapshotScript, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var adapterName = ReadString(root, "AdapterName");
            var description = ReadString(root, "AdapterDescription");
            var index = ReadInt(root, "InterfaceIndex");
            var type = description.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
                       adapterName.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) ||
                       adapterName.Contains("wlan", StringComparison.OrdinalIgnoreCase)
                ? "Wi-Fi"
                : "Ethernet / outro físico";
            var wifiRaw = ReadString(root, "WifiRaw");
            var nearbyRaw = ReadString(root, "NearbyRaw");
            var signal = ReadNumber(wifiRaw, "(?:Signal|Sinal)");
            var channel = ReadNumber(wifiRaw, "(?:Channel|Canal)");
            var radio = ReadText(wifiRaw, "(?:Radio type|Tipo de rádio|Tipo de radio)");
            var sameChannel = channel.HasValue
                ? Regex.Matches(nearbyRaw, @"(?im)^\s*(?:Channel|Canal)\s*:\s*" + channel.Value + @"\s*$").Count
                : (int?)null;
            var dns = ReadArray(root, "DnsServers");
            var mtu = ReadInt(root, "Mtu");
            var snapshot = new NetworkSnapshot(
                string.IsNullOrWhiteSpace(adapterName) ? "(não identificado)" : adapterName,
                description,
                ReadString(root, "AdapterAdvancedSettings"),
                type,
                ReadString(root, "LinkSpeed"),
                index,
                ReadString(root, "Gateway"),
                mtu,
                ReadString(root, "TcpAutoTuningLevel"),
                ReadString(root, "VpnAdapters"),
                dns,
                signal,
                channel,
                sameChannel,
                radio,
                ReadString(root, "DriverVersion"),
                ReadString(root, "PowerManagement"),
                ReadString(root, "ActivePowerSchemeGuid"),
                ReadNullableInt(root, "WifiPowerModeAc"));
            return string.IsNullOrWhiteSpace(snapshot.Gateway) || snapshot.Gateway is "0.0.0.0" or "::"
                ? await WithFallbackGateway(snapshot, cancellationToken)
                : snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return await ReadFallbackAsync(cancellationToken);
        }
    }

    private static async Task<NetworkSnapshot> WithFallbackGateway(NetworkSnapshot snapshot, CancellationToken token)
    {
        var fallback = await ReadFallbackAsync(token);
        return snapshot with
        {
            Gateway = string.IsNullOrWhiteSpace(snapshot.Gateway) || snapshot.Gateway is "0.0.0.0" or "::" ? fallback.Gateway : snapshot.Gateway,
            InterfaceIndex = snapshot.InterfaceIndex == 0 ? fallback.InterfaceIndex : snapshot.InterfaceIndex,
            AdapterName = snapshot.AdapterName == "(não identificado)" ? fallback.AdapterName : snapshot.AdapterName,
            AdapterDescription = string.IsNullOrWhiteSpace(snapshot.AdapterDescription) ? fallback.AdapterDescription : snapshot.AdapterDescription,
            ConnectionType = snapshot.ConnectionType == "Desconhecido" ? fallback.ConnectionType : snapshot.ConnectionType,
            Mtu = snapshot.Mtu == 0 ? fallback.Mtu : snapshot.Mtu,
            VpnAdapters = snapshot.VpnAdapters,
            DnsServers = snapshot.DnsServers.Length == 0 ? fallback.DnsServers : snapshot.DnsServers
        };
    }

    private static Task<NetworkSnapshot> ReadFallbackAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var properties = nic.GetIPProperties();
                var gateway = properties.GatewayAddresses.Select(item => item.Address)
                    .FirstOrDefault(address => address is not null && !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address));
                if (gateway is null) continue;
                var ipProperties = properties.GetIPv4Properties();
                var type = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet / outro físico";
                return Task.FromResult(new NetworkSnapshot(nic.Name, nic.Description, "", type, nic.Speed.ToString(),
                    ipProperties?.Index ?? 0, gateway.ToString(), ipProperties?.Mtu ?? 0, "Desconhecido", "",
                    properties.DnsAddresses.Select(item => item.ToString()).ToArray(), null, null, null, "", "", "Não disponível", "", null));
            }
            catch { }
        }
        return Task.FromResult(NetworkSnapshot.Empty);
    }

    private static string ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) ? value.ToString() : "";
    private static int ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static int? ReadNullableInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : null;
    private static string[] ReadArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ToString()).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
            : [];
    private static int? ReadNumber(string source, string label) =>
        int.TryParse(Regex.Match(source, @"(?im)^\s*" + label + @"\s*:\s*(\d+)").Groups[1].Value, out var value) ? value : null;
    private static string ReadText(string source, string label) =>
        Regex.Match(source, @"(?im)^\s*" + label + @"\s*:\s*(.+?)\s*$").Groups[1].Value;
}

internal static class PowerShell
{
    public static async Task<string> RunAsync(string script, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "PowerShell falhou." : error.Trim());
        return output.Trim();
    }
}
