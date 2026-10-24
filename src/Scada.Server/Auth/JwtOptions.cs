using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Scada.Core.Entities;

namespace Scada.Server.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "ScadaServer";
    public string Audience { get; set; } = "ScadaWeb";
    public string Key { get; set; } = string.Empty;
    public int ExpireHours { get; set; } = 8;
}

public sealed class JwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public (string Token, DateTimeOffset ExpiresAt) Create(User user)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(Math.Max(_options.ExpireHours, 1));
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("credentialsVersion", CredentialsVersion(user)),
            new("displayName", user.DisplayName)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public static string CredentialsVersion(User user) => CredentialsVersion(user.PasswordHash);

    public static string CredentialsVersion(string passwordHash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)));
}

public static class ClaimsPrincipalExtensions
{
    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string GetUserName(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
}
