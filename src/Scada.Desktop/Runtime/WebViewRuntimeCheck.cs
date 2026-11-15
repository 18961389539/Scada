using Microsoft.Web.WebView2.Core;

namespace Scada.Desktop.Runtime;

internal static class WebViewRuntimeCheck
{
    public const string DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static bool TryGetVersion(out string version)
    {
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(version);
        }
        catch
        {
            version = string.Empty;
            return false;
        }
    }
}
