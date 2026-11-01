using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.Tests;

// 自动优先级矩阵：后果优先，允许响应时间只影响严重/较大档。
public class AlarmPriorityTests
{
    [Theory]
    [InlineData(AlarmConsequence.Safety, 0, AlarmLevel.Critical)]
    [InlineData(AlarmConsequence.Safety, 3600, AlarmLevel.Critical)]
    [InlineData(AlarmConsequence.Severe, 60, AlarmLevel.Critical)]
    [InlineData(AlarmConsequence.Severe, 301, AlarmLevel.Warning)]
    [InlineData(AlarmConsequence.Major, 600, AlarmLevel.Warning)]
    [InlineData(AlarmConsequence.Major, 601, AlarmLevel.Info)]
    [InlineData(AlarmConsequence.Minor, 1, AlarmLevel.Info)]
    public void Compute_FollowsConsequenceAndResponseTime(AlarmConsequence consequence, int responseSec, AlarmLevel expected) =>
        Assert.Equal(expected, AlarmPriority.Compute(consequence, responseSec));
}
