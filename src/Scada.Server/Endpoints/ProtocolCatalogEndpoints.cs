using Scada.Core.Protocols;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record ProtocolFieldDto(
    string Key,
    string Type,
    object Default,
    string? LabelKey,
    string? Label,
    IReadOnlyList<string>? Options,
    double? Min,
    double? Max,
    string? PlaceholderKey,
    string? Placeholder,
    string? HelpKey,
    string? Help,
    bool Locked);

public sealed record ProtocolCatalogItem(
    string Value,
    string? Label,
    string? LabelKey,
    int DefaultPort,
    bool HostPort,
    bool Credentials,
    IReadOnlyList<ProtocolFieldDto> Fields,
    string AddressPlaceholder,
    string? AddressPlaceholderKey,
    string? AddressHelpKey,
    string? NoticeKey);

public static class ProtocolCatalogEndpoints
{
    public static void MapProtocolCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/protocols", () => Results.Ok(ProtocolCatalog.All.Select(From)))
            .RequireAuthorization(AccessPolicies.View)
            .WithSummary("List protocol form metadata sourced from IProtocolSpec");
    }

    public static ProtocolCatalogItem From(IProtocolSpec spec)
    {
        var form = spec.Form;
        return new(
            spec.Protocol.ToString(),
            form.Label,
            form.LabelKey,
            form.DefaultPort,
            spec.UsesHostPort,
            spec.UsesCredentials,
            form.Fields.Select(From).ToList(),
            form.AddressPlaceholder,
            form.AddressPlaceholderKey,
            form.AddressHelpKey,
            form.NoticeKey);
    }

    private static ProtocolFieldDto From(ProtocolFormField field) => new(
        field.Key,
        field.Type,
        field.Default,
        field.LabelKey,
        field.Label,
        field.Options,
        field.Min,
        field.Max,
        field.PlaceholderKey,
        field.Placeholder,
        field.HelpKey,
        field.Help,
        field.Locked);
}
