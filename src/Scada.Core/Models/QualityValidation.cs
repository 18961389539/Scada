using Scada.Core.Entities;

namespace Scada.Core.Models;

public static class QualityValidation
{
    public static string? DefectCode(QualityDefectCode code)
    {
        if (string.IsNullOrWhiteSpace(code.Code) || code.Code.Trim().Length > 64)
            return "缺陷代码须为 1～64 个字符";
        if (string.IsNullOrWhiteSpace(code.Name) || code.Name.Trim().Length > 100)
            return "缺陷名称须为 1～100 个字符";
        if (code.Category.Length > 64) return "缺陷分类过长（最多 64 个字符）";
        if (code.Description.Length > 500) return "缺陷说明过长（最多 500 个字符）";
        return null;
    }

    public static string? Inspection(QualityInspection inspection)
    {
        if (inspection.Sn.Length > 200) return "SN 过长（最多 200 个字符）";
        if (inspection.BatchNo.Length > 100) return "批次号过长（最多 100 个字符）";
        if (inspection.Product.Length > 100) return "产品名称过长（最多 100 个字符）";
        if (inspection.AreaId <= 0) return "必须指定区域";
        if (!Enum.IsDefined(inspection.Result)) return "检验结果无效";
        if (inspection.Result != QualityResult.Passed && inspection.DefectCodeId is null)
            return "非合格记录必须选择缺陷代码";
        if (inspection.Inspector.Length > 100) return "检验员过长（最多 100 个字符）";
        if (inspection.Note.Length > 500) return "备注过长（最多 500 个字符）";
        if (inspection.Ts <= 0) return "检验时间无效";
        return null;
    }
}
