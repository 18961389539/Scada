using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;
using Scada.Core.Scripting;

namespace Scada.Data.Infrastructure;

public static class SerialPortOccupancyQuery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<List<SerialPortOwner>> LoadAsync(
        ConfigDbContext db,
        IConfiguredSerialProfiles? profiles,
        CancellationToken ct,
        int? exceptProfileId = null,
        int? exceptDeviceId = null,
        int? exceptChannelId = null,
        int? exceptJobId = null,
        string? exceptConfigName = null)
    {
        var owners = new List<SerialPortOwner>();
        owners.AddRange(SerialPortOccupancy.FromConfigFile(profiles?.Current, exceptConfigName));
        var profileRows = await db.SerialProfiles.AsNoTracking().ToListAsync(ct);
        owners.AddRange(SerialPortOccupancy.FromProfiles(profileRows, exceptProfileId));
        var channels = await db.Channels.AsNoTracking().Where(row => row.Enabled).ToListAsync(ct);
        owners.AddRange(SerialPortOccupancy.FromChannels(channels, exceptChannelId));
        var serial = ChannelProtocols.SerialCandidates;
        var devices = (await db.Devices.AsNoTracking()
                .Where(row => row.Enabled && serial.Contains(row.Protocol))
                .ToListAsync(ct));
        owners.AddRange(SerialPortOccupancy.FromExclusiveDevices(devices, exceptDeviceId));
        var jobs = await db.TraceabilityJobs.AsNoTracking()
            .Where(row => row.Enabled && row.SnSource == TraceabilitySnSource.Serial)
            .Select(row => new { row.Id, row.Name, row.ScannerOptionsJson })
            .ToListAsync(ct);
        var scanners = new List<(int Id, string Name, string Port)>();
        foreach (var job in jobs)
        {
            try
            {
                var options = JsonSerializer.Deserialize<TraceabilityScannerOptions>(job.ScannerOptionsJson, Json);
                if (options is not null && !string.IsNullOrWhiteSpace(options.SerialPort))
                    scanners.Add((job.Id, job.Name, options.SerialPort));
            }
            catch (JsonException) { }
        }
        owners.AddRange(SerialPortOccupancy.FromScanners(scanners, exceptJobId));
        return owners;
    }
}
