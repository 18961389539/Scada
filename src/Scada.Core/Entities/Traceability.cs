using Scada.Core.Enums;

namespace Scada.Core.Entities;

public enum TraceabilitySnSource { Plc = 0, WebScan = 1, Tcp = 2, Serial = 3 }
public enum TraceabilitySnFormat { Numeric = 0, AsciiLowByteFirst = 1, AsciiHighByteFirst = 2 }
public enum TraceabilityTcpMode { Client = 0, Server = 1 }
public enum TraceabilityScanTerminator { CrLf = 0, Lf = 1, Cr = 2 }
public enum TraceabilityScanEncoding { Utf8 = 0, Ascii = 1 }

public sealed record TraceabilityScannerOptions
{
    public TraceabilityTcpMode TcpMode { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 9004;
    public string SerialPort { get; init; } = "COM1";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public SerialParity Parity { get; init; }
    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;
    public TraceabilityScanTerminator Terminator { get; init; }
    public TraceabilityScanEncoding Encoding { get; init; }
}

public sealed class TraceabilityJob
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public int TriggerTagId { get; set; }
    public TraceabilitySnSource SnSource { get; set; }
    public TraceabilitySnFormat SnFormat { get; set; }
    public string SnTagIdsJson { get; set; } = "[]";
    public string ScannerOptionsJson { get; set; } = "{}";
    public string FieldsJson { get; set; } = "[]";
    public int ScanExpirySeconds { get; set; } = 300;
    public long Revision { get; set; } = 1;
}

public sealed record TraceabilityField(string Name, int TagId);
public sealed record TraceabilityValue(string Name, int TagId, double Value, string? Unit);

public sealed class TraceabilityRecord
{
    public long Id { get; set; }
    public Guid CycleId { get; set; }
    public int JobId { get; set; }
    public int AreaId { get; set; }
    public string JobName { get; set; } = "";
    public string Sn { get; set; } = "";
    public long Ts { get; set; }
    public string ValuesJson { get; set; } = "[]";
    public bool Acknowledged { get; set; }
}

public sealed class TraceabilityAttempt
{
    public Guid Id { get; set; }
    public int JobId { get; set; }
    public long StartedTs { get; set; }
    public long? ClosedTs { get; set; }
    public int Reply { get; set; }
    public bool Acknowledged { get; set; }
    public string? Error { get; set; }
}