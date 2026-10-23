using Scada.Data.Models;
using Scada.Web.Plugins.PlgMimBasicComp.Code;

int passed = 0;
void Check(bool condition, string message) {if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message);}
void Reject(Action action, string message) {try {action();} catch (ArgumentException) {Check(true, message); return;} throw new Exception(message);}
ParameterValue P(double value = 55) => new() {Channel=205, FeedbackChannel=201, Value=value, Minimum=0, Maximum=100, Step=1, Tolerance=.01};
OperationsStore.ValidateParameters([P()]);
Reject(() => OperationsStore.ValidateParameters([P(double.NaN)]), "Non-finite recipe values are rejected");
Reject(() => OperationsStore.ValidateParameters([P(101)]), "Out-of-range values are rejected");
Reject(() => OperationsStore.ValidateParameters([P(55.5)]), "Off-step values are rejected");
Reject(() => OperationsStore.ValidateParameters([P(),P(56)]), "Duplicate output channels are rejected before execution");
Reject(() => OperationsStore.ValidateParameters([]), "Empty groups are rejected");

var weekly = new SavedSchedule {RepeatDays="1,2,3,4,5", TimeZoneId="Asia/Shanghai", NextRunUtc=DateTimeOffset.Parse("2026-10-18T01:00:00Z")};
Check(OperationsStore.NextOccurrence(weekly, DateTimeOffset.Parse("2026-10-19T00:00:00Z")) == DateTimeOffset.Parse("2026-10-19T01:00:00Z"), "Weekday schedule keeps 09:00 in the user's timezone");
Check(OperationsStore.NextOccurrence(weekly, DateTimeOffset.Parse("2026-10-19T02:00:00Z")) == DateTimeOffset.Parse("2026-10-20T01:00:00Z"), "Passed daily occurrence advances to the next workday");
Check(OperationsStore.NextOccurrence(weekly, DateTimeOffset.Parse("2026-10-23T02:00:00Z")) == DateTimeOffset.Parse("2026-10-26T01:00:00Z"), "Workday schedules skip the weekend");
weekly.RepeatDays=""; Check(OperationsStore.NextOccurrence(weekly, DateTimeOffset.UtcNow) == null, "One-shot schedules have no automatic recurrence");

TrendBundle Make(int[] seconds, CnlData[] values) {var t = new TrendBundle([403], seconds.Length); var start = new DateTime(2026,10,22,0,0,0,DateTimeKind.Utc); foreach (var s in seconds) t.Timestamps.Add(start.AddSeconds(s)); t.Trends[0].AddRange(values); return t;}
var continuous = Make(Enumerable.Range(0,61).Select(i=>i*60).ToArray(), Enumerable.Repeat(new CnlData(2,13),61).ToArray());
var result = ArchiveEnergy.Calculate(continuous,1000);
Check(Math.Abs(result.Kwh.Value - 2) < 1e-9 && result.CoveredMinutes == 60, "2 kW for 60 valid minutes integrates to 2 kWh");
var watts = Make([0,60],[new(2000,13),new(2000,13)]);
Check(Math.Abs(ArchiveEnergy.Calculate(watts,1).Kwh.Value - 2.0/60) < 1e-9, "W inputs convert to kWh correctly");
var gap = Make([0,60,600],[new(2,13),new(2,13),new(2,13)]);
Check(ArchiveEnergy.Calculate(gap,1000).CoveredMinutes == 1 && ArchiveEnergy.Calculate(gap,1000).SkippedIntervals == 1, "Long archive gaps are not interpolated into energy");
var invalid = Make([0,60,120],[new(2,13),new(2,0),new(2,13)]);
Check(ArchiveEnergy.Calculate(invalid,1000).Kwh == null && ArchiveEnergy.Calculate(invalid,1000).SkippedIntervals == 2, "Bad-quality samples do not contribute energy or false zeroes");
var ramp = Make([0,60],[new(0,13),new(2,13)]);
Check(Math.Abs(ArchiveEnergy.Calculate(ramp,1000).Kwh.Value - 1.0/60) < 1e-9, "Changing power uses trapezoidal integration");
var from = new DateTime(2026,10,22,0,0,0,DateTimeKind.Utc);
Event E(int second, int stat, int previous = 13, long id = 0) => new() {CnlNum=201, EventID=id == 0 ? second+100 : id, Timestamp=from.AddSeconds(second), CnlStat=stat, PrevCnlStat=previous};
var sequence = new[] {E(-1,13), E(10,14), E(20,15,14), E(40,13,15), E(50,14), E(60,13,14)};
var stats = AlarmStatistics.Calculate(201,sequence,from,from.AddSeconds(100));
Check(stats.Occurrences == 2 && stats.Recoveries == 2 && stats.LevelChanges == 1, "Alarm level changes do not create extra occurrences");
Check(stats.AlarmSeconds == 40 && stats.KnownSeconds == 100 && stats.ActiveAtEnd == false, "Alarm occupancy clips intervals to the query range");
Check(stats.Chatter == 1, "Repeated onsets within 60 seconds are classified as chatter");
stats = AlarmStatistics.Calculate(201,[E(-10,14),E(30,13,14)],from,from.AddSeconds(100));
Check(stats.Occurrences == 0 && stats.AlarmSeconds == 30, "Alarms already active before the window contribute duration without a new onset");
stats = AlarmStatistics.Calculate(201,[E(-10,13),E(10,14),E(20,0,14),E(50,13,0)],from,from.AddSeconds(100));
Check(stats.AlarmSeconds == 10 && stats.KnownSeconds == 70 && stats.Recoveries == 0, "Unknown quality intervals are excluded rather than reported as recovery");
stats = AlarmStatistics.Calculate(201,[E(20,14)],from,from.AddSeconds(100));
Check(stats.Occurrences == 1 && stats.KnownSeconds == 80 && stats.AlarmSeconds == 80, "Missing seed events do not invent coverage before the first event");
stats = AlarmStatistics.Calculate(201,sequence.Concat([sequence[1]]),from,from.AddSeconds(100));
Check(stats.Occurrences == 2 && stats.AlarmSeconds == 40, "Duplicate archived event IDs do not inflate statistics");
stats = AlarmStatistics.Calculate(201,[],from,from.AddSeconds(100));
Check(stats.KnownSeconds == 0 && stats.ActiveAtEnd == null, "Empty event archives report unknown end state and zero coverage");
stats = AlarmStatistics.Calculate(201,[E(-20,14),E(-10,13,14),E(10,14)],from,from.AddSeconds(100));
Check(stats.Chatter == 1, "Chatter uses a prior onset just before the query range");
Reject(() => AlarmStatistics.Calculate(201,[],from,from), "Invalid statistics ranges are rejected");
var policy = new DiagnosticPolicy {FaultChannel=104, ResetAllowedCodes=[1], Conditions=[new() {Channel=204,Expected=1}]};
Check(policy.Permitted(new(1,13),[new(1,13)]), "Server reset policy permits only listed codes and matched conditions");
Check(!policy.Permitted(new(1,0),[new(1,13)]) && !policy.Permitted(new(1,13),[new(1,0)]), "Invalid fault or permissive quality blocks reset");
Check(!policy.Permitted(new(2,13),[new(1,13)]) && !policy.Permitted(new(0,13),[new(1,13)]), "Normal and unlisted fault codes cannot be reset");
Check(!policy.Permitted(new(1,13),[new(0,13)]) && !policy.Permitted(new(1,13),[]), "Missing or false permissives block reset");
Console.WriteLine($"Completed: {passed} backend checks passed.");
