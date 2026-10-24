using System.Text.Json.Nodes;
using Scada.Server.Projects;

namespace Scada.Server.Tests;

// 计划器：改名会连带更新依赖它的键与引用；镜像模式产生删除；内容相同为不变。
public class ProjectPlannerTests
{
    private static ProjectItem I(string key, string json) => new() { Key = key, Data = JsonNode.Parse(json)!.AsObject() };
    private static ProjectPackage P(params (string Section, ProjectItem Item)[] items)
    {
        var p = new ProjectPackage();
        foreach (var (section, item) in items) p.Section(section).Add(item);
        return p;
    }

    [Fact]
    public void RenamingADeviceCarriesItsTagsAndReferences()
    {
        var current = P(("devices", I("泵", """{"name":"泵","pollIntervalMs":1000}""")), ("tags", I("泵/流量", """{"name":"流量","deviceRef":"泵","expression":""}""")));
        var incoming = P(("devices", I("泵", """{"name":"泵","pollIntervalMs":500}""")),
            ("tags", I("泵/流量", """{"name":"流量","deviceRef":"泵","expression":""}""")),
            ("tags", I("@双倍", """{"name":"双倍","deviceRef":null,"expression":"#{泵/流量} * 2"}""")),
            ("alarmRules", I("泵/流量|High|1", """{"tagRef":"泵/流量","type":"High","threshold":3}""")));
        var plan = ProjectPlanner.Build(current, incoming, mirror: false, ConflictResolution.Rename);
        var byKey = plan.Entries.ToDictionary(x => x.Key);
        Assert.Equal("rename", byKey["泵"].Action); Assert.Equal("泵_导入", byKey["泵"].FinalKey);
        Assert.Equal("add", byKey["泵/流量"].Action); Assert.Equal("泵_导入/流量", byKey["泵/流量"].FinalKey);
        Assert.Equal("#{泵_导入/流量} * 2", byKey["@双倍"].Data!["expression"]!.GetValue<string>());
        Assert.Equal("泵_导入/流量", byKey["泵/流量|High|1"].Data!["tagRef"]!.GetValue<string>());
    }

    [Fact]
    public void MirrorDeletesExtrasAndReportsFieldChanges()
    {
        var current = P(("devices", I("A", """{"name":"A","port":502}""")), ("devices", I("B", """{"name":"B"}""")));
        var target = P(("devices", I("A", """{"name":"A","port":503}""")));
        var plan = ProjectPlanner.Build(current, target, mirror: true, ConflictResolution.Skip);
        Assert.Equal("update", plan.Entries.Single(x => x.Key == "A").Action);
        Assert.Equal(new FieldChange("port", "502", "503"), plan.Entries.Single(x => x.Key == "A").Changes.Single());
        Assert.Equal("delete", plan.Entries.Single(x => x.Key == "B").Action);
        Assert.Empty(ProjectPlanner.Build(current, current, mirror: true, ConflictResolution.Skip).Effective);
    }
}
