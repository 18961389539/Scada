namespace Scada.Core.Models;

/// <summary>
/// 一条配置校验结果。与"返回一句中文"相比，这里多带两样东西：
/// <list type="bullet">
/// <item><c>Code</c>：稳定错误码（如 <c>device.name</c>），界面据此做本地化，改文案不会打断翻译键。</item>
/// <item><c>Field</c>：实体属性名，表单可以直接定位到出错的输入框。</item>
/// </list>
/// <c>Message</c> 保留当前中文文案，供日志、命令行与尚未接码的调用方使用；
/// 新代码请只用 <c>Code</c> 做展示判断，不要匹配文案。
/// </summary>
public readonly record struct ValidationIssue(string Code, string? Field, string Message)
{
    public override string ToString() => Field is null ? Message : $"{Field}: {Message}";
}

public static class ValidationIssueExtensions
{
    /// <summary>取第一条错误文案：兼容只关心"第一处错误"的旧调用方。</summary>
    public static string? FirstMessage(this IReadOnlyList<ValidationIssue> issues) =>
        issues.Count == 0 ? null : issues[0].Message;

    /// <summary>供 API 返回：错误码 + 字段 + 文案，前端可同时做表单定位与本地化。</summary>
    public static object[] ToPayload(this IReadOnlyList<ValidationIssue> issues) =>
        issues.Select(issue => (object)new { code = issue.Code, field = issue.Field, message = issue.Message }).ToArray();
}
