using System.Xml.Linq;
using Scada.Data.Const;
using Scada.Web.Plugins.PlgMimicEditor.Code;

int passed = 0;
Dictionary<int, int> channels = new()
{
    [101] = CnlTypeID.Input,
    [102] = CnlTypeID.InputOutput,
    [103] = CnlTypeID.Calculated,
    [104] = CnlTypeID.CalculatedOutput,
    [105] = CnlTypeID.Output
};

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);

    passed++;
    Console.WriteLine("PASS " + message);
}

int? GetChannelType(int number) => channels.TryGetValue(number, out int type) ? type : null;
List<MimicIssue> Inspect(string body) => MimicInspector.Inspect(XDocument.Parse(body), GetChannelType);
string Mimic(string components) => $"<Mimic><Components>{components}</Components></Mimic>";
string Component(string properties, int id = 1) => $"<Text><ID>{id}</ID><Name>Temperature</Name>{properties}</Text>";

var nested = Inspect(Mimic("""
    <Panel><ID>1</ID><Name>Panel</Name><Components>
      <Panel><ID>2</ID><Components>
        <Text><ID>3</ID><Name>Nested sensor</Name><InCnlNum>999</InCnlNum></Text>
      </Components></Panel>
    </Components></Panel>
    """));
Check(nested.Count == 1 && nested[0].IsError, "Missing channels are detected inside nested containers");
Check(nested[0].ComponentID == 3 && nested[0].ComponentName == "Nested sensor" &&
    nested[0].ComponentType == "Text" && nested[0].PropertyName == "InCnlNum",
    "Issues identify the nested component and its binding property");
Check(nested[0].Message.Contains("999") && nested[0].Message.Contains("#3"),
    "Visible issue messages identify both the channel and the component ID");

var propertyObjects = Inspect(Mimic("""
    <Text><ID>4</ID>
      <Font><Name>Font metadata</Name><InCnlNum>999</InCnlNum>
        <Components><Text><ID>40</ID><InCnlNum>999</InCnlNum></Text></Components>
      </Font>
      <PropertyBindings><Item><InCnlNum>999</InCnlNum></Item></PropertyBindings>
      <Size><Width>100</Width><Height>40</Height></Size>
    </Text>
    """));
Check(propertyObjects.Count == 0, "Nested property objects are never mistaken for components");

int unusedLookups = 0;
var unbound = MimicInspector.Inspect(XDocument.Parse(Mimic(Component(
    "<InCnlNum>0</InCnlNum><OutCnlNum>-1</OutCnlNum><Channels>0, -1</Channels><Label/>"))),
    number => { unusedLookups++; return null; });
Check(unbound.Count == 0 && unusedLookups == 0,
    "Zero and negative unbound channel numbers trigger no errors, warnings or lookups");
Check(Inspect(Mimic(Component("<InCnlNum/><Label/>"))).Count == 0,
    "An empty input binding does not create an empty-label warning");
Check(Inspect(Mimic(Component("<InCnlNum>999</InCnlNum><Label/>"))).Count == 1,
    "A missing input channel produces its binding error without an extra label warning");
Check(Inspect(Mimic(Component("<InCnlNum>101</InCnlNum><Label/>"))) is [{ IsError: false, PropertyName: "Label" }],
    "A real readable input with an empty label produces one warning");
Check(Inspect(Mimic(Component("<Channels>101, 103</Channels><Label> </Label>")))
    is [{ IsError: false, PropertyName: "Label" }],
    "Positive channel-list bindings also qualify for label guidance");
Check(Inspect(Mimic(Component("<InCnlNum>101</InCnlNum>"))).Count == 0,
    "Components without a label property are not warned about labels");

foreach (int channel in new[] { 101, 102, 103, 104 })
{
    Check(Inspect(Mimic(Component($"<InCnlNum>{channel}</InCnlNum>"))).Count == 0,
        $"Channel {channel} is accepted as a readable input");
}
Check(Inspect(Mimic(Component("<InCnlNum>105</InCnlNum>"))) is [{ IsError: true, PropertyName: "InCnlNum" }],
    "A command-only output is rejected as a readable input");
foreach (int channel in new[] { 102, 104, 105 })
{
    Check(Inspect(Mimic(Component($"<OutCnlNum>{channel}</OutCnlNum>"))).Count == 0,
        $"Channel {channel} is accepted as a writable output");
}
foreach (int channel in new[] { 101, 103 })
{
    Check(Inspect(Mimic(Component($"<OutCnlNum>{channel}</OutCnlNum>"))) is [{ IsError: true }],
        $"Read-only channel {channel} is rejected for commands");
}
Check(Inspect(Mimic(Component("<SetpointChannel>101</SetpointChannel><ModeOutputChannel>103</ModeOutputChannel>")))
    .Count == 2, "Custom command properties are checked for writable channel types");
Check(Inspect(Mimic(Component("<Channels>101，102;103；104&#xA;101&#x9;102</Channels>"))).Count == 0,
    "Channel lists accept documented separators and XML whitespace");
Check(Inspect(Mimic(Component("<Channels>999,999</Channels>"))).Count == 1,
    "Repeated bad channels in a list produce one actionable issue");
Check(Inspect(Mimic(Component("<Channels>101,invalid</Channels>"))) is [{ IsError: true }],
    "Malformed channel lists are rejected");
Check(Inspect(Mimic(Component("<InCnlNum>2147483648</InCnlNum>"))) is [{ IsError: true }],
    "Overflowing channel numbers produce an error");

foreach (string size in new[] { "<Width>0</Width><Height>900</Height>",
    "<Width>1400</Width><Height>20001</Height>", "<Width>wide</Width><Height>900</Height>" })
{
    Check(Inspect($"<Mimic><Document><Size>{size}</Size></Document><Components/></Mimic>")
        is [{ IsError: true, PropertyName: "Document.Size" }],
        $"Invalid document dimensions are identified: {size}");
}
Check(Inspect("<Mimic><Document><Size><Width>1400</Width><Height>900</Height></Size></Document><Components/></Mimic>")
    .Count == 0, "A correctly sized empty document has no issues");
Check(MimicInspector.Inspect(new XDocument(), GetChannelType) is [{ IsError: true }],
    "An empty XML document produces a readable error");

var mutableDocument = XDocument.Parse(Mimic(Component("<InCnlNum>999</InCnlNum>")));
Check(MimicInspector.Inspect(mutableDocument, GetChannelType).Count == 1,
    "The current channel configuration initially identifies a missing binding");
channels[999] = CnlTypeID.Input;
Check(MimicInspector.Inspect(mutableDocument, GetChannelType).Count == 0,
    "A channel configuration change is reflected without modifying the mimic file");
channels.Remove(999);

string manyComponents = string.Concat(Enumerable.Range(1, 20)
    .Select(id => Component("<InCnlNum>999</InCnlNum>", id)));
Check(Inspect(Mimic(manyComponents)).Count == 20,
    "All issues remain available so later errors cannot be hidden by truncation");
Console.WriteLine($"Completed: {passed} mimic inspection checks passed.");
