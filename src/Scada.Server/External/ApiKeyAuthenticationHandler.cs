using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;

namespace Scada.Server.External;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ExternalApiKeyService keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string KeyIdClaim = "apiKeyId";
    public const string ScopesClaim = "apiScopes";
    public const string AccessItemKey = "scada.external.access";
    public const string KeyItemKey = "scada.external.key";
    public const string ScopesItemKey = "scada.external.scopes";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = Extract(Request);
        if (secret is null) return AuthenticateResult.NoResult();
        var key = await keys.FindBySecretAsync(secret, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("无效的 API 密钥");
        var access = await keys.AccessForKeyAsync(key, Context.RequestAborted);
        Context.Items["scada.external.rateKey"] = key;
        Context.Items[AccessItemKey] = access;
        Context.Items[KeyItemKey] = key;
        Context.Items[ScopesItemKey] = key.Scopes;
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.Name, "apikey:" + key.Name));
        identity.AddClaim(new Claim(KeyIdClaim, key.Id.ToString()));
        identity.AddClaim(new Claim(ScopesClaim, ((int)key.Scopes).ToString()));
        identity.AddClaim(new Claim("apiKeyPrefix", key.KeyPrefix));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        _ = keys.TouchAsync(key.Id, CancellationToken.None);
        return AuthenticateResult.Success(ticket);
    }

    public static string? Extract(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Api-Key", out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString().Trim();
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth["Bearer ".Length..].Trim();
            if (token.StartsWith("scada_", StringComparison.Ordinal)) return token;
        }
        return null;
    }
}

public static class ExternalAuthExtensions
{
    public static ExternalApiScope? GetExternalScopes(this HttpContext http)
    {
        if (http.Items.TryGetValue(ApiKeyAuthenticationHandler.ScopesItemKey, out var s) && s is ExternalApiScope scopes)
            return scopes;
        return null;
    }

    public static ExternalApiKey? GetExternalKey(this HttpContext http) =>
        http.Items.TryGetValue(ApiKeyAuthenticationHandler.KeyItemKey, out var k) ? k as ExternalApiKey : null;

    public static bool IsApiKey(this ClaimsPrincipal user) =>
        user.Identity?.AuthenticationType == ApiKeyAuthenticationHandler.SchemeName
        || user.HasClaim(c => c.Type == ApiKeyAuthenticationHandler.KeyIdClaim);
}
