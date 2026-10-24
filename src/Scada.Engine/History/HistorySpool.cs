using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Scada.Core.Models;

namespace Scada.Engine.History;

// 历史写入的落盘暂存：写库失败先把批次追加到磁盘分段，恢复后按原序补传，整段补传成功才删除。
// 分段轮转便于满盘/损坏尾部隔离；默认满盘拒收，可选丢弃最旧完整段。
public sealed class HistorySpool
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex SegmentName = new(@"^history-spool-(\d+)\.ndjson$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public const string LegacyFileName = "history-spool.ndjson";
    public const string SegmentPrefix = "history-spool-";
    public const string SegmentSuffix = ".ndjson";

    private readonly string _historyRoot;
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly long _segmentBytes;
    private readonly double _diskFreeWarnGb;
    private readonly bool _dropOldestOnFull;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _dropped, _spilled, _corrupt;
    private string? _activePath;
    private int _nextSequence = 1;

    public HistorySpool(
        string historyRoot,
        long maxBytes = 16L * 1024 * 1024,
        long segmentBytes = 4L * 1024 * 1024,
        double diskFreeWarnGb = 0,
        bool dropOldestOnFull = false)
    {
        if (string.IsNullOrWhiteSpace(historyRoot)) throw new ArgumentException("历史根目录不能为空", nameof(historyRoot));
        if (maxBytes < 512) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (segmentBytes < 64) throw new ArgumentOutOfRangeException(nameof(segmentBytes));
        _historyRoot = Path.GetFullPath(historyRoot);
        _directory = Path.Combine(_historyRoot, "_spool");
        _maxBytes = maxBytes;
        // 单段不超过总上限，便于测试只调 maxBytes 时仍可用。
        _segmentBytes = Math.Min(segmentBytes, maxBytes);
        _diskFreeWarnGb = diskFreeWarnGb;
        _dropOldestOnFull = dropOldestOnFull;
        DiscoverState();
    }

    // 兼容旧测试：指向遗留单文件路径（可能不存在）；运行时以分段为准。
    public string FilePath => Path.Combine(_directory, LegacyFileName);
    public string DirectoryPath => _directory;
    public long DroppedBatches => Interlocked.Read(ref _dropped);
    public long SpilledBatches => Interlocked.Read(ref _spilled);
    public long CorruptLines => Interlocked.Read(ref _corrupt);
    public int SegmentCount
    {
        get
        {
            try { return ListPendingFiles().Count; }
            catch { return 0; }
        }
    }

    public long PendingBytes
    {
        get
        {
            try
            {
                return ListPendingFiles().Sum(path =>
                {
                    try { return new FileInfo(path).Length; }
                    catch { return 0L; }
                });
            }
            catch { return 0; }
        }
    }

    public async Task AppendAsync(IReadOnlyCollection<HistoryValue> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;
        var line = JsonSerializer.Serialize(batch.ToArray(), Json);
        var bytes = Encoding.UTF8.GetByteCount(line) + 1;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            DiscoverState();

            if (IsDiskCriticallyLow())
            {
                Interlocked.Increment(ref _dropped);
                throw new IOException($"历史根目录可用空间低于 {_diskFreeWarnGb:0.##} GB，本批次 {batch.Count} 条未能落盘");
            }

            var pending = PendingBytesLocked();
            if (pending + bytes > _maxBytes)
            {
                if (_dropOldestOnFull && TryDropOldestCompleteSegment(bytes, pending))
                    pending = PendingBytesLocked();

                if (pending + bytes > _maxBytes)
                {
                    Interlocked.Increment(ref _dropped);
                    throw new IOException($"历史暂存已达上限（{_maxBytes} 字节），本批次 {batch.Count} 条未能落盘");
                }
            }

            var target = EnsureActiveSegment(bytes);
            var current = File.Exists(target) ? new FileInfo(target).Length : 0;
            var payload = Encoding.UTF8.GetBytes(line + "\n");
            await using var stream = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            stream.Position = current;
            try
            {
                await stream.WriteAsync(payload, cancellationToken);
                stream.Flush(true);
            }
            catch
            {
                stream.SetLength(current);
                stream.Flush(true);
                throw;
            }

            Interlocked.Increment(ref _spilled);
            if (current + bytes >= _segmentBytes) _activePath = null;
        }
        finally { _gate.Release(); }
    }

    // 将待补传内容按序合并到单一 NDJSON（备份互斥测试与扁平快照仍可用）。
    public async Task<bool> CopyToAsync(string target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = ListPendingFiles();
            if (files.Count == 0) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            foreach (var path in files)
            {
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                await source.CopyToAsync(destination, cancellationToken);
            }

            destination.Flush(true);
            return true;
        }
        finally { _gate.Release(); }
    }

    // 备份：把整个 _spool 目录（分段 + 遗留单文件，不含 .corrupt/.tmp）拷到目标目录。
    public async Task<IReadOnlyList<string>> CopyDirectoryToAsync(string targetDirectory, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var files = ListPendingFiles();
            if (files.Count == 0) return Array.Empty<string>();
            Directory.CreateDirectory(targetDirectory);
            var copied = new List<string>();
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                var destination = Path.Combine(targetDirectory, name);
                await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var dest = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(dest, cancellationToken);
                    dest.Flush(true);
                }

                copied.Add(destination);
            }

            return copied;
        }
        finally { _gate.Release(); }
    }

    // 按写入顺序补传；任一批次写失败立即停止，剩余批次原序写回磁盘等待下一轮。
    public async Task DrainAsync(Func<IReadOnlyCollection<HistoryValue>, Task> write, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DiscoverState();
            var files = ListPendingFiles();
            if (files.Count == 0) return;

            var failed = false;
            Exception? failure = null;

            foreach (var path in files)
            {
                if (failed) break;
                var (remaining, fileFailed, _, fileFailure) = await DrainFileAsync(path, write, cancellationToken);
                if (fileFailed)
                {
                    failed = true;
                    failure = fileFailure;
                    if (remaining.Count == 0) TryDelete(path);
                    else await RewriteFileAsync(path, remaining, cancellationToken);
                    break;
                }

                if (remaining.Count == 0) TryDelete(path);
                else await RewriteFileAsync(path, remaining, cancellationToken);
            }

            DiscoverState();
            if (failed) throw new IOException("历史暂存仍有待补传批次", failure);
        }
        finally { _gate.Release(); }
    }

    private async Task<(List<string> Remaining, bool Failed, bool Progressed, Exception? Failure)> DrainFileAsync(
        string path,
        Func<IReadOnlyCollection<HistoryValue>, Task> write,
        CancellationToken cancellationToken)
    {
        var remaining = new List<string>();
        var failed = false;
        var progressed = false;
        Exception? failure = null;
        var lines = await ReadLinesRepairingTailAsync(path, cancellationToken);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (failed) { remaining.Add(line); continue; }

            HistoryValue[]? batch;
            try { batch = JsonSerializer.Deserialize<HistoryValue[]>(line, Json); }
            catch (JsonException)
            {
                Interlocked.Increment(ref _corrupt);
                progressed = true;
                continue;
            }

            if (batch is null || batch.Length == 0)
            {
                Interlocked.Increment(ref _corrupt);
                progressed = true;
                continue;
            }

            try
            {
                await write(batch);
                progressed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failed = true;
                failure = ex;
                remaining.Add(line);
            }
        }

        return (remaining, failed, progressed, failure);
    }

    private async Task<List<string>> ReadLinesRepairingTailAsync(string path, CancellationToken cancellationToken)
    {
        var raw = await File.ReadAllBytesAsync(path, cancellationToken);
        if (raw.Length == 0) return [];

        var text = Encoding.UTF8.GetString(raw);
        var endsWithNewline = text.EndsWith('\n') || text.EndsWith("\r\n", StringComparison.Ordinal);
        var parts = text.Split('\n');
        var lines = new List<string>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].TrimEnd('\r');
            var isLast = i == parts.Length - 1;
            if (isLast && !endsWithNewline)
            {
                if (part.Length == 0) break;
                IsolateCorruptTail(path, part);
                Interlocked.Increment(ref _corrupt);
                break;
            }

            if (part.Length > 0) lines.Add(part);
        }

        return lines;
    }

    private void IsolateCorruptTail(string path, string partial)
    {
        try
        {
            var sidecar = path + ".corrupt";
            File.AppendAllText(sidecar, partial + "\n", Encoding.UTF8);
            var bytes = File.ReadAllBytes(path);
            var cut = bytes.AsSpan().LastIndexOf((byte)'\n');
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(cut >= 0 ? cut + 1 : 0);
            stream.Flush(true);
        }
        catch
        {
            // 隔离失败时仍跳过半截行。
        }
    }

    private static async Task RewriteFileAsync(string path, List<string> lines, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var line in lines)
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellationToken);
            stream.Flush(true);
        }

        File.Move(temp, path, true);
    }

    private void DiscoverState()
    {
        if (!Directory.Exists(_directory))
        {
            _activePath = null;
            _nextSequence = 1;
            return;
        }

        var max = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, SegmentPrefix + "*" + SegmentSuffix))
        {
            var name = Path.GetFileName(path);
            var match = SegmentName.Match(name);
            if (!match.Success) continue;
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && seq > max)
                max = seq;
        }

        _nextSequence = max + 1;
        if (max > 0)
        {
            var candidate = SegmentPath(max);
            try
            {
                var length = new FileInfo(candidate).Length;
                _activePath = length < _segmentBytes ? candidate : null;
            }
            catch { _activePath = null; }
        }
        else _activePath = null;
    }

    private string EnsureActiveSegment(long upcomingBytes)
    {
        if (_activePath is not null && File.Exists(_activePath))
        {
            var length = new FileInfo(_activePath).Length;
            // 段已满，或本批次放不下且段内已有数据：滚动到新段。单批超过段上限时仍写入当前/新段。
            if (length >= _segmentBytes)
                _activePath = null;
            else if (length > 0 && length + upcomingBytes > _segmentBytes && upcomingBytes <= _segmentBytes)
                _activePath = null;
            else
                return _activePath;
        }

        var legacy = Path.Combine(_directory, LegacyFileName);
        if (File.Exists(legacy))
        {
            var length = new FileInfo(legacy).Length;
            if (length < _segmentBytes && (length + upcomingBytes <= _segmentBytes || upcomingBytes > _segmentBytes))
            {
                _activePath = legacy;
                return _activePath;
            }
        }

        var path = SegmentPath(_nextSequence++);
        _activePath = path;
        return path;
    }

    private string SegmentPath(int sequence) =>
        Path.Combine(_directory, SegmentPrefix + sequence.ToString("D6", CultureInfo.InvariantCulture) + SegmentSuffix);

    private List<string> ListPendingFiles()
    {
        var result = new List<string>();
        if (!Directory.Exists(_directory)) return result;

        var legacy = Path.Combine(_directory, LegacyFileName);
        if (File.Exists(legacy)) result.Add(legacy);

        var segments = new List<(int Seq, string Path)>();
        foreach (var path in Directory.EnumerateFiles(_directory, SegmentPrefix + "*" + SegmentSuffix))
        {
            var match = SegmentName.Match(Path.GetFileName(path));
            if (!match.Success) continue;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seq)) continue;
            segments.Add((seq, path));
        }

        segments.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        foreach (var item in segments) result.Add(item.Path);
        return result;
    }

    private long PendingBytesLocked() =>
        ListPendingFiles().Sum(path =>
        {
            try { return new FileInfo(path).Length; }
            catch { return 0L; }
        });

    private bool TryDropOldestCompleteSegment(long neededBytes, long pending)
    {
        var files = ListPendingFiles();
        foreach (var path in files)
        {
            if (_activePath is not null && string.Equals(path, _activePath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var length = new FileInfo(path).Length;
                if (length <= 0) continue;
                File.Delete(path);
                Interlocked.Increment(ref _dropped);
                pending -= length;
                if (pending + neededBytes <= _maxBytes) return true;
            }
            catch { }
        }

        return pending + neededBytes <= _maxBytes;
    }

    private bool IsDiskCriticallyLow()
    {
        if (_diskFreeWarnGb <= 0) return false;
        try
        {
            var root = Path.GetPathRoot(_historyRoot);
            if (string.IsNullOrEmpty(root)) return false;
            var freeGb = new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d;
            return freeGb > 0 && freeGb < _diskFreeWarnGb;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
