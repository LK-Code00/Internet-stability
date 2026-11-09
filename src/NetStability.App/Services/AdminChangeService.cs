using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;

namespace NetStability.App.Services;

public sealed class AdminChangeService
{
    private const string WifiSubgroup = "19cbb8fa-5279-450e-9fac-8a3d5fedd0c1";
    private const string WifiPowerSetting = "12bbebe6-58d6-4636-95bb-3217ef867c1a";

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static bool IsOnAcPower => GetSystemPowerStatus(out var status) && status.AcLineStatus == 1;

    public bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public async Task SetTcpAutotuningAsync(string expectedCurrent, string desired, CancellationToken cancellationToken = default)
    {
        var validLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "disabled", "highlyrestricted", "restricted", "normal", "experimental"
        };
        if (!validLevels.Contains(expectedCurrent) || !validLevels.Contains(desired))
            throw new InvalidOperationException("Nível de autotuning desconhecido; nenhuma alteração foi aplicada.");

        var script = $@"
$ErrorActionPreference = 'Stop'
$expected = '{Escape(expectedCurrent.ToLowerInvariant())}'
$desired = '{Escape(desired.ToLowerInvariant())}'
$current = (Get-NetTCPSetting -SettingName Internet -ErrorAction Stop | Select-Object -First 1).AutoTuningLevelLocal.ToString().ToLowerInvariant()
if ($current -ne $expected) {{ throw ('A configuração mudou desde o diagnóstico. Atual: ' + $current + '; esperado: ' + $expected) }}
& netsh.exe interface tcp set global ('autotuninglevel=' + $desired) | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'netsh não aceitou o nível solicitado.' }}
$verify = (Get-NetTCPSetting -SettingName Internet -ErrorAction Stop | Select-Object -First 1).AutoTuningLevelLocal.ToString().ToLowerInvariant()
if ($verify -ne $desired) {{ throw ('A verificação após a alteração retornou ' + $verify) }}
";
        await RunElevatedAsync(script, cancellationToken);
    }

    public Task SetWifiPowerModeAsync(string schemeGuid, int expectedCurrent, int desired, CancellationToken cancellationToken = default, bool activatePlan = true)
    {
        if (!Guid.TryParse(schemeGuid, out var parsed) || expectedCurrent is < 0 or > 3 || desired is < 0 or > 3)
            throw new InvalidOperationException("O plano ou nível de energia Wi-Fi não é válido.");
        var scheme = parsed.ToString("D");
        var script = $@"
$ErrorActionPreference = 'Stop'
$scheme = '{scheme}'
$subgroup = '{WifiSubgroup}'
$setting = '{WifiPowerSetting}'
$activatePlan = {activatePlan.ToString().ToLowerInvariant()}
$root = 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes'
$active = [string](Get-ItemProperty -Path $root -Name ActivePowerScheme -ErrorAction Stop).ActivePowerScheme
if ($activatePlan -and $active -ne $scheme) {{ throw 'O plano de energia ativo mudou desde o diagnóstico. Nenhuma alteração foi feita.' }}
$key = Join-Path (Join-Path (Join-Path $root $scheme) $subgroup) $setting
$current = [int](Get-ItemProperty -Path $key -Name ACSettingIndex -ErrorAction Stop).ACSettingIndex
if ($current -ne {expectedCurrent}) {{ throw ('A economia de energia Wi-Fi mudou desde o diagnóstico. Valor atual: ' + $current) }}
& powercfg.exe /setacvalueindex $scheme $subgroup $setting {desired} | Out-Null
if ($LASTEXITCODE -ne 0) {{ throw 'O Windows não aceitou o perfil de energia Wi-Fi.' }}
if ($activatePlan) {{
  & powercfg.exe /setactive $scheme | Out-Null
  if ($LASTEXITCODE -ne 0) {{ throw 'O Windows não conseguiu aplicar o plano de energia.' }}
}}
$verify = [int](Get-ItemProperty -Path $key -Name ACSettingIndex -ErrorAction Stop).ACSettingIndex
if ($verify -ne {desired}) {{ throw ('A verificação retornou o nível ' + $verify) }}
";
        return RunElevatedAsync(script, cancellationToken);
    }

    public Task<string> ReadAdapterPowerManagementAsync(string adapterName, CancellationToken cancellationToken = default)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
$name = '{Escape(adapterName)}'
$settings = Get-NetAdapterPowerManagement -Name $name -ErrorAction Stop | Select-Object -First 1
$settings | ConvertTo-Json -Compress -Depth 3
";
        return RunElevatedAsync(script, cancellationToken);
    }

    private static async Task<string> RunElevatedAsync(string script, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetStability");
        Directory.CreateDirectory(directory);
        var resultPath = Path.Combine(directory, $"elevation-{Guid.NewGuid():N}.txt");
        var wrapper = $@"
$ErrorActionPreference = 'Stop'
try {{
  $payload = & {{
    {script}
  }}
  if ($null -eq $payload -or [string]::IsNullOrWhiteSpace([string]$payload)) {{ $payload = 'OK' }}
  [IO.File]::WriteAllText('{Escape(resultPath)}', [string]$payload, [Text.Encoding]::UTF8)
  exit 0
}} catch {{
  [IO.File]::WriteAllText('{Escape(resultPath)}', ('ERROR: ' + $_.Exception.Message), [Text.Encoding]::UTF8)
  exit 1
}}
";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}"
        };

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar a operação administrativa.");
            await Task.Run(() => process.WaitForExit(), cancellationToken);
            if (!File.Exists(resultPath))
                throw new InvalidOperationException("A operação administrativa terminou sem resultado. Verifique se confirmou a solicitação do Windows.");
            var result = File.ReadAllText(resultPath).Trim();
            if (!result.Equals("OK", StringComparison.Ordinal))
                throw new InvalidOperationException(result.StartsWith("ERROR: ", StringComparison.Ordinal) ? result[7..] : result);
            return result;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("A solicitação de administrador foi cancelada.", exception);
        }
        finally
        {
            try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
        }
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
