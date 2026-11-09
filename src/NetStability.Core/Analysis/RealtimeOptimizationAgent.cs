using NetStability.Core.Models;

namespace NetStability.Core.Analysis;

public sealed record WifiAgentObservation(
    bool IsWifi,
    int? SignalPercent,
    bool IsOnAcPower,
    MetricSummary Gateway,
    bool TrafficSpike,
    bool DegradationIsPersistent,
    int? CurrentAcPowerMode,
    bool HasPendingRestore);

public sealed record WifiAgentDecision(bool ShouldApplyMaximumPerformance, string Message);

/// <summary>
/// Local, deterministic decision agent. It never sends observations outside this process.
/// Its small action set is intentionally limited to a reversible Wi-Fi AC power-plan setting.
/// </summary>
public static class RealtimeOptimizationAgent
{
    public static WifiAgentDecision Decide(WifiAgentObservation observation)
    {
        if (!observation.IsWifi)
            return Wait("A conexão ativa não é Wi-Fi. Nenhum ajuste sem fio será feito.");
        if (observation.Gateway.Sent < 6 || observation.Gateway.Received < 5)
            return Wait("Coletando amostras suficientes do roteador antes de decidir.");
        if (observation.SignalPercent is null or < 60)
            return Wait("O sinal Wi-Fi está fraco ou não foi medido. Aproxime o PC do roteador ou teste Ethernet.");
        if (observation.TrafficSpike)
            return Wait("A variação coincidiu com um pico de tráfego. Pause downloads e observe antes de alterar o Wi-Fi.");

        var localDegradation = observation.Gateway.LossPercent >= 5 || observation.Gateway.JitterMs >= 10;
        if (!localDegradation || !observation.DegradationIsPersistent)
            return Wait("O enlace até o roteador está estável ou a oscilação ainda não persistiu. Continuo observando.");
        if (!observation.IsOnAcPower)
            return Wait("A degradação local persistiu, mas o PC está na bateria. Não alterei o perfil para preservar autonomia.");
        if (observation.HasPendingRestore)
            return Wait("Já existe um ajuste Wi-Fi deste app aguardando restauração. Mantive o perfil atual.");
        if (observation.CurrentAcPowerMode is null)
            return Wait("O Windows/driver não expôs o perfil de energia Wi-Fi. Nenhum ajuste foi feito.");
        if (observation.CurrentAcPowerMode == 0)
            return Wait("O Wi-Fi já está em Máximo desempenho na tomada. Energia não parece ser a causa.");

        return new WifiAgentDecision(true,
            $"Detectei degradação persistente até o roteador (jitter {observation.Gateway.JitterMs:F1} ms, perda {observation.Gateway.LossPercent:F1}%). Vou pedir autorização para priorizar a estabilidade do Wi-Fi na tomada.");
    }

    private static WifiAgentDecision Wait(string message) => new(false, message);
}
