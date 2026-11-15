using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Alarms;

namespace Scada.Server.Services;

/// <summary>报警规则的写入契约。REST 与 MCP 共用同一份字段定义，避免两边漂移。</summary>
public sealed record AlarmRuleRequest(
    int TagId,
    AlarmRuleType Type,
    double Threshold,
    AlarmLevel Level,
    int DelaySec,
    double RecoverDeadband,
    bool Enabled,
    string? Message,
    int OffDelaySec = 0,
    int? SuppressTagId = null,
    AlarmSuppressMode SuppressMode = AlarmSuppressMode.None,
    string? Cause = null,
    string? Action = null,
    AlarmActiveWhen ActiveWhen = AlarmActiveWhen.On,
    AlarmCategory Category = AlarmCategory.Process,
    AlarmConsequence Consequence = AlarmConsequence.Major,
    int ResponseTimeSec = 600,
    bool AutoPriority = false,
    string? MessageEn = null,
    string? CauseEn = null,
    string? ActionEn = null,
    int? BitIndex = null,
    string? Expression = null);

/// <summary>
/// 报警规则写入的唯一落地点。
///
/// 此前「映射字段 → 结构校验 → 目标校验 → SaveChanges → ConfigReloader.ReloadAsync」这五步
/// 在报警规则端点、MCP 配置工具、工程导入三处各写了一遍。三份代码的字段映射已经出现细微差异，
/// 而最容易漏的是最后一步 <see cref="SaveAsync"/> 里的热加载：只落库不重载，
/// 界面显示已改、引擎仍按旧规则评估，且没有任何错误提示。
///
/// 分层约定：区域权限检查留在调用方（需要 HttpContext），本服务只承担数据校验与落库+重载。
/// </summary>
public sealed class AlarmRuleCommandService(ConfigReloader reloader)
{
    /// <summary>映射并校验。任一步失败返回错误文案，成功返回可落库的实体（未 Add）。</summary>
    public async Task<(AlarmRule? Rule, string? Error)> PrepareAsync(
        ConfigDbContext db, AlarmRuleRequest request, int? excludeRuleId, CancellationToken ct = default)
    {
        var rule = MapRule(request);
        if (Validate(rule) is { } error) return (null, error);
        if (await ValidateTargetAsync(db, rule, excludeRuleId, ct) is { } targetError) return (null, targetError);
        return (rule, null);
    }

    /// <summary>
    /// 落库并热加载。两个动作必须成对出现——这是本类存在的主要理由。
    /// SaveChanges 与 ReloadAsync 之间不吞异常：重载失败说明配置已经坏了，调用方应看到 500。
    /// </summary>
    public async Task SaveAsync(ConfigDbContext db, CancellationToken ct = default)
    {
        await db.SaveChangesAsync(ct);
        await reloader.ReloadAsync(ct);
    }

    public static AlarmRule MapRule(AlarmRuleRequest request)
    {
        var rule = new AlarmRule
        {
            TagId = request.TagId,
            Type = request.Type,
            ActiveWhen = AlarmRuleText.NormalizeActiveWhen(request.Type, request.ActiveWhen),
            Threshold = request.Threshold,
            Level = request.Level,
            Category = request.Category,
            Consequence = request.Consequence,
            ResponseTimeSec = request.ResponseTimeSec,
            AutoPriority = request.AutoPriority,
            DelaySec = request.DelaySec,
            RecoverDeadband = request.RecoverDeadband,
            Enabled = request.Enabled,
            Message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim(),
            MessageEn = string.IsNullOrWhiteSpace(request.MessageEn) ? null : request.MessageEn.Trim(),
            Cause = string.IsNullOrWhiteSpace(request.Cause) ? null : request.Cause.Trim(),
            CauseEn = string.IsNullOrWhiteSpace(request.CauseEn) ? null : request.CauseEn.Trim(),
            Action = string.IsNullOrWhiteSpace(request.Action) ? null : request.Action.Trim(),
            ActionEn = string.IsNullOrWhiteSpace(request.ActionEn) ? null : request.ActionEn.Trim(),
            OffDelaySec = request.OffDelaySec,
            BitIndex = request.Type == AlarmRuleType.Discrete ? request.BitIndex : null,
            Expression = request.Type == AlarmRuleType.Expression ? (string.IsNullOrWhiteSpace(request.Expression) ? null : request.Expression.Trim()) : null,
            SuppressTagId = request.SuppressMode == AlarmSuppressMode.None ? null : request.SuppressTagId,
            SuppressMode = request.SuppressMode
        };
        rule.ApplyAutoPriority();
        return rule;
    }

    /// <summary>结构校验：字段范围、枚举合法性、表达式可编译、文案长度。</summary>
    public static string? Validate(AlarmRule rule)
    {
        if (rule.TagId <= 0)
        {
            return "必须指定测点";
        }

        if (!Enum.IsDefined(rule.Type))
        {
            return "报警类型无效";
        }

        if (!Enum.IsDefined(rule.ActiveWhen))
        {
            return "位报警触发条件无效";
        }

        if (!Enum.IsDefined(rule.Level))
        {
            return "报警级别无效";
        }

        if (!Enum.IsDefined(rule.Category))
        {
            return "报警类别无效";
        }

        if (!Enum.IsDefined(rule.Consequence))
        {
            return "报警后果无效";
        }

        if (rule.ResponseTimeSec is < 0 or > 86400)
        {
            return "允许响应时间须在 0～86400 秒之间";
        }

        if (rule.AutoPriority && rule.ResponseTimeSec <= 0)
        {
            return "自动优先级需要填写允许响应时间";
        }

        // 位报警、质量坏报警和复合条件不使用阈值；其余类型必须有有效阈值。
        if (rule.Type is not (AlarmRuleType.StateChange or AlarmRuleType.Discrete or AlarmRuleType.BadQuality or AlarmRuleType.Expression) && double.IsNaN(rule.Threshold))
        {
            return "阈值无效";
        }

        // 位号只对位报警有意义，范围按 64 位字保护；具体测点位宽在目标校验里再收紧。
        if (rule.BitIndex is int bit && (rule.Type != AlarmRuleType.Discrete || bit is < 0 or > 63))
        {
            return "报警位号只能用于位报警，且在 0～63 之间";
        }

        if (rule.Type == AlarmRuleType.Expression)
        {
            if (string.IsNullOrWhiteSpace(rule.Expression))
            {
                return "复合条件报警必须填写表达式";
            }

            try
            {
                var expression = Scada.Core.Calculations.TagExpression.Compile(rule.Expression);
                if (expression.Dependencies.Count == 0) return "复合条件表达式至少引用一个测点（#测点号）";
            }
            catch (Scada.Core.Calculations.TagExpressionException ex)
            {
                return $"复合条件表达式无效：{ex.Message}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(rule.Expression))
        {
            return "只有复合条件报警可以使用表达式";
        }

        if (rule.DelaySec < 0)
        {
            return "报警延时不能为负数";
        }

        if (rule.RecoverDeadband < 0)
        {
            return "恢复死区不能为负数";
        }

        if (rule.DelaySec > 3600 || rule.OffDelaySec is < 0 or > 3600)
        {
            return "触发/恢复延时须在 0～3600 秒之间";
        }

        if (rule.SuppressMode == AlarmSuppressMode.None ? rule.SuppressTagId is not null : rule.SuppressTagId is null)
        {
            return "条件抑制需同时指定抑制方式和条件测点";
        }

        if (rule.SuppressTagId == rule.TagId)
        {
            return "条件测点不能是报警测点本身";
        }

        // 只写“高限/状态变化”等于把类型名重复一遍，操作员拿不到任何信息；留空则由引擎自动生成含数值的文案。
        if (rule.Message is { Length: > 0 } message && AlarmRuleText.IsPlaceholderOnly(message))
        {
            return "报警消息不能只写报警类型，请说明现象与处置（可留空自动生成）";
        }

        if (rule.Message?.Length > AlarmMessageBuilder.MaxLength)
        {
            return $"报警消息不能超过 {AlarmMessageBuilder.MaxLength} 个字符";
        }

        if (rule.MessageEn?.Length > AlarmMessageBuilder.MaxLength)
        {
            return $"英文报警消息不能超过 {AlarmMessageBuilder.MaxLength} 个字符";
        }

        if (rule.Cause?.Length > 200 || rule.Action?.Length > 200)
        {
            return "原因与处置各不能超过 200 个字符";
        }

        if (rule.CauseEn?.Length > 200 || rule.ActionEn?.Length > 200)
        {
            return "英文原因与处置各不能超过 200 个字符";
        }

        return null;
    }

    /// <summary>目标校验：测点存在性与类型匹配、位宽、同测点重复规则。</summary>
    public static async Task<string?> ValidateTargetAsync(ConfigDbContext db, AlarmRule rule, int? excludeRuleId, CancellationToken ct)
    {
        var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rule.TagId, ct);
        if (tag is null)
        {
            return $"测点不存在：{rule.TagId}";
        }
        if (tag.DataType == TagDataType.String) return "字符串测点不支持报警规则";

        if (rule.Type == AlarmRuleType.Discrete)
        {
            if (tag.DataType == TagDataType.Bool)
            {
                if (rule.BitIndex is not null) return "Bool 测点的位报警不需要填写位号";
            }
            else if (tag.DataType is TagDataType.Int16 or TagDataType.UInt16 or TagDataType.Int32 or TagDataType.UInt32)
            {
                var maxBit = tag.DataType is TagDataType.Int16 or TagDataType.UInt16 ? 15 : 31;
                if (rule.BitIndex is not int bit) return "数值字位报警必须填写位号";
                if (bit > maxBit) return $"该测点位宽只支持 0～{maxBit} 位";
            }
            else
            {
                return "位报警只能用于 Bool 或整数字测点";
            }
        }

        var level = rule.AutoPriority ? AlarmPriority.Compute(rule.Consequence, rule.ResponseTimeSec) : rule.Level;
        var duplicate = await db.AlarmRules.AsNoTracking().AnyAsync(x =>
            x.Id != (excludeRuleId ?? 0) &&
            x.TagId == rule.TagId &&
            x.Type == rule.Type &&
            x.Level == level &&
            x.Enabled == rule.Enabled &&
            x.Message == rule.Message &&
            x.BitIndex == rule.BitIndex &&
            x.Expression == rule.Expression &&
            (rule.Type != AlarmRuleType.Discrete || x.ActiveWhen == rule.ActiveWhen) &&
            (rule.Type == AlarmRuleType.StateChange || Math.Abs(x.Threshold - rule.Threshold) < 1e-9), ct);
        return duplicate ? "同一测点已存在相同的报警规则" : null;
    }
}
