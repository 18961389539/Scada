using Scada.Lang;
using Scada.Web.Plugins.PlgMimic.Components;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

/// <summary>Industrial controls shared by the online editor and mimic runtime.</summary>
public static class IndustrialComponentGroups
{
    public static List<ComponentGroup> Create()
    {
        LocaleDict dict = Locale.GetDictionary(PluginConst.ComponentModelPrefix + "IndustrialGroups");
        (string Key, string[] Types)[] groups = [
            ("Shapes", ["Line", "Rectangle", "Ellipse", "Arrow", "Pipe", "Tee", "Elbow", "DoubleArrow", "Flange"]),
            ("Displays", ["Gauge", "Progress", "Level", "Trend", "DataTable", "Thermometer", "PressureGauge", "FlowMeter", "EnergyMeter", "BarChart", "PieChart", "TrendMulti"]),
            ("Inputs", ["NumberInput", "Select", "Slider", "Checkbox", "RadioGroup", "ButtonGroup", "EmergencyStop", "KeySwitch", "SelectorSwitch", "AlarmAckButton"]),
            ("Equipment", ["Pump", "Valve", "Fan", "Motor", "Tank", "HeatExchanger", "Compressor", "Boiler", "Conveyor", "Filter", "CoolingTower", "ControlValve"]),
            ("Alarms", ["AlarmList", "StatusGroup", "AlarmBanner", "StatusBadge", "KpiCard", "StackLight", "InterlockPanel", "FaultDiagnostics", "AlarmStatistics"]),
            ("Electrical", ["CircuitBreaker", "Disconnector", "GroundSwitch", "Transformer", "Busbar", "ElectricalLine"]),
            ("Layout", ["GroupBox", "Divider", "Tabs", "CollapsiblePanel"]),
            ("Operations", ["NavBar", "NavButton", "BackButton", "DevicePanel", "ActiveAlarmTable", "HistoryQuery", "CommStatus", "OperationLog", "Recipe", "Schedule", "PidPanel"])
        ];
        List<ComponentGroup> result = [];
        foreach (var (key, types) in groups)
        {
            ComponentGroup group = new()
            {
                Name = dict[key],
                DictionaryPrefix = PluginConst.ComponentModelPrefix
            };
            foreach (string type in types)
            {
                group.Items.Add(new ComponentItem
                {
                    TypeName = "Industrial" + type,
                    DisplayName = dict[type],
                    IconUrl = "~/plugins/MimBasicComp/images/industrial-" + type + ".svg"
                });
            }
            result.Add(group);
        }
        return result;
    }
}
