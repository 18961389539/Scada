using Microsoft.Extensions.Configuration;

namespace Scada.Gateway.Hosting;

public static class GatewayAgentConfiguration
{
    public static bool ValidationRequested(string[] args)
    {
        using var commandLine = new ConfigurationManager(); commandLine.AddCommandLine(args);
        var value = commandLine.GetValue<bool?>("ValidateConfig");
        if (value is null && args.Any(x => x.Equals("--ValidateConfig", StringComparison.OrdinalIgnoreCase) || x.StartsWith("--ValidateConfig=", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("ValidateConfig 须明确指定 true 或 false");
        return value ?? false;
    }

    public static GatewayAgentOptions Read(IConfiguration configuration)
    {
        var path = configuration["GatewayConfig"];
        var options = new GatewayAgentOptions();
        if (path is null) configuration.GetSection("GatewayAgent").Bind(options);
        else
        {
            // A service installation is bound to this file; ambient machine environment
            // variables must not silently change its gateway identity or data directory.
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("网关服务配置须使用绝对路径");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 64 * 1024) throw new InvalidDataException("网关服务配置过大");
            using var file = new ConfigurationManager(); file.AddJsonStream(input);
            file.GetSection("GatewayAgent").Bind(options);
        }
        options.Validate(); return options;
    }
}
