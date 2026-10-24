using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Server.Services;

namespace Scada.Server.Auth;

public sealed class GatewayAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IDbContextFactory<ConfigDbContext> factory, IOptions<GatewaySettings> settings) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GatewayAgent";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Gateway ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!Request.IsHttps && !(settings.Value.AllowLoopbackHttp && Context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)))
            return AuthenticateResult.Fail("网关连接必须使用 HTTPS");
        if (header.Length > 300) return AuthenticateResult.Fail("网关身份无效");
        var parts = header[8..].Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1
            || parts[1].Length is < 40 or > 256 || parts[1].Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_'))) return AuthenticateResult.Fail("网关身份无效");
        await using var db = await factory.CreateDbContextAsync(Context.RequestAborted);
        var gateway = await db.Gateways.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Enabled, Context.RequestAborted);
        if (gateway is null || gateway.TokenHash.Length != 64 || !gateway.TokenHash.All(Uri.IsHexDigit)
            || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(gateway.TokenHash), Convert.FromHexString(GatewayCenterService.TokenHash(parts[1]))))
            return AuthenticateResult.Fail("网关身份无效或已停用");
        var claims = new[] { new Claim("gateway-id", id.ToString(CultureInfo.InvariantCulture)), new Claim("gateway-key-stamp", gateway.TokenHash) };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
