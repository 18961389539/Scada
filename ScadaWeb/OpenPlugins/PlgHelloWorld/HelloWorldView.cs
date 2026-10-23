using Scada.Data.Entities;
using Scada.Data.Models;

namespace Scada.Web.Plugins.PlgHelloWorld;

// Binding the overview to channels also enables the shell's native event-by-view filter.
public sealed class HelloWorldView : ViewBase
{
    public HelloWorldView(View view) : base(view) => StoredOnServer = false;

    public override void Bind(ConfigDataset database)
    {
        foreach (Cnl channel in database.CnlTable)
        {
            if (channel.Active && (channel.Code?.StartsWith("HelloWorld.", StringComparison.Ordinal) == true ||
                channel.DeviceNum == 1))
                AddCnl(channel);
        }
    }
}

public sealed class HelloWorldViewSpec : ViewSpec
{
    public override string TypeCode => nameof(HelloWorldView);
    public override string FileExtension => string.Empty;
    public override string IconUrl => "~/plugins/Main/images/table-icon.png";
    public override Type ViewType => typeof(HelloWorldView);
    public override string GetFrameUrl(int viewID) => "~/Demo";
}
