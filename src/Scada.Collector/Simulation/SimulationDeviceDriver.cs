using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Simulation;

namespace Scada.Collector.Simulation;

/// <summary>
/// 仿真驱动：不打开任何现场连接，读值来自 <see cref="SimulationRuntime"/>，
/// 写入落为强制值并可由同一次控制回读确认。适用于投运前的画面、报警、历史与联锁调试。
/// </summary>
public sealed class SimulationDeviceDriver(SimulationRuntime runtime) : IDeviceDriver
{
    private SimulationDeviceOptions _options = new();
    private bool _connected;

    public bool IsConnected => _connected;
    public string? LastError { get; private set; }

    public Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        _options = SimulationOptions.Parse(device);
        _connected = true;
        LastError = null;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_connected) throw new IOException(LastError ?? "仿真设备未连接");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var values = runtime.Read(device, _options, plan.Tags);
            LastError = values.FirstOrDefault(value => value.Error is not null)?.Error;
            return Task.FromResult(values);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            throw;
        }
    }

    public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!_connected) throw CommandFailure.Tag(new IOException("仿真设备未连接，命令未下发"), GatewayCommandOutcome.NotSent);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            runtime.Write(device.Id, tag.Id, value);
            LastError = null;
        }
        catch (IOException ex)
        {
            LastError = ex.Message;
            throw CommandFailure.Tag(ex, GatewayCommandOutcome.NotSent);
        }
        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        _connected = false;
        LastError = "已断开";
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
