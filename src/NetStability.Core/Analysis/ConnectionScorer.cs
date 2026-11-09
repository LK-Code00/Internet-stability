using NetStability.Core.Models;

namespace NetStability.Core.Analysis;

public sealed record ConnectionScore(int Value, string Label, string Explanation);

/// <summary>A transparent heuristic for this short sample window, not an ISP or game SLA.</summary>
public static class ConnectionScorer
{
    public static ConnectionScore Score(MetricSummary gateway, MetricSummary internet)
    {
        if (gateway.Received == 0 && internet.Received == 0)
            return new ConnectionScore(0, "Sem dados", "Os destinos não responderam a ICMP.");

        var score = 100;
        if (gateway.Received > 0 && gateway.LossPercent > 0) score -= (int)Math.Ceiling(Math.Min(45, gateway.LossPercent * 8));
        if (internet.Received > 0 && internet.LossPercent > 0) score -= (int)Math.Ceiling(Math.Min(35, internet.LossPercent * 5));
        if (gateway.JitterMs is > 5) score -= (int)Math.Min(25, (gateway.JitterMs.Value - 5) * 2);
        if (internet.JitterMs is > 15) score -= (int)Math.Min(25, (internet.JitterMs.Value - 15) * 0.7);
        if (gateway.AverageMs is > 20) score -= (int)Math.Min(15, gateway.AverageMs.Value - 20);

        score = Math.Clamp(score, 0, 100);
        var label = internet.Received == 0 ? $"Parcial · {score}/100" : score >= 85 ? "Estável" : score >= 65 ? "Atenção" : "Instável";
        var explanation = internet.Received == 0
            ? "Só foi possível pontuar o enlace local; o destino externo não respondeu, então a qualidade fim a fim é desconhecida."
            : score >= 85
            ? "Pouca perda e variação nesta janela curta. Continue monitorando durante a partida."
            : "Pontuação heurística baseada em perda e variação observadas nesta janela; confirme por mais tempo.";
        return new ConnectionScore(score, label, explanation);
    }
}

public static class FindingAnalyzer
{
    public static bool ShouldRestoreDefaultTcpAutotuning(string? level) =>
        level is not null && (level.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("highlyrestricted", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("restricted", StringComparison.OrdinalIgnoreCase) ||
                              level.Equals("experimental", StringComparison.OrdinalIgnoreCase));

    public static List<string> Analyze(DiagnosticReport report)
    {
        var findings = new List<string>();
        var local = report.GatewayMetrics;
        var internet = report.InternetMetrics;

        if (local.Received == 0)
            findings.Add("O gateway não respondeu a ICMP. Isso pode indicar bloqueio de ICMP; confirme com outro dispositivo ou cabo antes de concluir que há perda real.");
        else if (local.LossPercent > 0)
            findings.Add($"Há {local.LossPercent:F1}% de sondas ICMP sem resposta até o gateway. Se persistir por vários minutos, investigue Wi-Fi, cabo, driver ou roteador.");

        if (local.JitterMs is > 8)
            findings.Add($"A variação média entre respostas ao gateway foi {local.JitterMs:F1} ms; o problema parece estar no enlace local ou no agendamento do PC.");

        if (internet.Received == 0)
            findings.Add("O destino externo não respondeu a ICMP. Alguns hosts e VPNs filtram ping; o resultado externo é inconclusivo.");
        else if (internet.LossPercent > local.LossPercent)
            findings.Add("A perda aparece depois do gateway com mais frequência que no enlace local. Verifique VPN, provedor e rota externa; o app não pode corrigir o servidor do jogo.");

        if (internet.JitterMs is > 25 && (local.JitterMs ?? 0) < 8)
            findings.Add("A latência varia no destino externo enquanto o gateway permanece relativamente estável. Isso aponta para VPN, acesso do provedor, rota ou destino, sem identificar sozinho qual deles.");

        if (report.GameTarget is not null)
        {
            var game = report.GameMetrics;
            if (game.Received == 0)
                findings.Add("O alvo de jogo configurado não respondeu a ICMP. Muitos servidores filtram ping; use um endereço que aceite sondas ou interprete esta métrica como inconclusiva.");
            else if (game.LossPercent > 0)
                findings.Add($"O alvo de jogo teve {game.LossPercent:F1}% de sondas ICMP sem resposta. Isso não prova perda dos pacotes do jogo, que podem usar outra porta/protocolo.");
            if (game.JitterMs is > 25)
                findings.Add($"A variação média de RTT até o alvo de jogo foi {game.JitterMs:F1} ms nesta janela.");
        }

        if (report.WifiSignal is < 60)
            findings.Add($"O sinal Wi-Fi reportado pelo Windows é {report.WifiSignal}%. Teste mais perto do roteador ou compare com Ethernet; esse percentual não mede ruído nem perda de rádio diretamente.");
        if (report.SameChannelNetworks is >= 5)
            findings.Add($"Foram vistos {report.SameChannelNetworks} BSSIDs no mesmo canal Wi-Fi. Isso sugere disputa de airtime, mas não comprova interferência; teste outro canal/banda no roteador.");

        if (ShouldRestoreDefaultTcpAutotuning(report.TcpAutoTuningLevel))
            findings.Add("O TCP receive-window autotuning difere do modo Normal. Restaurar o padrão suportado pelo Windows pode ajudar transferências TCP; não promete alterar o ping de jogos.");

        if (report.DnsLookupMs is > 1000)
            findings.Add($"A resolução DNS levou {report.DnsLookupMs:F0} ms. DNS lento afeta consultas de nomes e início de conexões; não reduz o RTT de uma sessão de jogo já estabelecida.");

        if (!string.IsNullOrWhiteSpace(report.VpnAdapters))
            findings.Add($"Adaptador de túnel possivelmente ativo: {report.VpnAdapters}. Compare as medições com a VPN ligada e desligada, respeitando a política do serviço; este diagnóstico não altera nem contorna a VPN.");

        if (report.MtuProbeResult.Contains("possível", StringComparison.OrdinalIgnoreCase))
            findings.Add("A sonda IPv4 com DF respondeu a pacotes menores, mas não à carga de 1472 bytes. MTU de caminho reduzida e filtragem de ICMP grande são ambas explicações possíveis; não altere a MTU só com este resultado.");

        if (findings.Count == 0)
            findings.Add("Nenhum gargalo claro apareceu nesta amostra. Uma janela curta sem perda não garante estabilidade durante partidas; monitore enquanto joga.");

        return findings;
    }
}
