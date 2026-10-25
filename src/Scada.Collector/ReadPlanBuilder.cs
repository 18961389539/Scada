using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector;

public sealed class ReadPlanBuilder : IReadPlanBuilder
{
    private const int MaxGap = 10;
    private const int MaxRegisterLength = 125;
    private const int MaxBitLength = 2000;

    public ReadPlan Build(Device device, IReadOnlyList<Tag> tags)
    {
        if (device.Protocol == ProtocolType.OpcUa || Scada.Core.Protocols.ProtocolCatalog.IsTextAddress(device.Protocol)) return new ReadPlan { DeviceId = device.Id, Tags = tags };
        if (device.Protocol == ProtocolType.SiemensS7) return BuildS7(device, tags);
        var blocks = new List<ReadBlock>();

        foreach (var areaGroup in tags.GroupBy(x => x.Area).OrderBy(x => x.Key))
        {
            var maxLength = areaGroup.Key is TagArea.Coil or TagArea.DiscreteInput ? MaxBitLength : MaxRegisterLength;
            var ordered = areaGroup.OrderBy(x => x.Address).ThenBy(x => x.Id).ToList();

            var pending = new List<Tag>();
            var blockStart = 0;
            var blockEnd = 0;

            void Flush()
            {
                if (pending.Count == 0)
                {
                    return;
                }

                blocks.Add(new ReadBlock
                {
                    Area = areaGroup.Key,
                    StartAddress = blockStart,
                    Length = blockEnd - blockStart + 1,
                    Tags = pending.Select(tag => new TagReadItem(
                        tag.Id,
                        tag.Address - blockStart,
                        tag.DataType,
                        device.ByteOrder)).ToList()
                });
                pending.Clear();
            }

            foreach (var tag in ordered)
            {
                var tagEnd = tag.Address + (areaGroup.Key is TagArea.Coil or TagArea.DiscreteInput ? 1 : tag.RegisterCount) - 1;

                if (pending.Count == 0)
                {
                    blockStart = tag.Address;
                    blockEnd = tagEnd;
                    pending.Add(tag);
                    continue;
                }

                var gap = tag.Address - blockEnd - 1;
                var newLength = tagEnd - blockStart + 1;
                if (gap <= MaxGap && newLength <= maxLength)
                {
                    pending.Add(tag);
                    blockEnd = Math.Max(blockEnd, tagEnd);
                }
                else
                {
                    Flush();
                    blockStart = tag.Address;
                    blockEnd = tagEnd;
                    pending.Add(tag);
                }
            }

            Flush();
        }

        return new ReadPlan
        {
            DeviceId = device.Id,
            Blocks = blocks,
            Tags = tags
        };
    }

    private static ReadPlan BuildS7(Device device, IReadOnlyList<Tag> tags)
    {
        var blocks = new List<ReadBlock>();
        foreach (var group in tags.GroupBy(x => (x.Area, Db: x.Area == TagArea.DataBlock ? x.DbNumber : 1)))
        {
            var pending = new List<Tag>();
            var start = 0; var end = 0;
            void Flush()
            {
                if (pending.Count == 0) return;
                blocks.Add(new ReadBlock { Area = group.Key.Area, DbNumber = group.Key.Db, StartAddress = start, Length = end - start,
                    Tags = pending.Select(x => new TagReadItem(x.Id, x.Address - start, x.DataType, ByteOrder.ABCD, x.BitOffset)).ToList() });
                pending.Clear();
            }
            foreach (var tag in group.OrderBy(x => x.Address).ThenBy(x => x.Id))
            {
                if (pending.Count > 0 && (tag.Address > end || tag.Address + tag.ByteCount - start > 200)) Flush();
                if (pending.Count == 0) { start = tag.Address; end = tag.Address; }
                pending.Add(tag); end = Math.Max(end, tag.Address + tag.ByteCount);
            }
            Flush();
        }
        return new ReadPlan { DeviceId = device.Id, Tags = tags, Blocks = blocks };
    }
}
