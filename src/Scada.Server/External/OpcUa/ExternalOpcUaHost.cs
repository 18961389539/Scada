using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Server.Fluent;
using Opc.Ua.Server.Hosting;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;
using UaStatus = Opc.Ua.StatusCodes;

namespace Scada.Server.External.OpcUa;

/// <summary>
/// 对外 OPC UA 服务端：把测点树挂到 Objects/Scada，订阅走缓存刷新，写入走现场控制闭环。
/// 默认关闭（External:OpcUa:Enabled）。证书目录默认 keys/opcua-server。
/// </summary>
public sealed class ExternalOpcUaHost(
    IOptionsMonitor<ExternalIntegrationOptions> options,
    IOptions<ScadaSettings> scada,
    IRealtimeCache cache,
    IConfigSnapshot config,
    IServiceScopeFactory scopes,
    ExternalApiKeyService keys,
    ILogger<ExternalOpcUaHost> logger) : BackgroundService
{
    private IHost? _host;
    private CancellationTokenSource? _publishCts;
    public bool IsRunning { get; private set; }
    public string? EndpointUrl { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue.OpcUa;
            if (!opt.Enabled)
            {
                await StopServerAsync();
                await Task.Delay(1500, stoppingToken);
                continue;
            }
            try
            {
                var boundVersion = config.Version;
                await StartServerAsync(opt, stoppingToken);
                while (!stoppingToken.IsCancellationRequested && options.CurrentValue.OpcUa.Enabled)
                {
                    if (config.Version != boundVersion)
                    {
                        logger.LogInformation("配置变更，重建对外 OPC UA 地址空间（{Old} → {New}）", boundVersion, config.Version);
                        break;
                    }
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "对外 OPC UA 服务异常");
                await StopServerAsync();
                await Task.Delay(3000, stoppingToken);
            }
            finally { await StopServerAsync(); }
        }
    }

    private async Task StartServerAsync(ExternalOpcUaOptions opt, CancellationToken ct)
    {
        if (_host is not null) return;
        var listen = string.IsNullOrWhiteSpace(opt.EndpointUrl) ? "opc.tcp://0.0.0.0:4840/Scada" : opt.EndpointUrl.Trim();
        EndpointUrl = AdvertiseUrl(listen);
        var pki = ResolvePkiRoot(opt);
        Directory.CreateDirectory(pki);

        var modes = opt.SecurityModes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()).ToHashSet();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(cache);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(scopes);
        builder.Services.AddSingleton(keys);
        builder.Services.AddSingleton(logger);

        var serverBuilder = builder.Services.AddOpcUa().AddServer(o =>
        {
            o.ApplicationName = "Scada External OPC UA";
            o.ApplicationUri = "urn:scada:external-opcua";
            o.ProductUri = "urn:scada:external-opcua";
            o.EndpointUrls.Add(listen);
            o.PkiRoot = pki;
            o.AutoAcceptUntrustedCertificates = true;
            o.IncludeUnsecurePolicyNone = modes.Contains("none");
            o.IncludeSignAndEncryptPolicies = modes.Contains("sign") || modes.Contains("signandencrypt");
            o.Identity.Defaults.EnableAnonymous = opt.AllowAnonymous;
            o.Identity.Defaults.EnableUserNamePassword = opt.AllowUsernamePassword;
            o.Identity.Defaults.EnableX509 = opt.AllowCertificateUser;
            if (opt.AllowAnonymous)
                o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.Anonymous });
            if (opt.AllowUsernamePassword)
                o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.UserName });
            if (opt.AllowCertificateUser)
                o.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.Certificate });
        });

        serverBuilder.AddDefaultIdentityAuthenticators(o =>
        {
            o.EnableAnonymous = opt.AllowAnonymous;
            o.EnableUserNamePassword = false;
            o.EnableX509 = opt.AllowCertificateUser;
        });

        if (opt.AllowUsernamePassword)
        {
            var authenticator = new UserNamePasswordAuthenticator(async (token, tokenCt) =>
            {
                var user = token.UserName?.Trim() ?? "";
                var password = token.DecryptedPassword is { Length: > 0 } pwd
                    ? Encoding.UTF8.GetString(pwd) : "";
                // API 密钥：密码为 scada_… 明文；DisplayName 编码为 apikey:{id}:{name}，便于写时还原区域权限。
                if (password.StartsWith("scada_", StringComparison.Ordinal))
                {
                    var key = await keys.FindBySecretAsync(password, tokenCt);
                    if (key is null) throw new ServiceResultException(UaStatus.BadUserAccessDenied);
                    _ = keys.TouchAsync(key.Id, CancellationToken.None);
                    return new UserIdentity(token) { DisplayName = $"apikey:{key.Id}:{key.Name}" };
                }
                await using var scope = scopes.CreateAsyncScope();
                var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync(tokenCt);
                var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == user && x.Enabled, tokenCt);
                if (account is null || !PasswordHasher.Verify(password, account.PasswordHash))
                    throw new ServiceResultException(UaStatus.BadUserAccessDenied);
                return new UserIdentity(token) { DisplayName = account.UserName };
            });
            serverBuilder.AddIdentityAuthenticator(authenticator);
        }

        var publishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _publishCts = publishCts;
        var interval = Math.Clamp(opt.PublishingIntervalMs, 100, 10_000);

        serverBuilder.AddNodeManager("urn:scada:external:tags", nodes =>
            BindAddressSpace(nodes, cache, config, scopes, keys, logger, publishCts.Token, interval));

        _host = builder.Build();
        await _host.StartAsync(ct);
        IsRunning = true;
        logger.LogInformation("对外 OPC UA 已启动：监听 {Listen}，客户端可用 {Endpoint}", listen, EndpointUrl);
    }

    private async Task StopServerAsync()
    {
        IsRunning = false;
        try { _publishCts?.Cancel(); } catch { }
        try { _publishCts?.Dispose(); } catch { }
        _publishCts = null;
        if (_host is null) return;
        try { await _host.StopAsync(TimeSpan.FromSeconds(5)); } catch { }
        try { if (_host is IAsyncDisposable asyncDisp) await asyncDisp.DisposeAsync(); } catch { }
        try { _host.Dispose(); } catch { }
        _host = null;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await StopServerAsync();
        await base.StopAsync(cancellationToken);
    }

    private string ResolvePkiRoot(ExternalOpcUaOptions opt)
    {
        if (!string.IsNullOrWhiteSpace(opt.ServerCertPath))
        {
            var path = Path.GetFullPath(opt.ServerCertPath);
            return Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
        }
        var cfgDir = Path.GetDirectoryName(Path.GetFullPath(scada.Value.ConfigDbPath)) ?? "data";
        return Path.Combine(cfgDir, "keys", "opcua-server");
    }

    private static string AdvertiseUrl(string listen)
    {
        if (string.IsNullOrWhiteSpace(listen)) return "opc.tcp://127.0.0.1:4840/Scada";
        return listen.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
    }

    private static void BindAddressSpace(
        INodeManagerBuilder nodes,
        IRealtimeCache cache,
        IConfigSnapshot config,
        IServiceScopeFactory scopes,
        ExternalApiKeyService keys,
        ILogger logger,
        CancellationToken publishCt,
        int publishIntervalMs)
    {
        var root = nodes.AddFolder("Scada");
        var byArea = new Dictionary<int, INodeBuilder>();
        var byDevice = new Dictionary<int, INodeBuilder>();
        var published = new List<(int TagId, BaseVariableState State)>();

        foreach (var tag in config.Tags.OrderBy(t => t.Id))
        {
            try
            {
                var areaId = AreaOf(config, tag);
                if (!byArea.TryGetValue(areaId, out var areaNode))
                {
                    areaNode = nodes.AddFolder($"Area{areaId}", root.Node.NodeId);
                    byArea[areaId] = areaNode;
                }
                INodeBuilder parent = areaNode;
                if (tag.DeviceId is int deviceId)
                {
                    if (!byDevice.TryGetValue(deviceId, out var deviceNode))
                    {
                        var deviceName = config.TryGetDevice(deviceId, out var device) ? SafeName(device.Name) : $"Device{deviceId}";
                        deviceNode = nodes.AddFolder(deviceName, areaNode.Node.NodeId);
                        byDevice[deviceId] = deviceNode;
                    }
                    parent = deviceNode;
                }

                var variable = nodes.AddVariable<double>(SafeName(tag.Name) + "_" + tag.Id, parent.Node.NodeId);
                var state = (BaseVariableState)variable.Node;
                state.Description = new LocalizedText(tag.Description ?? tag.Name);
                state.AccessLevel = tag.Writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;
                state.UserAccessLevel = state.AccessLevel;
                var initial = cache.Get(tag.Id);
                state.Value = initial?.Value ?? 0d;
                state.StatusCode = ToStatus(initial?.Quality);

                var tagId = tag.Id;
                ((INodeBuilder)variable).OnRead((ctx, node, ct) =>
                {
                    var v = cache.Get(tagId);
                    if (v is null)
                        return ValueTask.FromResult(new AttributeSimpleReadResult(new ServiceResult(UaStatus.BadNoData), Variant.Null));
                    var status = ToStatus(v.Quality);
                    return ValueTask.FromResult(new AttributeSimpleReadResult(new ServiceResult(status), new Variant(v.Value)));
                });

                if (tag.Writable)
                {
                    ((INodeBuilder)variable).OnWrite(async (ctx, node, range, value, ct) =>
                    {
                        try
                        {
                            if (!TryToDouble(value, out var dbl))
                                return new AttributeWriteResult(new ServiceResult(UaStatus.BadTypeMismatch));
                            var actorName = string.IsNullOrWhiteSpace(ctx.UserId) ? "opcua" : ctx.UserId!;
                            if (ctx is ServerSystemContext serverCtx && !string.IsNullOrWhiteSpace(serverCtx.UserIdentity?.DisplayName))
                                actorName = serverCtx.UserIdentity!.DisplayName;

                            await using var scope = scopes.CreateAsyncScope();
                            var access = await ResolveAccessAsync(scope.ServiceProvider, keys, actorName, ct);
                            if (access is not null && !access.CanTag(Permission.Control, tagId))
                                return new AttributeWriteResult(new ServiceResult(UaStatus.BadUserAccessDenied));
                            if (access is null && (actorName is "opcua" or "Anonymous" || string.Equals(actorName, "anonymous", StringComparison.OrdinalIgnoreCase)))
                                return new AttributeWriteResult(new ServiceResult(UaStatus.BadUserAccessDenied));

                            var commands = scope.ServiceProvider.GetRequiredService<ControlCommandService>();
                            var result = await commands.SubmitAsync(new ControlSubmission(tagId, dbl,
                                new ControlActor(null, "opcua:" + actorName), ControlOrigin.Operator, "外部 OPC UA", true), ct);
                            return new AttributeWriteResult(result.Success
                                ? ServiceResult.Good
                                : new ServiceResult(UaStatus.Bad, new LocalizedText(result.Message)));
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "OPC UA 写入测点 {TagId} 失败", tagId);
                            return new AttributeWriteResult(new ServiceResult(UaStatus.BadUnexpectedError, ex));
                        }
                    });
                }

                published.Add((tagId, state));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "OPC UA 绑定测点 {Tag} 跳过", tag.Id);
            }
        }

        var ctx = nodes.Context;
        _ = Task.Run(async () =>
        {
            while (!publishCt.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(publishIntervalMs, publishCt);
                    foreach (var (tagId, state) in published)
                    {
                        var v = cache.Get(tagId);
                        if (v is null) continue;
                        state.Value = v.Value;
                        state.StatusCode = ToStatus(v.Quality);
                        try { state.ClearChangeMasks(ctx, false); } catch { }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { try { await Task.Delay(1000, publishCt); } catch { break; } }
            }
        }, publishCt);
    }

    private static async Task<AccessContext?> ResolveAccessAsync(IServiceProvider sp, ExternalApiKeyService keys, string actorName, CancellationToken ct)
    {
        try
        {
            if (actorName.StartsWith("opcua:", StringComparison.Ordinal))
                actorName = actorName["opcua:".Length..];
            if (actorName.StartsWith("apikey:", StringComparison.Ordinal))
            {
                var rest = actorName["apikey:".Length..];
                var colon = rest.IndexOf(':');
                var idPart = colon >= 0 ? rest[..colon] : rest;
                if (int.TryParse(idPart, out var keyId))
                {
                    await using var db = await sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync(ct);
                    var key = await db.ExternalApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == keyId, ct);
                    if (key is null || !key.Enabled || key.RevokedTs is not null)
                        return new AccessContext(null, Array.Empty<AccessGrant>(), await sp.GetRequiredService<AccessModelProvider>().GetAsync(ct));
                    return await keys.AccessForKeyAsync(key, ct);
                }
            }
            var accessProvider = sp.GetService<AccessModelProvider>();
            if (accessProvider is null) return null;
            await using var userDb = await sp.GetRequiredService<IDbContextFactory<ConfigDbContext>>().CreateDbContextAsync(ct);
            var user = await userDb.Users.AsNoTracking().SingleOrDefaultAsync(x => x.UserName == actorName && x.Enabled, ct);
            if (user is not null) return await accessProvider.ForUserAsync(user.Id, ct);
            return null;
        }
        catch { return null; }
    }

    private static StatusCode ToStatus(TagQuality? quality) => quality switch
    {
        TagQuality.Good => UaStatus.Good,
        TagQuality.Uncertain => UaStatus.Uncertain,
        null => UaStatus.BadNoData,
        _ => UaStatus.Bad
    };

    private static bool TryToDouble(Variant value, out double dbl)
    {
        dbl = 0;
        try
        {
            var boxed = value.AsBoxedObject();
            if (boxed is null) return false;
            dbl = Convert.ToDouble(boxed);
            return true;
        }
        catch { return false; }
    }

    private static int AreaOf(IConfigSnapshot config, Tag tag)
    {
        if (tag.AreaId is int a) return a;
        if (tag.DeviceId is int d && config.TryGetDevice(d, out var device)) return device.AreaId;
        return Area.DefaultId;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Item";
        var chars = name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        return new string(chars);
    }
}
