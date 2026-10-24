using System.Security.Cryptography;
using System.Text.Json;
using Scada.Core.Models;

namespace Scada.Gateway.Hosting;

public sealed class GatewayEngineeringRunner(GatewayAgentOptions options, IGatewayAcquisitionRuntime runtime)
{
    public async Task<GatewayEngineeringReply> ExecuteAsync(GatewayEngineeringRequest request, long serverNow, CancellationToken ct)
    {
        var secrets = new[] { options.Token }.Concat(runtime.Configuration?.Devices.Select(x => x.OpcPassword).Where(x => !string.IsNullOrEmpty(x)).Cast<string>() ?? []).ToArray();
        string Safe(string value) { foreach (var secret in secrets) value = value.Replace(secret, "[redacted]", StringComparison.Ordinal); return value.Length > 1000 ? value[..1000] : value; }
        try
        {
            if (request.Id == Guid.Empty || request.DeviceId < 1 || request.UserId < 1 || !Enum.IsDefined(request.Operation)
                || !GatewayProtocol.IsRevision(request.ConfigurationRevision) || request.CreatedTs < 1 || serverNow < request.CreatedTs
                || request.ExpiresTs <= request.CreatedTs || request.ExpiresTs - request.CreatedTs > 65000 || request.Payload.ValueKind is JsonValueKind.Undefined)
                return new(request.Id, 400, null, "工程请求参数无效");
            var remaining = request.ExpiresTs - serverNow;
            if (remaining <= 0) return new(request.Id, 504, null, "工程请求到达时已过期，未开始执行");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            deadline.Token.ThrowIfCancellationRequested();
            var data = await runtime.EngineerAsync(request, deadline.Token);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) WriteSafe(writer, data, null, Safe);
            if (output.Length > 16 * 1024 * 1024) return new(request.Id, 413, null, "工程响应超过 16 MiB，请减少查询范围");
            using var parsed = JsonDocument.Parse(output.ToArray()); return new(request.Id, 200, parsed.RootElement.Clone(), null);
        }
        catch (ArgumentException ex) { return new(request.Id, 400, null, Safe(ex.Message)); }
        catch (JsonException) { return new(request.Id, 400, null, "工程操作参数格式无效"); }
        catch (KeyNotFoundException ex) { return new(request.Id, 404, null, Safe(ex.Message)); }
        catch (InvalidOperationException ex) { return new(request.Id, 409, null, Safe(ex.Message)); }
        catch (OperationCanceledException) { return new(request.Id, 504, null, "工程操作取消或超时；修改类操作请查询实际状态"); }
        catch (Exception ex) when (ex is IOException or TimeoutException or CryptographicException or Opc.Ua.ServiceResultException)
        { return new(request.Id, 502, null, Safe(ex.Message)); }
        catch (Exception) { return new(request.Id, 500, null, "网关工程操作失败；修改类操作请查询实际状态"); }
    }
    private static void WriteSafe(Utf8JsonWriter writer, JsonElement element, string? property, Func<string, string> redact)
    {
        if (element.ValueKind == JsonValueKind.Object)
        { writer.WriteStartObject(); foreach (var item in element.EnumerateObject()) { writer.WritePropertyName(item.Name); WriteSafe(writer, item.Value, item.Name, redact); } writer.WriteEndObject(); }
        else if (element.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) WriteSafe(writer, item, property, redact); writer.WriteEndArray(); }
        else if (element.ValueKind == JsonValueKind.String && property is "message" or "error" or "currentError" or "lastFailure" or "connection") writer.WriteStringValue(redact(element.GetString()!));
        else element.WriteTo(writer); // Preserve identifiers, hashes and public DER bytes exactly.
    }
}
