namespace Scada.Core.Protocols;

/// <summary>设备参数表单元数据：字段、默认端口与地址提示。前端按 LabelKey/HelpKey 本地化，字面量 Label 保持原样。</summary>
public sealed record ProtocolFormField(
    string Key,
    string Type,
    object Default,
    string? LabelKey = null,
    string? Label = null,
    IReadOnlyList<string>? Options = null,
    double? Min = null,
    double? Max = null,
    string? PlaceholderKey = null,
    string? Placeholder = null,
    string? HelpKey = null,
    string? Help = null,
    bool Locked = false);

public sealed record ProtocolFormSpec(
    int DefaultPort,
    IReadOnlyList<ProtocolFormField> Fields,
    string AddressPlaceholder,
    string? AddressPlaceholderKey = null,
    string? AddressHelpKey = null,
    string? NoticeKey = null,
    string? LabelKey = null,
    string? Label = null);

internal static class FormField
{
    public static ProtocolFormField Text(string key, string @default, string? labelKey = null, string? label = null,
        string? placeholderKey = null, string? placeholder = null, string? helpKey = null) =>
        new(key, "text", @default, LabelKey: labelKey, Label: label, PlaceholderKey: placeholderKey, Placeholder: placeholder, HelpKey: helpKey);

    public static ProtocolFormField Number(string key, int @default, string? labelKey = null, string? label = null,
        double? min = null, double? max = null, string? helpKey = null) =>
        new(key, "number", @default, LabelKey: labelKey, Label: label, Min: min, Max: max, HelpKey: helpKey);

    public static ProtocolFormField Select(string key, string @default, string[] options, string? labelKey = null, string? label = null, string? helpKey = null) =>
        new(key, "select", @default, LabelKey: labelKey, Label: label, Options: options, HelpKey: helpKey);

    public static ProtocolFormField Bool(string key, bool @default, string? labelKey = null, string? label = null, string? helpKey = null, bool locked = false) =>
        new(key, "bool", @default, LabelKey: labelKey, Label: label, HelpKey: helpKey, Locked: locked);
}

/// <summary>各 IProtocolSpec 表单定义。字段键与协议 Options 属性（camelCase）对齐。</summary>
public static class ProtocolForms
{
    public static ProtocolFormSpec Mqtt { get; } = new(
        DefaultPort: 1883,
        Fields:
        [
            FormField.Text("clientId", "", labelKey: "protocols.k920416", placeholderKey: "protocols.k01f463"),
            FormField.Select("version", "V500", ["V500", "V311"], labelKey: "protocols.k5793c2"),
            FormField.Bool("useTls", false, label: "TLS"),
            FormField.Bool("allowUntrustedCertificate", false, labelKey: "protocols.k91ae87", helpKey: "protocols.ka4a388"),
            FormField.Bool("allowPlaintextPassword", false, labelKey: "protocols.k659c62", helpKey: "protocols.kcf8baf"),
            FormField.Number("keepAliveSec", 30, labelKey: "protocols.kf01fbd", min: 5, max: 3600),
            FormField.Number("qos", 1, label: "QoS", min: 0, max: 2),
            FormField.Bool("cleanSession", true, labelKey: "protocols.kefcec3"),
            FormField.Bool("retainWrites", false, labelKey: "protocols.kb64e3b"),
            FormField.Bool("confirmWrites", true, labelKey: "protocols.kfbdf65", helpKey: "protocols.k16f1af"),
            FormField.Number("staleAfterMs", 0, labelKey: "protocols.k6e99fa", min: 0, max: 86400000, helpKey: "protocols.kade0e0"),
        ],
        AddressPlaceholder: "plant/boiler|$.temp;w=plant/boiler/set;wpath=$.value;ts=$.ts",
        AddressHelpKey: "protocols.kc378f3",
        LabelKey: "protocols.k85cc9d");

    public static ProtocolFormSpec SparkplugB { get; } = new(
        DefaultPort: 1883,
        Fields:
        [
            FormField.Text("groupId", "", label: "Group ID"),
            FormField.Text("edgeNodeId", "", label: "Edge Node ID"),
            FormField.Text("deviceId", "", label: "Device ID", placeholderKey: "protocols.k0b8f28"),
            FormField.Text("primaryHostId", "", label: "Primary Host ID", helpKey: "protocols.k2f164b"),
            FormField.Bool("requestRebirth", true, labelKey: "protocols.k0d723b"),
            FormField.Bool("confirmWrites", true, labelKey: "protocols.kd4cb51"),
            FormField.Text("clientId", "", labelKey: "protocols.k920416", placeholderKey: "protocols.k01f463"),
            FormField.Select("version", "V500", ["V500", "V311"], labelKey: "protocols.k5793c2"),
            FormField.Bool("useTls", false, label: "TLS"),
            FormField.Bool("allowUntrustedCertificate", false, labelKey: "protocols.k91ae87", helpKey: "protocols.ka4a388"),
            FormField.Bool("allowPlaintextPassword", false, labelKey: "protocols.k659c62", helpKey: "protocols.kcf8baf"),
            FormField.Number("keepAliveSec", 30, labelKey: "protocols.kf01fbd", min: 5, max: 3600),
        ],
        AddressPlaceholder: "Inputs/Temperature",
        AddressHelpKey: "protocols.k081bc9",
        Label: "MQTT Sparkplug B");

    public static ProtocolFormSpec MitsubishiMc { get; } = new(
        DefaultPort: 5000,
        Fields:
        [
            FormField.Select("engine", "Native", ["Native", "IotClient"], labelKey: "protocols.k07a4d4", helpKey: "protocols.k92fb17"),
            FormField.Select("format", "Binary", ["Binary", "Ascii"], labelKey: "protocols.k453f47"),
            FormField.Select("transport", "Tcp", ["Tcp", "Udp"], labelKey: "protocols.k14b6b3"),
            FormField.Number("networkNo", 0, labelKey: "protocols.ke5af88", min: 0, max: 255),
            FormField.Number("pcNo", 255, labelKey: "protocols.kd3ebc6", min: 0, max: 255),
            FormField.Number("moduleIo", 1023, labelKey: "protocols.kc9270d", min: 0, max: 65535, helpKey: "protocols.ke2bf38"),
            FormField.Number("moduleStation", 0, labelKey: "protocols.k338ed2", min: 0, max: 255),
            FormField.Number("monitoringTimer", 8, labelKey: "protocols.k30f7f3", min: 0, max: 65535),
            FormField.Bool("highWordFirst", false, labelKey: "protocols.kbcb4ea", helpKey: "protocols.k5250f0"),
            FormField.Number("maxGap", 16, labelKey: "protocols.k7c5907", min: 0, max: 256),
        ],
        AddressPlaceholder: "D100、D100.3、M10、X1F、W1A0、ZR1000",
        AddressHelpKey: "protocols.k947bf7",
        NoticeKey: "protocols.k31ab95",
        LabelKey: "protocols.kc2fc66");

    public static ProtocolFormSpec OmronFins { get; } = new(
        DefaultPort: 9600,
        Fields:
        [
            FormField.Select("transport", "Udp", ["Udp", "Tcp"], labelKey: "protocols.k14b6b3"),
            FormField.Number("destNetwork", 0, labelKey: "protocols.k5e32b3", min: 0, max: 127),
            FormField.Number("destNode", 0, labelKey: "protocols.kc8b52c", min: 0, max: 254, helpKey: "protocols.kbdd818"),
            FormField.Number("destUnit", 0, labelKey: "protocols.kcad17f", min: 0, max: 255, helpKey: "protocols.k45bc58"),
            FormField.Number("sourceNetwork", 0, labelKey: "protocols.kc676ea", min: 0, max: 127),
            FormField.Number("sourceNode", 0, labelKey: "protocols.kd107dc", min: 0, max: 254, helpKey: "protocols.kf38242"),
            FormField.Bool("highWordFirst", false, labelKey: "protocols.kbcb4ea"),
            FormField.Number("maxGap", 16, labelKey: "protocols.k7c5907", min: 0, max: 256),
        ],
        AddressPlaceholder: "D100、CIO10.05、W3.00、H10、A100、T5、CNT5",
        AddressHelpKey: "protocols.ka9c0bd",
        LabelKey: "protocols.k589dd4");

    public static ProtocolFormSpec Iec104 { get; } = new(
        DefaultPort: 2404,
        Fields:
        [
            FormField.Number("commonAddress", 1, labelKey: "protocols.ka8738c", min: 1, max: 65534),
            FormField.Number("originatorAddress", 0, labelKey: "protocols.k4baa03", min: 0, max: 255),
            FormField.Select("commandMode", "SelectExecute", ["SelectExecute", "Direct"], labelKey: "protocols.k40f8dd", helpKey: "protocols.k58d70d"),
            FormField.Bool("waitActTerm", true, labelKey: "protocols.kb15ee7", helpKey: "protocols.k65b800"),
            FormField.Number("giIntervalSec", 900, labelKey: "protocols.k6bd1d8", min: 0, max: 86400, helpKey: "protocols.k120d54"),
            FormField.Number("counterIntervalSec", 0, labelKey: "protocols.kc59dea", min: 0, max: 86400),
            FormField.Bool("clockSync", true, labelKey: "protocols.k138e54"),
            FormField.Bool("deviceTimeUtc", true, labelKey: "protocols.k941e2f"),
            FormField.Number("t1Sec", 15, label: "t1 (s)", min: 1, max: 255),
            FormField.Number("t2Sec", 10, label: "t2 (s)", min: 1, max: 255),
            FormField.Number("t3Sec", 20, label: "t3 (s)", min: 1, max: 172800),
            FormField.Number("k", 12, label: "k", min: 1, max: 32767),
            FormField.Number("w", 8, label: "w", min: 1, max: 32767),
        ],
        AddressPlaceholder: "",
        AddressPlaceholderKey: "protocols.kf6e9ef",
        AddressHelpKey: "protocols.k292a6e",
        NoticeKey: "protocols.k078f54",
        LabelKey: "protocols.k1f8f6b");

    public static ProtocolFormSpec BacnetIp { get; } = new(
        DefaultPort: 47808,
        Fields:
        [
            FormField.Number("deviceInstance", 0, labelKey: "protocols.k9b82e7", min: 0, max: 4194302, helpKey: "protocols.k0ded08"),
            FormField.Bool("whoIs", true, labelKey: "protocols.k1be3a9", helpKey: "protocols.ka3015e"),
            FormField.Number("writePriority", 8, labelKey: "protocols.k5d6d59", min: 1, max: 16, helpKey: "protocols.ka31759"),
            FormField.Bool("readMultiple", true, label: "ReadPropertyMultiple", helpKey: "protocols.k7f4060"),
            FormField.Number("maxObjectsPerRequest", 16, labelKey: "protocols.k0423a0", min: 1, max: 64),
            FormField.Number("readRetries", 1, labelKey: "protocols.k115c35", min: 0, max: 3, helpKey: "protocols.k29e72b"),
            FormField.Bool("useCov", false, labelKey: "protocols.k3d9997", helpKey: "protocols.kb2cc87"),
            FormField.Number("covLifetimeSec", 300, labelKey: "protocols.k55e18f", min: 60, max: 28800),
            FormField.Number("localPort", 0, labelKey: "protocols.ke1df15", min: 0, max: 65535, helpKey: "protocols.keba495"),
        ],
        AddressPlaceholder: "AI:1、AV:10;pri=12、BO:3、MSV:2",
        AddressHelpKey: "protocols.kad2c9a",
        NoticeKey: "protocols.ke1ab45",
        Label: "BACnet/IP");

    public static ProtocolFormSpec OpcDa { get; } = new(
        DefaultPort: 0,
        Fields:
        [
            FormField.Text("progId", "", labelKey: "protocols.k581c5d", helpKey: "protocols.kdad167"),
            FormField.Text("node", "", labelKey: "protocols.k323319", helpKey: "protocols.kac1e80"),
            FormField.Bool("readFromDevice", false, labelKey: "protocols.k2e0e82", helpKey: "protocols.kabe258"),
            FormField.Number("updateRateMs", 1000, labelKey: "protocols.k69a9cf", min: 50, max: 3600000),
            FormField.Bool("timestampsUtc", false, labelKey: "protocols.k48e3f3", helpKey: "protocols.kfd8ead"),
        ],
        AddressPlaceholder: "Channel1.Device1.Tag1",
        AddressHelpKey: "protocols.k248d7c",
        NoticeKey: "protocols.k61622d",
        LabelKey: "protocols.k0c887c");

    public static ProtocolFormSpec Iec61850 { get; } = new(
        DefaultPort: 102,
        Fields:
        [
            FormField.Bool("useReports", false, labelKey: "protocols.kcf36a3", helpKey: "protocols.reportsUnread", locked: true),
        ],
        AddressPlaceholder: "IED1LD0/MMXU1.MX.TotW.mag.f",
        AddressHelpKey: "protocols.k1d54e4",
        NoticeKey: "protocols.k96f300",
        LabelKey: "protocols.kfaee37");

    public static ProtocolFormSpec EtherNetIp { get; } = new(
        DefaultPort: 44818,
        Fields:
        [
            FormField.Select("plcType", "ControlLogix",
                ["ControlLogix", "CompactLogix", "Micro800", "Slc500", "Plc5", "LogixPccc", "MicroLogix", "Omron"],
                labelKey: "protocols.ka7495b"),
            FormField.Text("path", "1,0", labelKey: "protocols.k6f8319", placeholder: "1,0", helpKey: "protocols.k19b7ff"),
            FormField.Bool("allowPacking", true, labelKey: "protocols.k7589db"),
        ],
        AddressPlaceholder: "MyTag、MyArray[3]、Program:MainProgram.MyTag、N7:4",
        AddressHelpKey: "protocols.k17a145",
        NoticeKey: "protocols.k0ba872",
        Label: "EtherNet/IP");
}
