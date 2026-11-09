using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NetStability.App.Services;
using NetStability.Core.Analysis;
using NetStability.Core.Models;

namespace NetStability.App;

public partial class MainWindow : Window
{
    private const int DiagnosticRounds = 8;
    private const int SampleCapacity = 120;
    private readonly NetworkSnapshotService _snapshotService = new();
    private readonly ProbeService _probeService = new();
    private readonly DnsDiagnosticService _dnsService = new();
    private readonly PathDiagnosticsService _pathDiagnostics = new();
    private readonly AdminChangeService _adminChangeService = new();
    private readonly StateStore _store = new();
    private readonly ObservableCollection<ProbeSample> _samples = [];
    private readonly ObservableCollection<DiagnosticReport> _history = [];
    private readonly ObservableCollection<string> _liveFindings = [];
    private NetworkSnapshot _snapshot = NetworkSnapshot.Empty;
    private AppSettings _settings = new("1.1.1.1", "");
    private string? _displayGameTarget;
    private CancellationTokenSource? _monitorCancellation;
    private DiagnosticReport? _latestReport;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        FindingsList.ItemsSource = _liveFindings;
        HistoryGrid.ItemsSource = _history;
        _settings = _store.LoadSettings();
        ExternalTargetBox.Text = _settings.ExternalTarget;
        GameTargetBox.Text = _settings.GameTarget;
        foreach (var report in _store.LoadHistory().OrderByDescending(item => item.StartedAt)) _history.Add(report);
        RestoreButton.IsEnabled = _store.HasBackupFile;
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => StopMonitoring();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _snapshot = await _snapshotService.ReadAsync();
            UpdateAdapterSummary();
            UpdateAdvancedDetails();
            if (_history.Count > 0)
            {
                _latestReport = _history[0];
                _displayGameTarget = _latestReport.GameTarget;
                _liveFindings.Clear();
                foreach (var item in _latestReport.Findings) _liveFindings.Add(item);
                UpdateDashboard(_latestReport.Samples);
                UpdateAdvancedDetails();
            }
            StatusText.Text = "Pronto. Uma medição curta não substitui o acompanhamento durante a partida.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível ler todos os dados do adaptador: {exception.Message}";
        }
    }

    private async void DiagnoseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StopMonitoring();
        SetBusy(true);
        try
        {
            var report = await RunDiagnosticCoreAsync();
            _latestReport = report;
            StatusText.Text = "Diagnóstico concluído. Consulte os achados e o histórico.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Diagnóstico cancelado."; }
        catch (Exception exception) { StatusText.Text = $"Falha no diagnóstico: {exception.Message}"; }
        finally { SetBusy(false); }
    }

    private async Task<DiagnosticReport> RunDiagnosticCoreAsync()
    {
        _samples.Clear();
        _liveFindings.Clear();
        _snapshot = await _snapshotService.ReadAsync();
        UpdateAdapterSummary();
        UpdateAdvancedDetails();
        var external = _settings.ExternalTarget.Trim();
        var game = string.IsNullOrWhiteSpace(_settings.GameTarget) ? null : _settings.GameTarget.Trim();
        _displayGameTarget = game;
        var dnsLatency = await _dnsService.MeasureSystemResolverAsync(CancellationToken.None);
        var samples = new List<ProbeSample>(DiagnosticRounds);
        var traffic = new TrafficSampler();
        var started = DateTimeOffset.Now;
        var historyWriteFailed = false;

        for (var index = 0; index < DiagnosticRounds; index++)
        {
            FooterText.Text = $"Diagnóstico: amostra {index + 1}/{DiagnosticRounds} • gateway + {external}" + (game is null ? "" : $" + {game}");
            var sample = await _probeService.ProbeCycleAsync(_snapshot, external, game, traffic, CancellationToken.None);
            samples.Add(sample);
            _samples.Add(sample);
            UpdateDashboard(_samples);
            if (index < DiagnosticRounds - 1) await Task.Delay(500);
        }

        FooterText.Text = "Medições concluídas • verificando PMTU por ICMP e registrando os primeiros saltos da rota...";
        var mtuTask = _pathDiagnostics.CheckPathMtuAsync(external, CancellationToken.None);
        var routeTask = _pathDiagnostics.TraceRouteAsync(external, CancellationToken.None);
        await Task.WhenAll(mtuTask, routeTask);

        var report = new DiagnosticReport
        {
            StartedAt = started,
            FinishedAt = DateTimeOffset.Now,
            AdapterName = _snapshot.AdapterName,
            AdapterDescription = _snapshot.AdapterDescription,
            AdapterAdvancedSettings = _snapshot.AdapterAdvancedSettings,
            ConnectionType = _snapshot.ConnectionType,
            Gateway = _snapshot.Gateway,
            WifiSignal = _snapshot.WifiSignal,
            WifiChannel = _snapshot.WifiChannel,
            SameChannelNetworks = _snapshot.SameChannelNetworks,
            TcpAutoTuningLevel = _snapshot.TcpAutoTuningLevel,
            VpnAdapters = _snapshot.VpnAdapters,
            Mtu = _snapshot.Mtu,
            DnsServers = _snapshot.DnsServers,
            DnsLookupMs = dnsLatency,
            MtuProbeResult = mtuTask.Result,
            RouteTrace = routeTask.Result,
            GameTarget = game,
            Samples = samples
        };
        report.Findings.AddRange(FindingAnalyzer.Analyze(report));
        if (dnsLatency is null)
            report.Findings.Add("A resolução do sistema não respondeu em 3 segundos. Firewall, indisponibilidade DNS ou política privada/DoH podem tornar o resultado inconclusivo.");
        report.Findings.Add("A rota bruta foi registrada no relatório exportável. Saltos intermediários sem resposta podem filtrar ICMP e não significam perda de tráfego encaminhado.");
        if (string.IsNullOrWhiteSpace(_snapshot.TcpAutoTuningLevel))
            report.Findings.Add("O nível de TCP autotuning não foi lido. Nenhum ajuste TCP poderá ser proposto com segurança.");

        _liveFindings.Clear();
        foreach (var finding in report.Findings) _liveFindings.Add(finding);
        _history.Insert(0, report);
        while (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        try { _store.SaveHistory(_history); }
        catch (Exception exception)
        {
            var warningText = $"Não foi possível persistir o histórico local: {exception.Message}";
            report.Findings.Add(warningText);
            _liveFindings.Add(warningText);
            historyWriteFailed = true;
        }
        _latestReport = report;
        UpdateDashboard(_samples);
        UpdateAdvancedDetails();
        FooterText.Text = historyWriteFailed
            ? "Diagnóstico concluído • não foi possível gravar o histórico neste computador"
            : "Diagnóstico concluído • ICMP pode ser filtrado • tráfego IPv4 passivo não é speed test";
        return report;
    }

    private async void MonitorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitorCancellation is not null)
        {
            StopMonitoring();
            StatusText.Text = "Monitoramento parado.";
            return;
        }

        try
        {
            _snapshot = await _snapshotService.ReadAsync();
            UpdateAdapterSummary();
            _samples.Clear();
            _displayGameTarget = string.IsNullOrWhiteSpace(_settings.GameTarget) ? null : _settings.GameTarget.Trim();
            _monitorCancellation = new CancellationTokenSource();
            MonitorButton.Content = "Parar monitoramento";
            StatusText.Text = "Monitorando gateway, destino externo e servidor de jogo configurado.";
            _ = MonitorLoopAsync(_monitorCancellation.Token);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Não foi possível iniciar o monitoramento: {exception.Message}";
            StopMonitoring();
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        var traffic = new TrafficSampler();
        double? previousInternet = null;
        var external = _settings.ExternalTarget.Trim();
        var game = string.IsNullOrWhiteSpace(_settings.GameTarget) ? null : _settings.GameTarget.Trim();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sample = await _probeService.ProbeCycleAsync(_snapshot, external, game, traffic, cancellationToken);
                var preceding = _samples.TakeLast(12).ToArray();
                _samples.Add(sample);
                while (_samples.Count > SampleCapacity) _samples.RemoveAt(0);
                UpdateDashboard(_samples);

                if (sample.InternetMs is null)
                    SetAlert("O destino externo não respondeu a ICMP nesta amostra; confirme com outro serviço ou destino.");
                else if (preceding.Length >= 6 && sample.InternetMs >= Median(preceding.Where(item => item.InternetMs.HasValue).Select(item => item.InternetMs!.Value)) + 25 &&
                         TrafficAboveRecentBaseline(sample, preceding))
                    SetAlert("Suspeita de aumento de latência correlacionado com tráfego do adaptador. Isso pode indicar fila/congestionamento, mas não confirma bufferbloat nem localiza o gargalo; teste latência sob carga no roteador.");
                else if (previousInternet is not null && sample.InternetMs - previousInternet.Value >= 50)
                    SetAlert($"A latência externa subiu {sample.InternetMs - previousInternet.Value:F0} ms desde a amostra anterior.");
                else if (sample.GatewayMs is null)
                    SetAlert("O gateway não respondeu a ICMP; isso pode ser filtragem ou degradação do enlace local.");
                previousInternet = sample.InternetMs;
                FooterText.Text = $"Monitoramento ativo • última amostra {sample.At:HH:mm:ss} • destino {external}";
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { SetAlert($"Monitoramento interrompido: {exception.Message}"); }
        finally
        {
            if (_monitorCancellation?.Token == cancellationToken)
            {
                _monitorCancellation.Dispose();
                _monitorCancellation = null;
                MonitorButton.Content = "Iniciar monitoramento";
            }
        }
    }

    private async void OptimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StopMonitoring();
        SetBusy(true);
        try
        {
            var backup = _store.LoadBackup();
            if (backup is not null)
                throw new InvalidOperationException("Há um backup de otimização pendente. Restaure-o primeiro para evitar sobrescrever a origem guardada.");

            StatusText.Text = "Registrando a condição anterior...";
            var before = await RunDiagnosticCoreAsync();

            var currentLevel = before.TcpAutoTuningLevel.Trim();
            if (FindingAnalyzer.ShouldRestoreDefaultTcpAutotuning(currentLevel))
            {
                var newBackup = new OptimizationBackup(currentLevel, "normal", DateTimeOffset.Now);
                _store.SaveBackup(newBackup);
                try
                {
                    StatusText.Text = "Solicitando privilégio administrativo para restaurar o padrão TCP...";
                    await _adminChangeService.SetTcpAutotuningAsync(currentLevel, "normal");
                    RestoreButton.IsEnabled = true;
                    StatusText.Text = "O TCP autotuning foi ajustado para Normal. Registrando nova medição...";
                }
                catch (OperationCanceledException)
                {
                    _store.DeleteBackup();
                    throw;
                }
                catch
                {
                    var stateAfterAttempt = await _snapshotService.ReadAsync();
                    if (stateAfterAttempt.TcpAutoTuningLevel.Equals(currentLevel, StringComparison.OrdinalIgnoreCase))
                        _store.DeleteBackup();
                    else
                        RestoreButton.IsEnabled = true;
                    throw;
                }
            }
            else
            {
                StatusText.Text = "Nenhuma alteração local foi justificada. Repetindo a medição para comparar a condição da rede.";
            }

            var after = await RunDiagnosticCoreAsync();
            _latestReport = after;
            ShowComparison(before, after);
        }
        catch (OperationCanceledException exception) { StatusText.Text = exception.Message; }
        catch (Exception exception) { StatusText.Text = $"A otimização não foi concluída: {exception.Message}"; }
        finally { SetBusy(false); }
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        StopMonitoring();
        SetBusy(true);
        try
        {
            var backup = _store.LoadBackup();
            if (backup is null)
            {
                StatusText.Text = "Não há alterações deste app para restaurar.";
                return;
            }
            await _adminChangeService.SetTcpAutotuningAsync(backup.AppliedLevel, backup.OriginalLevel);
            _store.DeleteBackup();
            RestoreButton.IsEnabled = false;
            StatusText.Text = $"TCP autotuning restaurado para {backup.OriginalLevel}.";
            _snapshot = await _snapshotService.ReadAsync();
            UpdateAdvancedDetails();
        }
        catch (Exception exception) { StatusText.Text = $"Não foi possível restaurar: {exception.Message}"; }
        finally { SetBusy(false); }
    }

    private void SaveTargetsButton_Click(object sender, RoutedEventArgs e)
    {
        var external = ExternalTargetBox.Text.Trim();
        var game = GameTargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(external))
        {
            StatusText.Text = "Informe um destino externo; use um IP ou nome DNS.";
            return;
        }
        _settings = new AppSettings(external, game);
        _store.SaveSettings(_settings);
        StatusText.Text = "Destinos salvos. Um monitoramento em andamento usa os destinos anteriores até reiniciar.";
    }

    private async void PowerQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (string.IsNullOrWhiteSpace(_snapshot.AdapterName) || _snapshot.AdapterName.StartsWith("(", StringComparison.Ordinal))
                throw new InvalidOperationException("Execute um diagnóstico para identificar o adaptador ativo primeiro.");
            PowerQueryResultText.Text = await _adminChangeService.ReadAdapterPowerManagementAsync(_snapshot.AdapterName);
        }
        catch (Exception exception) { PowerQueryResultText.Text = $"Consulta não concluída: {exception.Message}"; }
        finally { SetBusy(false); }
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not DiagnosticReport report)
        {
            StatusText.Text = "Selecione um diagnóstico no histórico para exportar.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Exportar relatório de rede",
            FileName = $"NetStability-{report.StartedAt:yyyyMMdd-HHmmss}.json",
            DefaultExt = ".json",
            Filter = "Relatório JSON (*.json)|*.json|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            StatusText.Text = $"Relatório exportado para {dialog.FileName}";
        }
        catch (Exception exception) { StatusText.Text = $"Falha ao exportar: {exception.Message}"; }
    }

    private void HistoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not DiagnosticReport report) return;
        var details = report.Findings.ToList();
        details.Insert(0, $"Propriedades avançadas do driver: {report.AdapterAdvancedSettings}");
        details.Insert(0, $"Adaptador: {report.AdapterName} ({report.ConnectionType}) • gateway: {report.Gateway} • MTU: {report.Mtu} bytes");
        details.Add($"DNS da interface física: {string.Join(", ", report.DnsServers)} • resolução do sistema: {Fmt(report.DnsLookupMs, " ms")}");
        details.Add($"Sonda MTU: {report.MtuProbeResult}");
        details.Add($"Rota externa:\n{report.RouteTrace}");
        if (!string.IsNullOrWhiteSpace(report.ComparisonSummary)) details.Insert(0, report.ComparisonSummary);
        ReportFindingsList.ItemsSource = details;
    }

    private void UpdateDashboard(IEnumerable<ProbeSample> samples)
    {
        var values = samples.ToArray();
        var usingGameTarget = !string.IsNullOrWhiteSpace(_displayGameTarget);
        JitterChart.UseGameTargetForJitter = usingGameTarget;
        JitterChartDescription.Text = usingGameTarget
            ? $"Jitter até {_displayGameTarget}; tráfego é leitura passiva IPv4."
            : "Jitter entre sondas externas; tráfego é leitura passiva IPv4.";
        PingChart.SetSamples(values);
        JitterChart.SetSamples(values);
        if (values.Length == 0) return;

        var latest = values[^1];
        var external = MetricSummary.From(values.Select(sample => sample.InternetMs));
        var gameMetrics = MetricSummary.From(values.Select(sample => sample.GameMs));
        var primary = usingGameTarget ? gameMetrics : external;
        var gateway = MetricSummary.From(values.Select(sample => sample.GatewayMs));
        CurrentPingLabel.Text = usingGameTarget ? "PING ATUAL · JOGO" : "PING ATUAL · EXTERNO";
        CurrentPingTargetText.Text = usingGameTarget ? _displayGameTarget! : "destino externo";
        AveragePingLabel.Text = usingGameTarget ? "MÉDIA / MÍN–MÁX · JOGO" : "MÉDIA / MÍN–MÁX · EXTERNO";
        CurrentPingText.Text = Fmt(usingGameTarget ? latest.GameMs : latest.InternetMs, " ms");
        AveragePingText.Text = Fmt(primary.AverageMs, " ms");
        RangePingText.Text = $"{Fmt(primary.MinimumMs, "")} – {Fmt(primary.MaximumMs, "")} ms";
        JitterText.Text = Fmt(primary.JitterMs, " ms");
        LossText.Text = $"{primary.LossPercent:F1}%";
        SpeedText.Text = $"↓ {Fmt(latest.DownloadMbps, " Mb/s")}  ↑ {Fmt(latest.UploadMbps, " Mb/s")}";
        GatewayStatusText.Text = latest.GatewayMs.HasValue
            ? $"Gateway {_snapshot.Gateway}: {latest.GatewayMs.Value:F0} ms"
            : $"Gateway {_snapshot.Gateway}: sem resposta ICMP";
        var score = ConnectionScorer.Score(gateway, external);
        QualityText.Text = $"{score.Label} · {score.Value}/100";
        QualityText.Foreground = score.Value >= 85 ? (System.Windows.Media.Brush)FindResource("Accent")
            : score.Value >= 65 ? (System.Windows.Media.Brush)FindResource("Warning")
            : (System.Windows.Media.Brush)FindResource("Danger");
        QualityExplanationText.Text = score.Explanation;
        WifiStatusText.Text = _snapshot.ConnectionType == "Wi-Fi"
            ? $"Wi-Fi • sinal Windows {_snapshot.WifiSignal?.ToString() ?? "—"}% • canal {_snapshot.WifiChannel?.ToString() ?? "—"} • BSSIDs no canal: {_snapshot.SameChannelNetworks?.ToString() ?? "—"}"
            : $"{_snapshot.ConnectionType} • link {_snapshot.LinkSpeed} • MTU IPv4 {_snapshot.Mtu}";
        DnsStatusText.Text = $"DNS da interface física: {string.Join(", ", _snapshot.DnsServers)}";
    }

    private void UpdateAdapterSummary()
    {
        AdapterSummaryText.Text = $"{_snapshot.AdapterName} • {_snapshot.ConnectionType} • gateway {_snapshot.Gateway} • link {_snapshot.LinkSpeed}";
    }

    private void UpdateAdvancedDetails()
    {
        AdvancedDetailsText.Text =
            $"Adaptador: {_snapshot.AdapterName}\nDescrição: {_snapshot.AdapterDescription}\nConexão: {_snapshot.ConnectionType} • link: {_snapshot.LinkSpeed}\n" +
            $"Propriedades avançadas do driver (somente leitura; rótulos dependem do fabricante): {_snapshot.AdapterAdvancedSettings}\n" +
            $"Índice: {_snapshot.InterfaceIndex} • gateway local: {_snapshot.Gateway}\nMTU IPv4 informada pelo Windows: {_snapshot.Mtu} bytes (sem ajuste automático; valide PMTU antes de alterar)\n" +
            $"TCP receive-window autotuning: {_snapshot.TcpAutoTuningLevel}\nDNS da interface física: {string.Join(", ", _snapshot.DnsServers)}\n" +
            $"Wi-Fi: sinal {_snapshot.WifiSignal?.ToString() ?? "—"}% • canal {_snapshot.WifiChannel?.ToString() ?? "—"} • tipo de rádio {_snapshot.RadioType} • BSSIDs no mesmo canal {_snapshot.SameChannelNetworks?.ToString() ?? "—"}\n" +
            $"Driver: {_snapshot.DriverVersion}\nGerenciamento de energia (NetAdapter): {_snapshot.PowerManagement}\n" +
            $"Interfaces VPN/túnel suspeitas: {_snapshot.VpnAdapters}" +
            (_latestReport is null ? "" : $"\nResultado da sonda MTU: {_latestReport.MtuProbeResult}\nRota para o destino externo:\n{_latestReport.RouteTrace}");
    }

    private void ShowComparison(DiagnosticReport before, DiagnosticReport after)
    {
        var summary = string.Join(Environment.NewLine, new[]
        {
            "Comparação antes/depois (janelas curtas, não prova causalidade):",
            $"Ping médio: {Fmt(before.InternetMetrics.AverageMs, " ms")} → {Fmt(after.InternetMetrics.AverageMs, " ms")} ({Delta(before.InternetMetrics.AverageMs, after.InternetMetrics.AverageMs)})",
            $"Jitter: {Fmt(before.InternetMetrics.JitterMs, " ms")} → {Fmt(after.InternetMetrics.JitterMs, " ms")} ({Delta(before.InternetMetrics.JitterMs, after.InternetMetrics.JitterMs)})",
            $"Perda ICMP: {before.InternetMetrics.LossPercent:F1}% → {after.InternetMetrics.LossPercent:F1}%",
            $"Gateway médio: {Fmt(before.GatewayMetrics.AverageMs, " ms")} → {Fmt(after.GatewayMetrics.AverageMs, " ms")}"
        });
        var compared = after with { ComparisonBaselineId = before.Id, ComparisonSummary = summary };
        var historyIndex = _history.IndexOf(after);
        if (historyIndex >= 0) _history[historyIndex] = compared;
        try { _store.SaveHistory(_history); }
        catch (Exception exception) { StatusText.Text = $"{summary}{Environment.NewLine}Comparação calculada, mas não foi possível salvar o histórico: {exception.Message}"; }
        _latestReport = compared;
        if (!StatusText.Text.StartsWith(summary, StringComparison.Ordinal)) StatusText.Text = summary;
        FooterText.Text = "Comparação exibida em Avançado • diferenças pequenas podem ser apenas variação natural da rede";
        _liveFindings.Insert(0, summary.Replace(Environment.NewLine, " | ", StringComparison.Ordinal));
        MainTabs.SelectedIndex = 2;
    }

    private void SetAlert(string message)
    {
        StatusText.Text = $"Alerta {DateTime.Now:HH:mm:ss}: {message}";
        FooterText.Text = $"Alerta de degradação • {message}";
    }

    private void StopMonitoring()
    {
        if (_monitorCancellation is null) return;
        _monitorCancellation.Cancel();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DiagnoseButton.IsEnabled = !busy;
        OptimizeButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy && _store.HasBackupFile;
        MonitorButton.IsEnabled = !busy;
        PowerQueryButton.IsEnabled = !busy;
    }

    private static string Fmt(double? value, string suffix) => value.HasValue ? $"{value.Value:F1}{suffix}" : "—";

    private static string Delta(double? before, double? after)
    {
        if (!before.HasValue || !after.HasValue) return "sem amostra suficiente";
        var delta = after.Value - before.Value;
        var direction = delta < 0 ? "menor" : delta > 0 ? "maior" : "igual";
        return $"{delta:+0.0;-0.0;0.0} ms, {direction}";
    }

    private static bool TrafficAboveRecentBaseline(ProbeSample current, IReadOnlyList<ProbeSample> preceding)
    {
        var currentRate = (current.DownloadMbps ?? 0) + (current.UploadMbps ?? 0);
        var recentRates = preceding.Select(item => (item.DownloadMbps ?? 0) + (item.UploadMbps ?? 0)).ToArray();
        return currentRate >= Math.Max(0.25, Median(recentRates) * 2);
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        if (values.Length == 0) return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) / 2 : values[middle];
    }
}
