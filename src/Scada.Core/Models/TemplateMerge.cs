namespace Scada.Core.Models;

public static class TemplateMerge
{
    public static T Fields<T>(string path, T baseline, T local, T incoming,
        IReadOnlyDictionary<string, string> resolutions, List<TemplateConflict> conflicts) where T : class
    {
        var merged = ObjectTemplateJson.Copy(local);
        foreach (var property in typeof(T).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name != "Id"))
        {
            var before = property.GetValue(baseline); var current = property.GetValue(local); var desired = property.GetValue(incoming);
            if (ObjectTemplateJson.Equal(current, before) || ObjectTemplateJson.Equal(current, desired)) property.SetValue(merged, desired);
            else if (!ObjectTemplateJson.Equal(desired, before))
            {
                var key = $"{path}.{property.Name}";
                if (resolutions.TryGetValue(key, out var resolution) && resolution is "keepLocal" or "useTemplate")
                    property.SetValue(merged, resolution == "useTemplate" ? desired : current);
                else conflicts.Add(new(key, $"{path} / {property.Name}", ObjectTemplateJson.Write(before), ObjectTemplateJson.Write(current), ObjectTemplateJson.Write(desired), ["keepLocal", "useTemplate"]));
            }
        }
        return merged;
    }

    public static T Whole<T>(string key, string label, T baseline, T local, T incoming,
        IReadOnlyDictionary<string, string> resolutions, List<TemplateConflict> conflicts)
    {
        if (ObjectTemplateJson.Equal(local, baseline) || ObjectTemplateJson.Equal(local, incoming)) return incoming;
        if (ObjectTemplateJson.Equal(incoming, baseline)) return local;
        if (resolutions.TryGetValue(key, out var resolution) && resolution is "keepLocal" or "useTemplate") return resolution == "useTemplate" ? incoming : local;
        conflicts.Add(new(key, label, ObjectTemplateJson.Write(baseline), ObjectTemplateJson.Write(local), ObjectTemplateJson.Write(incoming), ["keepLocal", "useTemplate"]));
        return local;
    }
}
