using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Options;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.External;
using Scada.Server.External.Rest;
using Scada.Server.Mcp;
using Scada.Server.Startup;

namespace Scada.Server.Tests;

/// <summary>P0/P1 安全边界回归：区域限制、限流身份识别、MCP 来源、审计失败可见性与 scope 解析。</summary>
public sealed class SecurityBoundaryTests
{
    [Fact]
    public void AreaRestrictionHasThreeDistinctStates()
    {
        Assert.Null(ExternalApiKeyService.ParseAreas(null));
        Assert.Null(ExternalApiKeyService.ParseAreas("  "));
        Assert.Equal(new[] { 1, 2 }, ExternalApiKeyService.ParseAreas("[1,2]"));
        Assert.True(ExternalApiKeyService.IsAreaRestrictionValid("[1,2]"));

        // 损坏的配置必须变成「空数组 = 无任何区域」，绝不能退回 null（全厂权限）。
        foreach (var bad in new[] { "{broken", "[]", "[0,1]", "[-1]", "\"5\"", "[\"1\"]" })
        {
            Assert.Empty(ExternalApiKeyService.ParseAreas(bad)!);
            Assert.False(ExternalApiKeyService.IsAreaRestrictionValid(bad));
        }
    }

    [Fact]
    public void DamagedAreaRestrictionIsVisibleInTheKeyView()
    {
        var view = ExternalApiKeyService.View(new ExternalApiKey { Name = "k", AreaIdsJson = "{broken" });
        Assert.True(view.AreaRestrictionInvalid);
        Assert.Empty(view.AreaIds!);
        Assert.False(ExternalApiKeyService.View(new ExternalApiKey { Name = "k", AreaIdsJson = "[1]" }).AreaRestrictionInvalid);
    }

    [Fact]
    public async Task CorruptedAreaRestrictionRejectsAuthenticationInsteadOfGrantingFullAccess()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        var secret = ExternalApiKeyService.GenerateSecret(out var prefix);
        await using (var db = new ConfigDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.ExternalApiKeys.Add(new ExternalApiKey
            {
                Name = "damaged", KeyPrefix = prefix, KeyHash = ExternalApiKeyService.Hash(secret),
                Scopes = ExternalApiScope.All, AreaIdsJson = "{broken", Enabled = true
            });
            await db.SaveChangesAsync();
        }

        var before = ExternalApiKeyService.InvalidAreaRestrictionRejections;
        var service = new ExternalApiKeyService(new Factory(options), null!, null!);
        Assert.Null(await service.FindBySecretAsync(secret, CancellationToken.None));
        Assert.Equal(before + 1, ExternalApiKeyService.InvalidAreaRestrictionRejections);
    }

    [Fact]
    public void ApiKeyAndGatewayCredentialsGetTheirOwnRateLimitPartition()
    {
        var anonymous = new DefaultHttpContext();
        Assert.Null(ScadaResilience.CredentialPartitionKey(anonymous));

        var key = new DefaultHttpContext();
        key.Request.Headers["X-Api-Key"] = "scada_secret-value";
        var keyPartition = ScadaResilience.CredentialPartitionKey(key);
        Assert.StartsWith("apikey:", keyPartition);
        Assert.DoesNotContain("scada_secret-value", keyPartition);
        Assert.Equal(keyPartition, ScadaResilience.CredentialPartitionKey(key));

        var other = new DefaultHttpContext();
        other.Request.Headers["X-Api-Key"] = "scada_another";
        Assert.NotEqual(keyPartition, ScadaResilience.CredentialPartitionKey(other));

        var gateway = new DefaultHttpContext();
        gateway.Request.Headers.Authorization = "Gateway 3:" + new string('a', 48);
        Assert.StartsWith("gateway:", ScadaResilience.CredentialPartitionKey(gateway));

        var user = new DefaultHttpContext();
        user.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "7")], "jwt"));
        Assert.Equal("user:7", ScadaResilience.CredentialPartitionKey(user));
    }

    [Fact]
    public void MissingRemoteAddressIsOnlyAllowedWhenMcpExplicitlyAllowsAnySource()
    {
        Assert.False(McpSourceGuard.IsAllowed(null, null));
        Assert.False(McpSourceGuard.IsAllowed(null, ["127.0.0.1/32"]));
        Assert.True(McpSourceGuard.IsAllowed(null, ["*"]));
        Assert.True(McpSourceGuard.IsAllowed(IPAddress.Loopback, null));
        Assert.False(McpSourceGuard.IsAllowed(IPAddress.Parse("10.0.0.9"), null));
    }

    [Fact]
    public void AuditWriteFailuresAreCountedAndLogged()
    {
        var before = AuditFailures.Count;
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        AuditFailures.Report(services, "unit-test", new InvalidOperationException("disk full"));
        Assert.Equal(before + 1, AuditFailures.Count);
    }

    [Fact]
    public void UndefinedScopeValuesCannotBeStored()
    {
        var method = typeof(ExternalRestEndpoints).GetMethod("ParseScopes", BindingFlags.NonPublic | BindingFlags.Static)!;
        ExternalApiScope Parse(params string[] names) => (ExternalApiScope)method.Invoke(null, [names])!;
        Assert.Equal(ExternalApiScope.None, Parse("31", "999"));
        Assert.Equal(ExternalApiScope.ReadTags | ExternalApiScope.Write, Parse("ReadTags", "Write"));
        Assert.Equal(ExternalApiScope.ReadTags | ExternalApiScope.Write, Parse("ReadTags, Write"));
        Assert.Equal(ExternalApiScope.All, Parse("All"));
    }

    private sealed class Factory(DbContextOptions<ConfigDbContext> options) : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(options);
        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
