using Scada.Core.Licensing;
using Scada.Engine.Calculations;

namespace Scada.Server.Licensing;

// 试用包含全部功能位。已激活的授权必须带 csharp-scripts，计算任务才会编译和运行 C#。
internal sealed class CSharpScriptLicenseGate(LicenseService license) : ICSharpScriptLicense
{
    public bool AllowsCSharp => !license.Current.Blocked && license.Current.Features.Contains(LicenseFeatures.CSharpScripts);
}
