using Scada.Data.Models;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

/// <summary>Event-based occupancy; unknown intervals never count as normal or as alarm duration.</summary>
public static class AlarmStatistics
{
    public sealed class Row
    {
        public int Channel { get; set; }
        public int Occurrences { get; set; }
        public int Recoveries { get; set; }
        public int LevelChanges { get; set; }
        public int Chatter { get; set; }
        public double AlarmSeconds { get; set; }
        public double KnownSeconds { get; set; }
        public bool? ActiveAtEnd { get; set; }
    }
    public static bool Active(int status) => status is 11 or 12 or 14 or 15;
    public static Row Calculate(int channel, IEnumerable<Event> source, DateTime from, DateTime to, int chatterSeconds = 60)
    {
        if (to <= from) throw new ArgumentException("结束时间必须晚于开始时间");
        var events = source.Where(e => e.CnlNum == channel && e.Timestamp <= to)
            .GroupBy(e => (e.EventID, e.Timestamp)).Select(g => g.Last()).OrderBy(e => e.Timestamp).ThenBy(e => e.EventID).ToArray();
        var row = new Row {Channel = channel};
        var seed = events.LastOrDefault(e => e.Timestamp < from);
        int status = seed?.CnlStat ?? 0;
        var cursor = from;
        DateTime? lastStart = events.LastOrDefault(e => e.Timestamp < from && Active(e.CnlStat) && OperationsEngine.Good(e.PrevCnlStat) && !Active(e.PrevCnlStat))?.Timestamp;
        bool first = true;
        foreach (var e in events.Where(e => e.Timestamp >= from))
        {
            var elapsed = (e.Timestamp - cursor).TotalSeconds;
            if (OperationsEngine.Good(status)) {row.KnownSeconds += elapsed; if (Active(status)) row.AlarmSeconds += elapsed;}
            // PrevCnlStat describes the transition, but cannot prove coverage before the first archived event.
            int previous = first && seed == null ? e.PrevCnlStat : status;
            if (Active(e.CnlStat) && OperationsEngine.Good(previous) && !Active(previous))
            {
                row.Occurrences++;
                if (lastStart.HasValue && (e.Timestamp - lastStart.Value).TotalSeconds <= chatterSeconds) row.Chatter++;
                lastStart = e.Timestamp;
            }
            if (Active(previous) && OperationsEngine.Good(e.CnlStat) && !Active(e.CnlStat)) row.Recoveries++;
            if (Active(previous) && Active(e.CnlStat) && previous != e.CnlStat) row.LevelChanges++;
            status = e.CnlStat; cursor = e.Timestamp; first = false;
        }
        if (OperationsEngine.Good(status)) {var elapsed = (to - cursor).TotalSeconds; row.KnownSeconds += elapsed; if (Active(status)) row.AlarmSeconds += elapsed;}
        row.ActiveAtEnd = OperationsEngine.Good(status) ? Active(status) : null;
        return row;
    }
}
