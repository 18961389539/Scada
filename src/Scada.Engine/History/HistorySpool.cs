using System.Text;
using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Engine.History;

// 历史写入的落盘暂存：写库失败先把批次追加到磁盘，恢复后按原序补传，补传成功才删除。
// 目的不是提高吞吐，而是让"写入失败"变成可恢复的积压，而不是内存里的静默丢弃。
public sealed class HistorySpool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory, _path;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _dropped, _spilled;

    public HistorySpool(string historyRoot, long maxBytes = 16L * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(historyRoot)) throw new ArgumentException("历史根目录不能为空", nameof(historyRoot));
        _directory = Path.Combine(Path.GetFullPath(historyRoot), "_spool");
        _path = Path.Combine(_directory, "history-spool.ndjson");
        _maxBytes = maxBytes;
    }

    public string FilePath => _path;
    public long DroppedBatches => Interlocked.Read(ref _dropped);
    public long SpilledBatches => Interlocked.Read(ref _spilled);
    public long PendingBytes { get { try { return File.Exists(_path) ? new FileInfo(_path).Length : 0; } catch { return 0; } } }

    public async Task AppendAsync(IReadOnlyCollection<HistoryValue> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        var line = JsonSerializer.Serialize(batch.ToArray(), Json);
        var bytes = Encoding.UTF8.GetByteCount(line) + 1;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var current = File.Exists(_path) ? new FileInfo(_path).Length : 0;
            if (current + bytes > _maxBytes)
            {
                Interlocked.Increment(ref _dropped);
                throw new IOException($"历史暂存已达上限（{_maxBytes} 字节），本批次 {batch.Count} 条未能落盘");
            }
            var payload = Encoding.UTF8.GetBytes(line + "\n");
            await using var stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            stream.Position = current;
            try { await stream.WriteAsync(payload, cancellationToken); stream.Flush(true); }
            catch { stream.SetLength(current); stream.Flush(true); throw; }
            Interlocked.Increment(ref _spilled);
        }
        finally { _gate.Release(); }
    }

    // Backup uses the same gate as append/drain, so its copy cannot end midway through a batch.
    public async Task<bool> CopyToAsync(string target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await using var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken); destination.Flush(true); return true;
        }
        finally { _gate.Release(); }
    }

    // 按写入顺序补传；任一批次写失败立即停止，剩余批次原序写回磁盘等待下一轮。
    public async Task DrainAsync(Func<IReadOnlyCollection<HistoryValue>, Task> write, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return;
            var remaining = new List<string>();
            var failed = false; var progressed = false; Exception? failure = null;
            foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (failed) { remaining.Add(line); continue; }
                var batch = JsonSerializer.Deserialize<HistoryValue[]>(line, Json);
                if (batch is null || batch.Length == 0) continue;
                try { await write(batch); progressed = true; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { failed = true; failure = ex; remaining.Add(line); }
            }
            if (remaining.Count == 0) { File.Delete(_path); return; }
            if (progressed)
            {
                var temp = _path + ".tmp";
                await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    foreach (var line in remaining) await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
                    stream.Flush(true);
                }
                File.Move(temp, _path, true);
            }
            if (failed) throw new IOException("历史暂存仍有待补传批次", failure);
        }
        finally { _gate.Release(); }
    }
}
