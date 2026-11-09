using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;

namespace NetStability.App.Services;

public sealed class PathDiagnosticsService
{
    private static readonly int[] Ipv4PayloadSizes = [1472, 1400, 1200];

    public async Task<string> CheckPathMtuAsync(string target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Destino MTU ausente.";
        IPAddress? address;
        if (!IPAddress.TryParse(target, out address))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                address = addresses.FirstOrDefault(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch { address = null; }
        }

        if (address is null || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return "Sonda de MTU não executada: destino IPv4 não resolvido.";

        var results = new List<(int Size, bool Success)>();
        foreach (var size in Ipv4PayloadSizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(650), new byte[size],
                    new PingOptions(64, true), cancellationToken);
                results.Add((size, reply.Status == IPStatus.Success));
            }
            catch (OperationCanceledException) { throw; }
            catch { results.Add((size, false)); }
        }

        var supported = string.Join(", ", results.Where(item => item.Success).Select(item => item.Size));
        if (results.All(item => item.Success))
            return $"IPv4 ICMP DF recebeu resposta para payloads de {supported} bytes (1472 bytes correspondem a 1500 bytes com cabeçalhos IP/ICMP).";
        if (results[^1].Success && results[0].Success == false)
            return $"Possível limite menor que MTU 1500: payloads respondidos {supported}; 1472 sem resposta. Filtragem de ICMP grande também pode explicar.";
        if (results.Any(item => item.Success))
            return $"Resposta parcial a cargas DF; payloads respondidos {supported}. Resultado inconclusivo para MTU por filtragem ou política do caminho.";
        return "Nenhum tamanho DF respondeu. MTU inconclusiva: o destino, a VPN ou a rede podem filtrar ICMP.";
    }

    public async Task<string> TraceRouteAsync(string target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Rota não testada: destino ausente.";
        IPAddress? address;
        if (!IPAddress.TryParse(target, out address))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                address = addresses.FirstOrDefault(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch { address = null; }
        }
        if (address is null) return "Rota não testada: destino IPv4 não resolvido.";

        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tracert.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add("-h");
        start.ArgumentList.Add("8");
        start.ArgumentList.Add("-w");
        start.ArgumentList.Add("180");
        start.ArgumentList.Add(address.ToString());

        try
        {
            using var process = Process.Start(start);
            if (process is null) return "Não foi possível iniciar tracert.exe.";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(true); } catch { }
                return "Rota incompleta: tempo limite de 8 segundos; tentativas intermediárias podem ser filtradas.";
            }
            var output = await outputTask;
            var error = await errorTask;
            var result = string.IsNullOrWhiteSpace(output) ? error : output;
            return string.IsNullOrWhiteSpace(result) ? "tracert não retornou dados." : result.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return $"Falha ao executar tracert: {exception.Message}"; }
    }
}
