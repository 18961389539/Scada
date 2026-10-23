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
}
