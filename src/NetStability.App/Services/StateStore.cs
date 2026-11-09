using System.IO;
using System.Text.Json;
using NetStability.Core.Models;

namespace NetStability.App.Services;

public sealed record OptimizationBackup(string OriginalLevel, string AppliedLevel, DateTimeOffset CreatedAt);
public sealed record AppSettings(string ExternalTarget, string GameTarget);

public sealed class StateStore
{
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetStability");
    private string HistoryPath => Path.Combine(_directory, "history.json");
    private string BackupPath => Path.Combine(_directory, "optimization-backup.json");
    private string SettingsPath => Path.Combine(_directory, "settings.json");
    public bool HasBackupFile => File.Exists(BackupPath);

    public IReadOnlyList<DiagnosticReport> LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return [];
            return JsonSerializer.Deserialize<List<DiagnosticReport>>(File.ReadAllText(HistoryPath), _options) ?? [];
        }
        catch { return []; }
    }

    public void SaveHistory(IEnumerable<DiagnosticReport> reports)
    {
        WriteJson(HistoryPath, reports.Take(100).ToArray());
    }

    public OptimizationBackup? LoadBackup()
    {
        if (!File.Exists(BackupPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<OptimizationBackup>(File.ReadAllText(BackupPath), _options)
                   ?? throw new InvalidDataException("O backup está vazio.");
        }
        catch (Exception exception)
        {
            throw new InvalidDataException($"Não foi possível ler o backup de otimização em {BackupPath}. Preserve o arquivo e revise-o antes de fazer outra alteração.", exception);
        }
    }

    public void SaveBackup(OptimizationBackup backup)
    {
        WriteJson(BackupPath, backup);
    }

    public void DeleteBackup()
    {
        if (File.Exists(BackupPath)) File.Delete(BackupPath);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), _options) ?? new AppSettings("1.1.1.1", "")
                : new AppSettings("1.1.1.1", "");
        }
        catch { return new AppSettings("1.1.1.1", ""); }
    }

    public void SaveSettings(AppSettings settings)
    {
        WriteJson(SettingsPath, settings);
    }

    private void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, _options));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }
}
