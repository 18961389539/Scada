namespace Scada.Core.Options;

public sealed class ScadaSettings
{
    public string ConfigDbPath { get; set; } = "data/scada.db";
    public string HistoryRoot { get; set; } = "data/history";
    public string BackupRoot { get; set; } = "data/backups";
    public string? OpcPkiRoot { get; set; }
    public int HistoryRetentionMonths { get; set; } = 2;
    public int Agg5MinRetentionDays { get; set; } = 365;
    public int BackupRetentionDays { get; set; } = 30;
    public int ArchiveFlushIntervalMs { get; set; } = 1000;
    public int ArchiveBatchSize { get; set; } = 2000;
    public int PushIntervalMs { get; set; } = 500;
    public int CommandTimeoutMs { get; set; } = 5000;
    public int ConfigRefreshSeconds { get; set; } = 10;
    public bool SeedDemoData { get; set; }
    public bool AggregationEnabled { get; set; } = true;
}
