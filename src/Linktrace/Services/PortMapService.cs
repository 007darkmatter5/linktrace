using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

/// <summary>A connection between two ports that are both on devices in the view.</summary>
/// <param name="Cable">The cable, when the two ports are plugged into each other directly.</param>
/// <param name="CableCount">Number of cables along the path (1 when direct).</param>
public record PortLink(int PortAId, int PortBId, Cable? Cable, int CableCount)
{
    public bool IsDirect => CableCount == 1;
}

/// <summary>A port in the view whose cable leads to a device that isn't shown.</summary>
/// <param name="FarDeviceId">Device at the far end (end of the whole path when following patch panels).</param>
/// <param name="FarText">"CORE-SW01 › Gi1/0/9"</param>
public record ExternalLink(int PortId, Cable? Cable, int CableCount, int FarDeviceId, string FarText);

public record PortMapData(List<PortLink> Links, List<ExternalLink> Externals);

public record Neighbor(int DeviceId, string Name, DeviceKind Kind, int CableCount);

/// <summary>Works out how the ports of a set of devices are connected, for the port map.</summary>
public class PortMapService(IDbContextFactory<ApplicationDbContext> factory)
{
    /// <summary>Devices directly cabled to this one, with how many cables go to each.</summary>
    public async Task<List<Neighbor>> GetNeighborsAsync(int deviceId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var rows = await ctx.Ports.AsNoTracking()
            .Where(p => p.DeviceId == deviceId && p.CableId != null)
            .SelectMany(p => p.Cable!.Ports.Where(o => o.Id != p.Id), (p, o) => new { o.DeviceId, o.Device.Name, o.Device.Kind })
            .ToListAsync();
        return rows.Where(r => r.DeviceId != deviceId)
            .GroupBy(r => r.DeviceId)
            .Select(g => new Neighbor(g.Key, g.First().Name, g.First().Kind, g.Count()))
            .OrderByDescending(n => n.CableCount).ThenBy(n => n.Name)
            .ToList();
    }

    /// <summary>Devices in a rack, top of the rack first (devices without a position last).</summary>
    public async Task<List<int>> GetRackDeviceIdsAsync(int rackId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var devices = await ctx.Devices.AsNoTracking().Where(d => d.RackId == rackId)
            .Select(d => new { d.Id, d.Name, d.RackUnit, d.HeightU }).ToListAsync();
        return devices.OrderByDescending(d => d.RackUnit is int u ? u + d.HeightU - 1 : int.MinValue)
            .ThenBy(d => d.Name).Select(d => d.Id).ToList();
    }

    /// <summary>
    /// Connections among <paramref name="deviceIds"/>, plus cables that lead elsewhere.
    /// With <paramref name="throughPassThrough"/>, paths are followed through patch panels that
    /// aren't in the view, so e.g. two switches joined via a pair of fiber panels show one line.
    /// </summary>
    public async Task<PortMapData> GetMapAsync(IReadOnlyCollection<int> deviceIds, bool throughPassThrough)
    {
        var shown = deviceIds.ToHashSet();
        await using var ctx = await factory.CreateDbContextAsync();

        // The whole port graph is small (a few ints per port), so walk it in memory.
        var ports = await ctx.Ports.AsNoTracking()
            .Select(p => new GraphPort(p.Id, p.DeviceId, p.CableId, p.PairedPortId))
            .ToDictionaryAsync(p => p.Id);
        var cableEnds = ports.Values.Where(p => p.CableId != null)
            .GroupBy(p => p.CableId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToArray());

        var startPorts = ports.Values.Where(p => shown.Contains(p.DeviceId) && p.CableId != null).ToList();
        var cableIds = startPorts.Select(p => p.CableId!.Value).Distinct().ToList();
        var cables = await ctx.Cables.AsNoTracking().Where(c => cableIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        var links = new List<PortLink>();
        var pending = new List<(int PortId, int? CableId, int Cables, int FarPortId)>();
        var seenPairs = new HashSet<(int, int)>();

        foreach (var start in startPorts)
        {
            var path = Walk(start, ports, cableEnds, shown, throughPassThrough);
            if (path is null) continue;
            var (endId, cableCount, reachedShown) = path.Value;
            var cable = cableCount == 1 ? cables.GetValueOrDefault(start.CableId!.Value) : null;

            if (reachedShown)
            {
                var key = start.Id < endId ? (start.Id, endId) : (endId, start.Id);
                if (seenPairs.Add(key)) links.Add(new PortLink(start.Id, endId, cable, cableCount));
            }
            else
            {
                pending.Add((start.Id, start.CableId, cableCount, endId));
            }
        }

        // Describe the far ends of cables leaving the view.
        var farIds = pending.Select(p => p.FarPortId).Distinct().ToList();
        var far = await ctx.Ports.AsNoTracking().Where(p => farIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Face, p.DeviceId, DeviceName = p.Device.Name })
            .ToDictionaryAsync(p => p.Id);
        var externals = pending
            .Where(p => far.ContainsKey(p.FarPortId))
            .Select(p =>
            {
                var f = far[p.FarPortId];
                return new ExternalLink(p.PortId, p.Cables == 1 && p.CableId is int cid ? cables.GetValueOrDefault(cid) : null,
                    p.Cables, f.DeviceId, Display.Port(f.DeviceName, f.Name, f.Face));
            })
            .ToList();

        return new PortMapData(links, externals);
    }

    private record GraphPort(int Id, int DeviceId, int? CableId, int? PairedPortId);

    /// <summary>
    /// From a port, cross its cable; if following pass-throughs, keep going (through the paired
    /// port, across the next cable…) until reaching a shown device or the end of the path.
    /// </summary>
    private static (int EndPortId, int Cables, bool ReachedShown)? Walk(GraphPort start, Dictionary<int, GraphPort> ports,
        Dictionary<int, int[]> cableEnds, HashSet<int> shown, bool follow)
    {
        var visited = new HashSet<int> { start.Id };
        var current = start;
        var cables = 0;

        while (true)
        {
            // Across the cable.
            if (current.CableId is not int cid || !cableEnds.TryGetValue(cid, out var ends)) return null;
            var farId = ends.FirstOrDefault(id => id != current.Id);
            if (farId == 0 || !ports.TryGetValue(farId, out var farPort)) return null;
            cables++;
            if (shown.Contains(farPort.DeviceId)) return (farPort.Id, cables, true);
            if (!follow || !visited.Add(farPort.Id)) return (farPort.Id, cables, false);

            // Through the device to the paired port, if there is one with a cable.
            if (farPort.PairedPortId is not int pid || !ports.TryGetValue(pid, out var paired)
                || paired.CableId is null || !visited.Add(paired.Id))
                return (farPort.Id, cables, false);
            current = paired;
        }
    }
}
