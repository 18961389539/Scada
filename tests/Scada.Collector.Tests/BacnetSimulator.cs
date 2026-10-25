using System.Collections.Concurrent;
using System.IO.BACnet;
using System.Net;

namespace Scada.Collector.Tests;

/// <summary>基于同一 MIT BACnet 库的最小 BACnet/IP 服务器：Who-Is/I-Am、RP/RPM、带优先级数组的 WriteProperty、SubscribeCOV 与非确认通知。</summary>
public sealed class BacnetSimulator : IDisposable
{
    public sealed class Obj
    {
        public object?[] Priority = new object?[16];
        public object Relinquish = 0f;
        public BacnetApplicationTags Tag = BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL;
        public string Flags = "0000";
        public bool CovSupported = true;
        public object Present => Priority.FirstOrDefault(p => p is not null) ?? Relinquish;
    }

    private readonly BacnetClient _server;
    public readonly ConcurrentDictionary<BacnetObjectId, Obj> Objects = new();
    public readonly ConcurrentQueue<(BacnetObjectId Id, byte Priority, object? Value)> Writes = new();
    private readonly ConcurrentDictionary<BacnetObjectId, (System.IO.BACnet.BacnetAddress Adr, uint Sid)> _covSubscribers = new();
    public uint DeviceInstance { get; }
    public int Port { get; }
    public bool RejectRpm { get; set; }
    public bool AnswerWhoIs { get; set; } = true;
    public bool Silent { get; set; }
    public int RpmRequests, RpRequests, Subscriptions;

    public BacnetSimulator(uint deviceInstance)
    {
        DeviceInstance = deviceInstance;
        var transport = new BacnetIpUdpProtocolTransport(0, true);
        _server = new BacnetClient(transport, 1000, 0);
        _server.OnWhoIs += (s, adr, lo, hi) => { if (AnswerWhoIs && !Silent && (lo < 0 || (lo <= DeviceInstance && DeviceInstance <= hi))) s.Iam(DeviceInstance, BacnetSegmentations.SEGMENTATION_NONE, adr); };
        _server.OnReadPropertyRequest += OnRp;
        _server.OnReadPropertyMultipleRequest += OnRpm;
        _server.OnWritePropertyRequest += OnWrite;
        _server.OnSubscribeCOV += OnSubscribe;
        _server.Start();
        Port = ((IPEndPoint)transport.LocalEndPoint).Port;
    }

    public Obj Add(BacnetObjectTypes type, uint instance, object relinquish, BacnetApplicationTags tag)
    {
        var o = new Obj { Relinquish = relinquish, Tag = tag };
        Objects[new BacnetObjectId(type, instance)] = o;
        return o;
    }

    private IList<BacnetValue>? Read(BacnetObjectId id, uint prop, out BacnetError? error)
    {
        error = null;
        if (id.type == BacnetObjectTypes.OBJECT_DEVICE)
        {
            if (id.instance != DeviceInstance) { error = new(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT); return null; }
            return [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_OBJECT_ID, id)];
        }
        if (!Objects.TryGetValue(id, out var o)) { error = new(BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT); return null; }
        return prop switch
        {
            (uint)BacnetPropertyIds.PROP_PRESENT_VALUE => [new BacnetValue(o.Tag, o.Present)],
            (uint)BacnetPropertyIds.PROP_STATUS_FLAGS => [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.Parse(o.Flags))],
            _ => Fail(out error),
        };
        static IList<BacnetValue>? Fail(out BacnetError? e) { e = new(BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_UNKNOWN_PROPERTY); return null; }
    }

    private void OnRp(BacnetClient s, System.IO.BACnet.BacnetAddress adr, byte invoke, BacnetObjectId id, BacnetPropertyReference prop, BacnetMaxSegments seg)
    {
        if (Silent) return;
        Interlocked.Increment(ref RpRequests);
        var v = Read(id, prop.propertyIdentifier, out var e);
        if (e is { } err) s.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, invoke, err.error_class, err.error_code);
        else s.ReadPropertyResponse(adr, invoke, s.GetSegmentBuffer(seg), id, prop, v!);
    }

    private void OnRpm(BacnetClient s, System.IO.BACnet.BacnetAddress adr, byte invoke, IList<BacnetReadAccessSpecification> specs, BacnetMaxSegments seg)
    {
        if (Silent) return;
        Interlocked.Increment(ref RpmRequests);
        if (RejectRpm) { s.SendConfirmedServiceReject(adr, invoke, BacnetRejectReason.UNRECOGNIZED_SERVICE); return; }
        var results = specs.Select(spec => new BacnetReadAccessResult(spec.objectIdentifier, spec.propertyReferences.Select(r =>
        {
            var v = Read(spec.objectIdentifier, r.propertyIdentifier, out var e);
            return new BacnetPropertyValue { property = r, value = e is { } err ? [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_ERROR, err)] : v! };
        }).ToList())).ToList();
        s.ReadPropertyMultipleResponse(adr, invoke, s.GetSegmentBuffer(seg), results);
    }

    private void OnWrite(BacnetClient s, System.IO.BACnet.BacnetAddress adr, byte invoke, BacnetObjectId id, BacnetPropertyValue value, BacnetMaxSegments seg)
    {
        if (Silent) return;
        if (!Objects.TryGetValue(id, out var o)) { s.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invoke, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_UNKNOWN_OBJECT); return; }
        if (id.type is BacnetObjectTypes.OBJECT_ANALOG_INPUT or BacnetObjectTypes.OBJECT_BINARY_INPUT) { s.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invoke, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_WRITE_ACCESS_DENIED); return; }
        var v = value.value[0].Value;
        if (id.type == BacnetObjectTypes.OBJECT_MULTI_STATE_VALUE && v is uint u && u > 3) { s.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invoke, BacnetErrorClasses.ERROR_CLASS_PROPERTY, BacnetErrorCodes.ERROR_CODE_VALUE_OUT_OF_RANGE); return; }
        var pri = value.priority is >= 1 and <= 16 ? value.priority : (byte)16;
        o.Priority[pri - 1] = value.value[0].Tag == BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL ? null : v;
        Writes.Enqueue((id, pri, v));
        s.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, invoke);
        Notify(id);
    }

    private void OnSubscribe(BacnetClient s, System.IO.BACnet.BacnetAddress adr, byte invoke, uint sid, BacnetObjectId id, bool cancel, bool confirmed, uint lifetime, BacnetMaxSegments seg)
    {
        if (Silent) return;
        if (!Objects.TryGetValue(id, out var o) || !o.CovSupported) { s.ErrorResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invoke, BacnetErrorClasses.ERROR_CLASS_OBJECT, BacnetErrorCodes.ERROR_CODE_OPTIONAL_FUNCTIONALITY_NOT_SUPPORTED); return; }
        Interlocked.Increment(ref Subscriptions);
        _covSubscribers[id] = (adr, sid);
        s.SimpleAckResponse(adr, BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, invoke);
        Notify(id); // 订阅后立即发送一次初始值
    }

    /// <summary>修改值并（若被订阅）发送 COV 通知。</summary>
    public void Set(BacnetObjectTypes type, uint instance, object relinquish, string? flags = null, bool notify = true)
    {
        var id = new BacnetObjectId(type, instance); var o = Objects[id];
        o.Relinquish = relinquish; if (flags is not null) o.Flags = flags;
        if (notify) Notify(id);
    }

    private void Notify(BacnetObjectId id)
    {
        if (!_covSubscribers.TryGetValue(id, out var sub) || !Objects.TryGetValue(id, out var o)) return;
        _server.Notify(sub.Adr, sub.Sid, DeviceInstance, id, 300, false, new List<BacnetPropertyValue>
        {
            new() { property = new(BacnetPropertyIds.PROP_PRESENT_VALUE, uint.MaxValue), value = [new BacnetValue(o.Tag, o.Present)] },
            new() { property = new(BacnetPropertyIds.PROP_STATUS_FLAGS, uint.MaxValue), value = [new BacnetValue(BacnetApplicationTags.BACNET_APPLICATION_TAG_BIT_STRING, BacnetBitString.Parse(o.Flags))] },
        });
    }

    public void Dispose() => _server.Dispose();
}
