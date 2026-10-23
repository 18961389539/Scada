using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Opc.Ua;
using Opc.Ua.Server;
using Opc.Ua.Server.UserDatabase;
using Opc.Ua.Server.Hosting;
using System.Text;

var builder = Host.CreateApplicationBuilder(args);
var endpoint = builder.Configuration["endpoint"] ?? "opc.tcp://localhost:4841/ScadaSimulator";
var pki = Path.GetFullPath(builder.Configuration["pki"] ?? Path.Combine(AppContext.BaseDirectory, "data", "pki"));
Directory.CreateDirectory(pki);
var users = new JsonUserDatabase(Path.Combine(pki, "users.json"));
users.CreateUser("operator", Encoding.UTF8.GetBytes("opc-test-password"), new ArrayOf<Role>(new[] { Role.Operator }), UserConfigurationMask.None, "Local OPC simulator operator");
if (!users.CheckCredentials("operator", Encoding.UTF8.GetBytes("opc-test-password"))) throw new InvalidOperationException("模拟服务器账户未正确初始化");
builder.Services.AddOpcUa().AddServer(options =>
{
    var machineId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..24];
    options.ApplicationName = "ScadaOpcSimulator"; options.ApplicationUri = $"urn:scada:simulator:{machineId}"; options.ProductUri = "urn:scada:simulator";
    options.EndpointUrls.Add(endpoint); options.PkiRoot = pki;
    options.IncludeUnsecurePolicyNone = true;
    options.AutoAcceptUntrustedCertificates = builder.Configuration.GetValue<bool>("accept-test-clients");
    options.Identity.Defaults.EnableUserNamePassword = true; options.Identity.Defaults.UserDatabase = users;
    options.Identity.Defaults.UserManagement = new Opc.Ua.Server.UserManagement.UserManagement(users);
    options.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.Anonymous }); options.UserTokenPolicies.Add(new OpcUaUserTokenPolicy { TokenType = UserTokenType.UserName });
}).AddNodeManager("urn:scada:simulator:variables", nodes =>
{
    var folder = nodes.AddFolder("Scada");
    var temperature = nodes.AddVariable<float>("Temperature", folder.Node.NodeId); temperature.Node.Value = 23.5f;
    temperature.Node.AccessLevel = AccessLevels.CurrentReadOrWrite; temperature.Node.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
    var run = nodes.AddVariable<bool>("Run", folder.Node.NodeId); run.Node.Value = false;
    run.Node.AccessLevel = AccessLevels.CurrentReadOrWrite; run.Node.UserAccessLevel = AccessLevels.CurrentReadOrWrite;
    var pressure = nodes.AddVariable<double>("Pressure", folder.Node.NodeId); pressure.Node.Value = 1.25;
    var model = nodes.AddVariable<string>("Model", folder.Node.NodeId); model.Node.Value = "SCADA OPC UA simulator";
    for (var i = 0; i < 110; i++) { var value = nodes.AddVariable<ushort>($"Register{i:000}", folder.Node.NodeId); value.Node.Value = (ushort)i; }
});
await builder.Build().RunAsync();
