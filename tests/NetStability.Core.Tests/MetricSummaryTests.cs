using NetStability.Core.Analysis;
using NetStability.Core.Models;
using Xunit;

namespace NetStability.Core.Tests;

public class MetricSummaryTests
{
    [Fact]
    public void SummaryComputesLossAndMeanAbsoluteSuccessiveDelta()
    {
        var summary = MetricSummary.From(new double?[] { 20, 30, null, 40 });

        Assert.Equal(4, summary.Sent);
        Assert.Equal(3, summary.Received);
        Assert.Equal(25d, summary.LossPercent);
        Assert.Equal(20d, summary.MinimumMs!.Value);
        Assert.Equal(30d, summary.AverageMs!.Value);
        Assert.Equal(40d, summary.MaximumMs!.Value);
        Assert.Equal(10d, summary.JitterMs!.Value);
    }

    [Fact]
    public void EmptySamplesDoNotInventLatency()
    {
        var summary = MetricSummary.From([]);

        Assert.Null(summary.AverageMs);
        Assert.Null(summary.JitterMs);
        Assert.Equal(0d, summary.LossPercent);
    }

    [Fact]
    public void JitterDoesNotBridgeAcrossALostProbe()
    {
        var summary = MetricSummary.From(new double?[] { 20, null, 100 });

        Assert.Null(summary.JitterMs);
        Assert.Equal(100d / 3d, summary.LossPercent, 10);
    }

    [Fact]
    public void ScoreReportsNoDataWhenBothDestinationsAreSilent()
    {
        var empty = MetricSummary.From([]);
        var score = ConnectionScorer.Score(empty, empty);

        Assert.Equal("Sem dados", score.Label);
        Assert.Equal(0, score.Value);
    }

    [Fact]
    public void ScoreMarksExternalQualityUnknownWhenOnlyGatewayReplies()
    {
        var gateway = MetricSummary.From(new double?[] { 1, 2, 2 });
        var internet = MetricSummary.From(new double?[] { null, null, null });

        var score = ConnectionScorer.Score(gateway, internet);

        Assert.StartsWith("Parcial", score.Label);
        Assert.Contains("fim a fim é desconhecida", score.Explanation);
    }

    [Theory]
    [InlineData("disabled", true)]
    [InlineData("highlyrestricted", true)]
    [InlineData("Normal", false)]
    [InlineData("unknown", false)]
    [InlineData(null, false)]
    public void TcpOptimizationOnlyTargetsKnownNonDefaultLevels(string? level, bool expected)
    {
        Assert.Equal(expected, FindingAnalyzer.ShouldRestoreDefaultTcpAutotuning(level));
    }
}
