using Scada.Web.Services;

namespace Scada.Web.Plugins.PlgHelloWorld;

public sealed class PlgHelloWorldLogic : PluginLogic
{
    public PlgHelloWorldLogic(IWebContext context) : base(context) => Info = new HelloWorldInfo();
    public override List<ViewSpec> ViewSpecs => [new HelloWorldViewSpec()];

    private sealed class HelloWorldInfo : LibraryInfo
    {
        public override string Code => "PlgHelloWorld";
        public override string Name => "HelloWorld 智慧工厂";
        public override string Descr => "中文学习总览：工艺监控、原生命令、报警、归档、公式与角色权限。";
    }
}
