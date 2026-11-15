using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scada.Core.Scripting;

namespace Scada.ScriptWorker;

internal sealed record CompiledScript(byte[] Image, byte[] Pdb, string[] Outputs);

/// <summary>
/// 编译产物缓存：内存 LRU + 磁盘副本。Worker 每 50 次执行会被回收，进程内的缓存随之消失；
/// 没有磁盘这一层，回收后第一个请求就要重新跑一遍 Roslyn（百毫秒级），负载高时会撞上执行超时。
/// </summary>
internal sealed class CompileCache
{
    // 包装源码（using 区、类壳、#line）变了就必须让旧缓存失效，否则会加载到用旧模板编出的程序集。
    private const string TemplateVersion = "1";
    private const int MaxEntries = 16;
    private const long MaxBytes = 32L * 1024 * 1024;
    private const int RetentionDays = 30;

    private sealed class Entry
    {
        public required CompiledScript Script { get; init; }
        public long LastUsed { get; set; }
        public long Bytes => Script.Image.Length + Script.Pdb.Length;
    }

    // 编译产物是对着某一版 Scada.Core 的脚本 API 编出来的。只升级服务器、不升级 Worker（或反之）时，
    // 旧产物可能引用已改签名的成员，直到运行期才炸成 MissingMethodException；把模块 MVID 纳入 key，
    // 换了 Core 就自然失效。相同源码的确定性构建 MVID 不变，所以正常重启仍然命中。
    private static readonly string CoreIdentity = typeof(ScriptContext).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private readonly Dictionary<string, Entry> _memory = new(StringComparer.Ordinal);
    private long _clock;

    public string Directory { get; }

    public CompileCache(string directory)
    {
        Directory = directory;
        try { System.IO.Directory.CreateDirectory(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        Sweep();
    }

    public static string Key(string source, string? library) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{TemplateVersion}\0{ScriptProtocol.Version}\0{CoreIdentity}\0{source}\0{library ?? ""}"))).ToLowerInvariant();

    public bool TryGet(string key, out CompiledScript script)
    {
        if (_memory.TryGetValue(key, out var entry))
        {
            entry.LastUsed = ++_clock;
            script = entry.Script;
            return true;
        }
        if (Load(key) is { } loaded)
        {
            Remember(key, loaded);
            script = loaded;
            return true;
        }
        script = null!;
        return false;
    }

    public void Put(string key, byte[] image, byte[] pdb, string[] outputs)
    {
        var script = new CompiledScript(image, pdb, outputs);
        Remember(key, script);
        Persist(key, script);
    }

    private void Remember(string key, CompiledScript script)
    {
        _memory[key] = new Entry { Script = script, LastUsed = ++_clock };
        while (_memory.Count > 1 && (_memory.Count > MaxEntries || _memory.Values.Sum(entry => entry.Bytes) > MaxBytes))
        {
            var oldest = _memory.MinBy(pair => pair.Value.LastUsed);
            _memory.Remove(oldest.Key);
        }
    }

    private CompiledScript? Load(string key)
    {
        try
        {
            var dll = Path.Combine(Directory, key + ".dll");
            var pdb = Path.Combine(Directory, key + ".pdb");
            var manifest = Path.Combine(Directory, key + ".json");
            if (!File.Exists(dll) || !File.Exists(pdb) || !File.Exists(manifest)) return null;
            var outputs = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? [];
            if (outputs.Length > CSharpScriptLimits.MaxOutputs) return null;
            var image = File.ReadAllBytes(dll);
            var symbols = File.ReadAllBytes(pdb);
            // 物理损坏（断电、杀软、写盘中断）比想象中常见：签名不对就当没有缓存，重新编译即可。
            if (image is [not (byte)'M', not (byte)'Z', ..] || symbols is [not (byte)'B', not (byte)'S', not (byte)'J', not (byte)'B', ..]) return null;
            return new CompiledScript(image, symbols, outputs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Persist(string key, CompiledScript script)
    {
        try
        {
            Write(key + ".dll", script.Image);
            Write(key + ".pdb", script.Pdb);
            Write(key + ".json", JsonSerializer.SerializeToUtf8Bytes(script.Outputs));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 缓存写不进去只影响下次冷启动，不影响本次执行。
        }

        void Write(string name, byte[] content)
        {
            var path = Path.Combine(Directory, name);
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
    }

    private void Sweep()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.*"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
