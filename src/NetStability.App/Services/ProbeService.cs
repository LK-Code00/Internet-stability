using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetStability.Core.Models;

namespace NetStability.App.Services;

public sealed class ProbeService
{
    private static readonly byte[] Payload = new byte[32];

    public async Task<double?> PingAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        try
        {
            IPAddress address;
            if (!IPAddress.TryParse(host.Trim(), out address!))
            {
                var addresses = await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
                address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault()
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            }

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(950), Payload,
                new PingOptions(64, true), cancellationToken);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task<ProbeSample> ProbeCycleAsync(NetworkSnapshot snapshot, string externalTarget,
        string? gameTarget, TrafficSampler traffic, CancellationToken cancellationToken)
    {
        var gateway = string.IsNullOrWhiteSpace(snapshot.Gateway)
            ? null
            : await PingAsync(snapshot.Gateway, cancellationToken);
        var external = await PingAsync(externalTarget, cancellationToken);
        var game = string.IsNullOrWhiteSpace(gameTarget) ? null : await PingAsync(gameTarget, cancellationToken);
        var rates = traffic.Sample(snapshot.InterfaceIndex);
        return new ProbeSample(DateTimeOffset.Now, gateway, external, game, rates.DownloadMbps, rates.UploadMbps);
    }
}

public sealed class TrafficSampler
{
    private long? _lastReceived;
    private long? _lastSent;
    private DateTimeOffset? _lastAt;

    public (double? DownloadMbps, double? UploadMbps) Sample(int interfaceIndex)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(item => item.OperationalStatus == OperationalStatus.Up &&
                    item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    item.GetIPProperties().GetIPv4Properties()?.Index == interfaceIndex);
            if (nic is null) return (null, null);
            var stats = nic.GetIPv4Statistics();
            var now = DateTimeOffset.UtcNow;
            var received = stats.BytesReceived;
            var sent = stats.BytesSent;
            if (_lastAt is null || _lastReceived is null || _lastSent is null)
            {
                _lastAt = now; _lastReceived = received; _lastSent = sent;
                return (0, 0);
            }

            var seconds = Math.Max(0.001, (now - _lastAt.Value).TotalSeconds);
            var down = Math.Max(0, received - _lastReceived.Value) * 8 / seconds / 1_000_000;
            var up = Math.Max(0, sent - _lastSent.Value) * 8 / seconds / 1_000_000;
            _lastAt = now; _lastReceived = received; _lastSent = sent;
            return (down, up);
        }
        catch { return (null, null); }
    }
}

public sealed class DnsDiagnosticService
{
    public async Task<double?> MeasureSystemResolverAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            _ = await Dns.GetHostAddressesAsync("example.com", cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
