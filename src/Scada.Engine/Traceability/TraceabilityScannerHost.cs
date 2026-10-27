using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Data;

namespace Scada.Engine.Traceability;

public sealed record TraceabilityScannerStatus(string State, string? Error = null);

public sealed class TraceabilityScannerHost(IDbContextFactory<ConfigDbContext> factory, TraceabilityService service,
    INodeRole role, ILogger<TraceabilityScannerHost> logger) : BackgroundService
{
    private sealed record Connection(long Revision, CancellationTokenSource Cancellation, Task Task);
    private readonly Dictionary<int, Connection> _connections = new();
    private readonly ConcurrentDictionary<int, TraceabilityScannerStatus> _statuses = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TraceabilityScannerStatus Status(int id) => _statuses.GetValueOrDefault(id) ?? new("stopped");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { await RefreshAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Cannot refresh traceability scanners"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await StopConnectionsAsync(); }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var jobs = role.IsActive
                ? await db.TraceabilityJobs.AsNoTracking().Where(job => job.Enabled && (job.SnSource == TraceabilitySnSource.Tcp || job.SnSource == TraceabilitySnSource.Serial)).ToListAsync(ct)
                : [];
            foreach (var id in _connections.Keys.ToArray())
                if (!jobs.Any(job => job.Id == id && job.Revision == _connections[id].Revision)) await StopConnectionAsync(id);
            foreach (var job in jobs)
            {
                if (_connections.ContainsKey(job.Id)) continue;
                var errors = service.Validate(job);
                if (errors.Count != 0) { _statuses[job.Id] = new("error", string.Join(" ", errors)); continue; }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _connections[job.Id] = new(job.Revision, cancellation, RunConnectionAsync(job, cancellation.Token));
            }
        }
        finally { _gate.Release(); }
    }

    private async Task RunConnectionAsync(TraceabilityJob job, CancellationToken ct)
    {
        var options = TraceabilityService.ScannerOptions(job);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                void State(string state) => _statuses[job.Id] = new(state);
                async Task Receive(string sn)
                {
                    try
                    {
                        await service.ReceiveScannerAsync(job.Id, sn, job.SnSource, job.Revision, ct);
                        _statuses[job.Id] = new("connected");
                    }
                    catch (InvalidOperationException ex)
                    {
                        _statuses[job.Id] = new("connected", ex.Message);
                        logger.LogWarning("Scanner SN rejected for traceability job {JobId}: {Reason}", job.Id, ex.Message);
                    }
                }
                if (job.SnSource == TraceabilitySnSource.Tcp) await TraceabilityScannerConnection.ReceiveTcpAsync(options, State, Receive, ct);
                else await TraceabilityScannerConnection.ReceiveSerialAsync(options, State, Receive, ct);
                throw new IOException("Scanner disconnected; reconnecting.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _statuses[job.Id] = new("disconnected", ex.Message);
                logger.LogWarning("Traceability scanner {JobId} disconnected: {Reason}", job.Id, ex.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task StopConnectionAsync(int id)
    {
        if (!_connections.Remove(id, out var connection)) return;
        await connection.Cancellation.CancelAsync();
        try { await connection.Task; }
        catch (OperationCanceledException) { }
        finally { connection.Cancellation.Dispose(); _statuses[id] = new("stopped"); }
    }

    private async Task StopConnectionsAsync()
    {
        await _gate.WaitAsync();
        try { foreach (var id in _connections.Keys.ToArray()) await StopConnectionAsync(id); }
        finally { _gate.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await StopConnectionsAsync();
    }
}