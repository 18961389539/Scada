using System.Globalization;
using System.Text;
using System.Text.Json;
using Scada.Desktop.Api;
using Scada.Desktop.Configuration;

namespace Scada.Desktop.Runtime;

/// <summary>
/// 在页面脚本执行前把会话写入 localStorage，使前端路由守卫直接进入目标画面。
/// 同一进程用 sessionStorage 标记只注入一次，避免覆盖前端续期后的新 token。
/// </summary>
internal static class InjectionScript
{
    public static string Build(DesktopSettings settings, SessionData? session, string bootId, bool force = false, string? serverUrl = null)
    {
        var origin = new Uri(serverUrl ?? settings.ServerUrl).GetLeftPart(UriPartial.Authority);
        var builder = new StringBuilder("(function(){try{if(window.top!==window.self)return;");
        builder.Append("if(location.origin!==").Append(Js(origin)).Append(")return;");
        if (!force)
        {
            builder.Append("if(sessionStorage.getItem(").Append(Js(FrontendContract.DesktopBootKey)).Append(")===")
                .Append(Js(bootId)).Append(")return;");
        }

        if (session is not null)
        {
            builder.Append("localStorage.setItem(").Append(Js(FrontendContract.TokenKey)).Append(',').Append(Js(session.Token)).Append(");");
            builder.Append("localStorage.setItem(").Append(Js(FrontendContract.UserKey)).Append(',').Append(Js(session.UserJson)).Append(");");
            builder.Append("localStorage.setItem(").Append(Js(FrontendContract.ExpiresKey)).Append(',').Append(Js(session.ExpiresAt)).Append(");");
            if (!string.IsNullOrWhiteSpace(session.Locale))
            {
                builder.Append("localStorage.setItem(").Append(Js(FrontendContract.LocaleKey)).Append(',')
                    .Append(Js(session.Locale!)).Append(");");
            }
        }

        if (settings.Target.Kind == "workstation" && settings.Target.WorkstationId is int workstationId)
        {
            builder.Append("localStorage.setItem(").Append(Js(FrontendContract.WorkstationIdKey)).Append(',')
                .Append(Js(workstationId.ToString(CultureInfo.InvariantCulture))).Append(");");
            if (!string.IsNullOrWhiteSpace(settings.Target.WorkstationName))
            {
                builder.Append("localStorage.setItem(").Append(Js(FrontendContract.WorkstationKey)).Append(',')
                    .Append(Js(settings.Target.WorkstationName!)).Append(");");
            }
            else
            {
                builder.Append("localStorage.removeItem(").Append(Js(FrontendContract.WorkstationKey)).Append(");");
            }
        }
        else
        {
            builder.Append("localStorage.removeItem(").Append(Js(FrontendContract.WorkstationIdKey)).Append(");");
            builder.Append("localStorage.removeItem(").Append(Js(FrontendContract.WorkstationKey)).Append(");");
        }

        builder.Append("sessionStorage.setItem(").Append(Js(FrontendContract.DesktopBootKey)).Append(',')
            .Append(Js(bootId)).Append(");");
        builder.Append("}catch(e){}})();");
        return builder.ToString();
    }

    private static string Js(string value) => JsonSerializer.Serialize(value);
}
