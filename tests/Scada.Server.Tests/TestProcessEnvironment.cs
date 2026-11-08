using System.Runtime.CompilerServices;

namespace Scada.Server.Tests;

// WebApplicationFactory 在测试进程内启动真实宿主：托盘属于桌面交互功能，测试时关掉，
// 与 E2E 启动器（web/tests/start-server.mjs）保持一致。
internal static class TestProcessEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("SCADA_NO_TRAY", "1");
    }
}