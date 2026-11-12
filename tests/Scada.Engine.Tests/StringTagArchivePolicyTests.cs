using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Engine.History;

namespace Scada.Engine.Tests;

public sealed class StringTagArchivePolicyTests
{
    private static Tag TextTag(ArchiveMode mode = ArchiveMode.OnChange) => new()
    {
        DeviceId = 1, Name = "sn", DataType = TagDataType.String, TextLength = 32, Scale = 1, Offset = 0, ArchiveMode = mode, ArchivePeriodSec = 60
    };

    private static TagValue Value(string text, long ts = 1000, TagQuality quality = TagQuality.Good) =>
        new(1, 0, quality, ts, Text: text);

    [Fact]
    public void Only_text_or_quality_changes_are_archived()
    {
        var tag = TextTag();
        var last = new ArchiveState(1000, 0, TagQuality.Good, "SN-1");

        Assert.False(ArchivePolicy.ShouldStore(tag, Value("SN-1", 2000), last, 2000));
        Assert.True(ArchivePolicy.ShouldStore(tag, Value("SN-2", 2000), last, 2000));
        Assert.True(ArchivePolicy.ShouldStore(tag, Value("SN-1", 2000, TagQuality.Bad), last, 2000));
    }

    [Fact]
    public void Periodic_mode_also_archives_on_the_period()
    {
        var tag = TextTag(ArchiveMode.Periodic);
        var last = new ArchiveState(1000, 0, TagQuality.Good, "SN-1");

        Assert.False(ArchivePolicy.ShouldStore(tag, Value("SN-1", 59_000), last, 59_000));
        Assert.True(ArchivePolicy.ShouldStore(tag, Value("SN-1", 61_000), last, 61_000));
    }

    [Fact]
    public void String_tags_reject_positional_numeric_options()
    {
        var tag = TextTag();
        Assert.Null(Scada.Core.Models.ConfigurationValidator.Tag(tag));

        tag.Scale = 10;
        Assert.Contains("倍率", Scada.Core.Models.ConfigurationValidator.Tag(tag)!);
        tag.Scale = 1;

        tag.TextLength = 0;
        Assert.Contains("字符串长度", Scada.Core.Models.ConfigurationValidator.Tag(tag)!);
    }
}
