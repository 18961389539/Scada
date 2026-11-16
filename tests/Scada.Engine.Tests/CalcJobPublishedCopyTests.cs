using System.Reflection;
using Scada.Core.Entities;
using Scada.Engine.Calculations;

namespace Scada.Engine.Tests;

// 发布副本的守门：UsePublished 是唯一把 Published* 搬进运行时字段的入口。
// 新增一个 Published 字段却忘记在 UsePublished 里复制，保存草稿的值就会在运行中生效——
// 这类缺陷不会报错，只表现为"发布没生效"或"改了草稿却影响了正在跑的任务"。
public sealed class CalcJobPublishedCopyTests
{
    // PublishedRevision 是版本号本身；PublishedLibrarySource 没有草稿对应字段，运行时直接读已发布副本。
    private static readonly HashSet<string> Excluded = ["PublishedRevision", "PublishedLibrarySource"];

    [Fact]
    public void EveryPublishedFieldIsCopiedToItsRuntimeField()
    {
        var job = new CalcJob { Kind = CalcJobKind.Script, PublishedRevision = 1 };
        var copied = new List<string>();
        foreach (var published in typeof(CalcJob).GetProperties()
                     .Where(x => x.Name.StartsWith("Published", StringComparison.Ordinal))
                     .Where(x => !Excluded.Contains(x.Name)))
        {
            var runtimeName = published.Name["Published".Length..];
            var runtime = typeof(CalcJob).GetProperty(runtimeName);
            Assert.True(runtime is not null, $"Published 字段 {published.Name} 没有对应的运行时字段 {runtimeName}");
            Assert.True(runtime!.CanWrite, $"运行时字段 {runtimeName} 不可写，UsePublished 无法复制");
            published.SetValue(job, Sample(published.PropertyType, published: true));
            runtime.SetValue(job, Sample(runtime.PropertyType, published: false));
            copied.Add(published.Name);
        }

        Assert.NotEmpty(copied);
        UsePublished(job);

        foreach (var name in copied)
        {
            var runtimeName = name["Published".Length..];
            var expected = typeof(CalcJob).GetProperty(name)!.GetValue(job);
            var actual = typeof(CalcJob).GetProperty(runtimeName)!.GetValue(job);
            Assert.True(Equals(expected, actual), $"{runtimeName} 未从 {name} 复制：期望 {expected}，实际 {actual}");
        }
    }

    [Fact]
    public void AnUnpublishedDraftIsLeftUntouched()
    {
        var job = new CalcJob { Kind = CalcJobKind.Script, PublishedRevision = 0, Script = "draft" };

        UsePublished(job);

        Assert.Equal("draft", job.Script);
    }

    private static void UsePublished(CalcJob job) =>
        typeof(CalcJobEngine).GetMethod("UsePublished", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [job]);

    private static object Sample(Type type, bool published) => type switch
    {
        _ when type == typeof(string) => published ? "published" : "draft",
        _ when type == typeof(int) => published ? 11 : 22,
        _ when type == typeof(long) => published ? 11L : 22L,
        _ when type == typeof(double) => published ? 1.5 : 2.5,
        _ when type == typeof(bool) => published,
        _ when type.IsEnum => Enum.ToObject(type, published ? 1 : 2),
        _ => throw new InvalidOperationException($"新增的发布字段类型 {type} 需要在这里补样本值")
    };
}