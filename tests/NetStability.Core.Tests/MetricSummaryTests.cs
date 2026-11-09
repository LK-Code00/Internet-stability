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

    [Fact]
    public void WifiAgentActsOnlyAfterPersistentLocalDegradationWithSafeConditions()
    {
        var gateway = MetricSummary.From(new double?[] { 2, 20, 2, 20, 2, 20, 2, 20 });
        var decision = RealtimeOptimizationAgent.Decide(new WifiAgentObservation(
            true, 80, true, gateway, false, true, 2, false));

        Assert.True(decision.ShouldApplyMaximumPerformance);
        Assert.Contains("perda", decision.Message);
    }

    [Fact]
    public void WifiAgentDoesNotChangeEnergyOnBatteryOrWeakSignal()
    {
        var gateway = MetricSummary.From(new double?[] { 2, 20, 2, 20, 2, 20, 2, 20 });
        var battery = RealtimeOptimizationAgent.Decide(new WifiAgentObservation(
            true, 80, false, gateway, false, true, 2, false));
        var weakSignal = RealtimeOptimizationAgent.Decide(new WifiAgentObservation(
            true, 45, true, gateway, false, true, 2, false));

        Assert.False(battery.ShouldApplyMaximumPerformance);
        Assert.Contains("bateria", battery.Message);
        Assert.False(weakSignal.ShouldApplyMaximumPerformance);
        Assert.Contains("sinal Wi-Fi", weakSignal.Message);
    }

    [Fact]
    public void WifiAgentDoesNotTreatTrafficSpikeAsAdapterPowerProblem()
    {
        var gateway = MetricSummary.From(new double?[] { 2, 20, 2, 20, 2, 20, 2, 20 });
        var decision = RealtimeOptimizationAgent.Decide(new WifiAgentObservation(
            true, 80, true, gateway, true, true, 2, false));

        Assert.False(decision.ShouldApplyMaximumPerformance);
        Assert.Contains("pico de tráfego", decision.Message);
    }
}
