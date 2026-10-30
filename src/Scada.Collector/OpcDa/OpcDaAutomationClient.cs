using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Scada.Collector.OpcDa;

/// <summary>
/// 通过 OPC Foundation 自动化包装器（ProgID <c>OPC.Automation.1</c>，OPCDAAuto.dll，需在 Windows 上单独安装并注册）后期绑定访问 OPC DA 服务器。
/// 未在真实 Windows 环境验证。Read/Write 由 <see cref="OpcDaDriver"/> 的 <c>_io</c> 串行执行，Connect/Close 不受该锁保护。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OpcDaAutomationClient : IOpcDaClient
{
    private const short SourceCache = 1, SourceDevice = 2;
    private dynamic? _server;
    private dynamic? _group;
    private readonly Dictionary<string, int> _handles = new(StringComparer.Ordinal);
    private int _nextClientHandle = 1;

    public bool Connected { get; private set; }

    public void Connect(string progId, string? node, int updateRateMs)
    {
        var type = Type.GetTypeFromProgID("OPC.Automation.1")
            ?? throw new InvalidOperationException("未安装 OPC 自动化包装器（OPC.Automation.1 / OPCDAAuto.dll），请先安装 OPC Core Components");
        _server = Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建 OPC.Automation 对象");
        if (node is null) _server.Connect(progId); else _server.Connect(progId, node);
        _group = _server.OPCGroups.Add("scada");
        _group.UpdateRate = updateRateMs;
        _group.IsActive = true;
        _group.IsSubscribed = false;
        Connected = true;
    }

    public IReadOnlyList<int> AddItems(IReadOnlyList<string> itemIds)
    {
        var errors = new int[itemIds.Count];
        for (var i = 0; i < itemIds.Count; i++)
        {
            if (_handles.ContainsKey(itemIds[i])) continue;
            try
            {
                dynamic item = _group!.OPCItems.AddItem(itemIds[i], _nextClientHandle++);
                _handles[itemIds[i]] = (int)item.ServerHandle;
            }
            catch (COMException ex) { errors[i] = ex.HResult; }
        }
        return errors;
    }

    private static Array OneBased<T>(int n) => Array.CreateInstance(typeof(T), [n], [1]);

    public IReadOnlyList<OpcDaSample> Read(IReadOnlyList<string> itemIds, bool fromDevice)
    {
        var handles = OneBased<int>(itemIds.Count);
        for (var i = 0; i < itemIds.Count; i++) handles.SetValue(_handles[itemIds[i]], i + 1);
        Array values, errors; object qualities, timestamps;
        _group!.SyncRead(fromDevice ? SourceDevice : SourceCache, itemIds.Count, ref handles, out values, out errors, out qualities, out timestamps);
        var q = (Array)qualities; var t = (Array)timestamps;
        var result = new OpcDaSample[itemIds.Count];
        for (var i = 0; i < itemIds.Count; i++)
        {
            var lb = values.GetLowerBound(0);
            result[i] = new(values.GetValue(lb + i), Convert.ToInt16(q.GetValue(q.GetLowerBound(0) + i)),
                t.GetValue(t.GetLowerBound(0) + i) is DateTime dt ? dt : DateTime.MinValue, Convert.ToInt32(errors.GetValue(errors.GetLowerBound(0) + i)));
        }
        return result;
    }

    public int Write(string itemId, object value)
    {
        var handles = OneBased<int>(1); handles.SetValue(_handles[itemId], 1);
        var values = OneBased<object>(1); values.SetValue(value, 1);
        Array errors;
        _group!.SyncWrite(1, ref handles, ref values, out errors);
        return Convert.ToInt32(errors.GetValue(errors.GetLowerBound(0)));
    }

    public void Dispose()
    {
        Connected = false;
        try { if (_server is not null) { _server.OPCGroups.RemoveAll(); _server.Disconnect(); } } catch { }
        if (_server is not null) try { Marshal.FinalReleaseComObject(_server); } catch { }
        _server = null; _group = null; _handles.Clear();
    }
}
