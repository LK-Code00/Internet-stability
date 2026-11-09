namespace NetStability.Core.Models;

public sealed record ProbeSample(
    DateTimeOffset At,
    double? GatewayMs,
    double? InternetMs,
    double? GameMs,
    double? DownloadMbps = null,
    double? UploadMbps = null);

public sealed record MetricSummary(
    int Sent,
    int Received,
    double? MinimumMs,
    double? AverageMs,
    double? MaximumMs,
    double? JitterMs,
    double LossPercent)
{
    public static MetricSummary From(IEnumerable<double?> samples)
    {
        var values = samples.ToArray();
        var received = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var jitterSamples = values.Zip(values.Skip(1), (a, b) => a.HasValue && b.HasValue ? Math.Abs(b.Value - a.Value) : (double?)null)
            .Where(value => value.HasValue).Select(value => value!.Value).ToArray();

        return new MetricSummary(
            values.Length,
            received.Length,
            received.Length == 0 ? null : received.Min(),
            received.Length == 0 ? null : received.Average(),
            received.Length == 0 ? null : received.Max(),
            jitterSamples.Length == 0 ? null : jitterSamples.Average(),
            values.Length == 0 ? 0 : (values.Length - received.Length) * 100d / values.Length);
    }
}

public sealed record DiagnosticReport
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset FinishedAt { get; init; }
    public string AdapterName { get; init; } = "Desconhecido";
    public string AdapterDescription { get; init; } = "";
    public string AdapterAdvancedSettings { get; init; } = "";
    public string ConnectionType { get; init; } = "Desconhecido";
    public string Gateway { get; init; } = "";
    public int? WifiSignal { get; init; }
    public int? WifiChannel { get; init; }
    public int? SameChannelNetworks { get; init; }
    public string TcpAutoTuningLevel { get; init; } = "Desconhecido";
    public string VpnAdapters { get; init; } = "";
    public int Mtu { get; init; }
    public string[] DnsServers { get; init; } = [];
    public double? DnsLookupMs { get; init; }
    public string MtuProbeResult { get; init; } = "";
    public string RouteTrace { get; init; } = "";
    public Guid? ComparisonBaselineId { get; init; }
    public string ComparisonSummary { get; init; } = "";
    public string? GameTarget { get; init; }
    public List<ProbeSample> Samples { get; init; } = [];
    public List<string> Findings { get; init; } = [];

    public MetricSummary GatewayMetrics => MetricSummary.From(Samples.Select(sample => sample.GatewayMs));
    public MetricSummary InternetMetrics => MetricSummary.From(Samples.Select(sample => sample.InternetMs));
    public MetricSummary GameMetrics => GameTarget is null
        ? MetricSummary.From([])
        : MetricSummary.From(Samples.Select(sample => sample.GameMs));

    public double? AverageDownMbps => Average(Samples.Select(sample => sample.DownloadMbps));
    public double? AverageUpMbps => Average(Samples.Select(sample => sample.UploadMbps));

    private static double? Average(IEnumerable<double?> source)
    {
        var values = source.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }
}
