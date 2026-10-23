using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public static class ObjectTemplateExamples
{
    public static ObjectTemplateDocument Pump()
    {
        ScreenNode Node(string id, string type, string text, int x, int y, int w = 160, int h = 100) => new()
        { Id = id, Type = type, Text = text, X = x, Y = y, Width = w, Height = h };
        var start = Node("start", "button", "启动", 90, 300, 150, 48);
        start.Action = new() { Type = "command", Value = 1 };
        var stop = Node("stop", "button", "停止", 270, 300, 150, 48);
        stop.Action = new() { Type = "command", Value = 0 };
        return new()
        {
            Tags =
            [
                new() { Key = "run", AddressOffset = 0, Properties = new Tag { DeviceId = 1, Name = "运行", Area = TagArea.Coil, DataType = TagDataType.Bool, Writable = true, MinValue = 0, MaxValue = 1 } },
                new() { Key = "frequency", AddressOffset = 0, Properties = new Tag { DeviceId = 1, Name = "频率", Area = TagArea.HoldingRegister, DataType = TagDataType.Float, Writable = true, Unit = "Hz", MinValue = 0, MaxValue = 50, Deadband = .1 } },
                new() { Key = "fault", AddressOffset = 2, Properties = new Tag { DeviceId = 1, Name = "故障代码", Area = TagArea.HoldingRegister, DataType = TagDataType.UInt16, MinValue = 0, MaxValue = 65535 } }
            ],
            Alarms = [new() { Key = "fault", TagKey = "fault", Properties = new AlarmRule { TagId = 1, Type = AlarmRuleType.High, Threshold = 0, Level = AlarmLevel.Critical, Message = "设备故障，请检查控制器故障代码" } }],
            Panel = new ScreenDocument
            {
                Width = 800, Height = 480,
                Nodes = [Node("title", "text", "水泵操作面板", 50, 20, 700, 50), Node("pump", "pump", "运行状态", 90, 110, 200, 150),
                    Node("frequencyLabel", "text", "频率 / Hz", 400, 90, 250, 40), Node("frequency", "value", "频率", 400, 140, 250, 60),
                    Node("faultLabel", "text", "故障代码", 400, 220, 250, 40), Node("fault", "value", "故障代码", 400, 270, 250, 50), start, stop]
            },
            Bindings = new() { ["pump"] = "run", ["frequency"] = "frequency", ["fault"] = "fault" },
            ActionBindings = new() { ["start"] = "run", ["stop"] = "run" }
        };
    }
}
