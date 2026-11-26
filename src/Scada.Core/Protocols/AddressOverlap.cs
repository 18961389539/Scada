using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Protocols;

public sealed record AddressOverlapConflict(int TagId, string TagName, int OtherTagId, string OtherTagName, string Reason);

/// <summary>
/// 采集侧同设备地址重叠检查。合法共享：同一寄存器/字节的不同位；明确别名或只读映射（相同占用且至多一侧可写）；
/// 字与位的只读映射（位落在字范围内且至多一侧可写）。冲突：部分重叠、同占用两侧均可写、不同类型同范围（字/位规则除外）。
/// </summary>
public static class AddressOverlap
{
    public const string IssueCode = "tag.address.overlap";

    public static bool SameAddressFootprint(Tag a, Tag b) =>
        a.Area == b.Area
        && a.Address == b.Address
        && a.DbNumber == b.DbNumber
        && a.BitOffset == b.BitOffset
        && string.Equals(a.NodeId ?? "", b.NodeId ?? "", StringComparison.Ordinal)
        && a.DataType == b.DataType
        && a.TextLength == b.TextLength;

    public static bool UnchangedForCompatibility(Tag incoming, Tag previous) =>
        SameAddressFootprint(incoming, previous) && incoming.Writable == previous.Writable;

    public static IReadOnlyList<AddressOverlapConflict> Conflicts(
        Tag incoming, Device? device, IEnumerable<Tag> others, Tag? previous = null)
    {
        if (incoming.Kind != TagKind.Physical) return [];
        if (previous is not null && UnchangedForCompatibility(incoming, previous)) return [];
        var list = new List<AddressOverlapConflict>();
        foreach (var other in others)
        {
            if (other.Kind != TagKind.Physical) continue;
            if (incoming.Id != 0 && other.Id == incoming.Id) continue;
            if (Classify(incoming, other, device) is string reason)
                list.Add(new AddressOverlapConflict(incoming.Id, incoming.Name, other.Id, other.Name, reason));
        }
        return list;
    }

    public static IReadOnlyList<AddressOverlapConflict> ConflictsAmong(
        IEnumerable<Tag> tags, Device? device, IReadOnlySet<(int A, int B)>? grandfathered = null)
    {
        var list = tags.Where(t => t.Kind == TagKind.Physical).ToList();
        var found = new List<AddressOverlapConflict>();
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var a = list[i];
                var b = list[j];
                if (Classify(a, b, device) is not string reason) continue;
                if (grandfathered is not null && a.Id != 0 && b.Id != 0)
                {
                    var key = a.Id < b.Id ? (a.Id, b.Id) : (b.Id, a.Id);
                    if (grandfathered.Contains(key)) continue;
                }
                found.Add(new AddressOverlapConflict(a.Id, a.Name, b.Id, b.Name, reason));
            }
        }
        return found;
    }

    public static string FormatList(IReadOnlyList<AddressOverlapConflict> conflicts)
    {
        if (conflicts.Count == 0) return string.Empty;
        return string.Join("；", conflicts.Select(c =>
        {
            var id = c.OtherTagId > 0 ? $"#{c.OtherTagId}" : "";
            return $"与测点「{c.OtherTagName}」{id}冲突：{c.Reason}";
        }));
    }

    internal static string? Classify(Tag a, Tag b, Device? device)
    {
        _ = device;

        var aText = PointAddress.IsText(a);
        var bText = PointAddress.IsText(b);
        if (aText || bText)
        {
            if (aText && bText)
            {
                if (string.IsNullOrEmpty(a.NodeId) || !string.Equals(a.NodeId, b.NodeId, StringComparison.Ordinal))
                    return null;
                return a.Writable && b.Writable ? $"同一文本/OPC 地址「{a.NodeId}」且均可写" : null;
            }
            return null;
        }

        if (a.Area != b.Area) return null;
        if (a.Area == TagArea.DataBlock && a.DbNumber != b.DbNumber) return null;

        var unit = UsesBytes(a.Area) ? "字节" : "寄存器";
        var aStart = a.Address;
        var bStart = b.Address;
        var aCount = UsesBytes(a.Area) ? PointAddress.ByteCount(a) : PointAddress.RegisterCount(a);
        var bCount = UsesBytes(b.Area) ? PointAddress.ByteCount(b) : PointAddress.RegisterCount(b);
        var aEnd = aStart + aCount;
        var bEnd = bStart + bCount;
        if (aEnd <= bStart || bEnd <= aStart) return null;

        var aBit = IsBitTag(a);
        var bBit = IsBitTag(b);
        var shareable = AllowsBitShare(a.Area);

        if (aBit && bBit && shareable)
        {
            if (aStart != bStart) return $"位地址与另一测点的{unit}范围重叠（{aStart} 与 {bStart}+{bCount}）";
            if (a.BitOffset != b.BitOffset) return null;
            return a.Writable && b.Writable ? $"占用相同{unit} {aStart}.{a.BitOffset} 且均可写" : null;
        }

        if (aBit && bBit && !shareable)
        {
            if (aStart != bStart) return null;
            return a.Writable && b.Writable ? $"占用相同{AreaName(a.Area)} 地址 {aStart} 且均可写" : null;
        }

        if (shareable && aBit != bBit)
        {
            var bit = aBit ? a : b;
            var word = aBit ? b : a;
            var wordCount = UsesBytes(word.Area) ? PointAddress.ByteCount(word) : PointAddress.RegisterCount(word);
            if (bit.Address >= word.Address && bit.Address < word.Address + wordCount)
            {
                if (!(a.Writable && b.Writable)) return null;
                return $"字地址范围覆盖位地址 {bit.Address}.{bit.BitOffset} 且两侧均可写";
            }
        }

        var sameSpan = aStart == bStart && aCount == bCount && a.DataType == b.DataType;
        if (sameSpan && !aBit && !bBit)
            return a.Writable && b.Writable ? $"占用相同{unit} {aStart}（{a.DataType}，长度 {aCount}）且均可写" : null;

        return $"{unit}范围重叠（{a.DataType} {aStart}+{aCount} 与 {b.DataType} {bStart}+{bCount}）";
    }

    private static bool IsBitTag(Tag tag) => tag.DataType == TagDataType.Bool && !PointAddress.IsText(tag);

    private static bool AllowsBitShare(TagArea area) => area is TagArea.HoldingRegister or TagArea.InputRegister
        or TagArea.DataBlock or TagArea.Memory or TagArea.Input or TagArea.Output;

    private static bool UsesBytes(TagArea area) => area is TagArea.DataBlock or TagArea.Memory or TagArea.Input or TagArea.Output;

    private static string AreaName(TagArea area) => area switch
    {
        TagArea.Coil => "线圈",
        TagArea.DiscreteInput => "离散输入",
        _ => area.ToString()
    };
}