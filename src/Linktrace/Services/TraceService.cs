using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public enum LinkKind
{
    /// <summary>A physical cable between two ports.</summary>
    Cable,
    /// <summary>Internal path inside a device, e.g. patch panel front ↔ rear.</summary>
    PassThrough
}

public record TraceLink(LinkKind Kind, Cable? Cable);

/// <summary>One port along the path, plus how it connects to the next port.</summary>
public record TraceHop(Port Port, TraceLink? LinkToNext)
{
    public Device Device => Port.Device;
}

public record TraceResult(Port Start, IReadOnlyList<TraceHop> Hops, bool LoopDetected)
{
    public TraceHop First => Hops[0];
    public TraceHop Last => Hops[^1];
    public int CableCount => Hops.Count(h => h.LinkToNext?.Kind == LinkKind.Cable);
    public decimal? TotalLengthMeters
    {
        get
        {
            var cables = Hops.Where(h => h.LinkToNext?.Kind == LinkKind.Cable).Select(h => h.LinkToNext!.Cable!).ToList();
            return cables.Count > 0 && cables.All(c => c.LengthMeters is not null) ? cables.Sum(c => c.LengthMeters!.Value) : null;
        }
    }
}

/// <summary>
/// Follows a circuit end to end. From any port it alternates two kinds of step:
/// across a cable to the far port, then through the device to a paired port
/// (patch panel front ↔ rear). It stops when there is no next step.
/// </summary>
public class TraceService(IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task<TraceResult?> TraceAsync(int portId)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var cache = new Dictionary<int, Port>();

        async Task<Port?> Load(int id)
        {
            if (cache.TryGetValue(id, out var cached)) return cached;
            // Identity resolution is required because Port → Cable → Ports loops back to this port.
            var port = await ctx.Ports.AsNoTrackingWithIdentityResolution()
                .Include(p => p.Device).ThenInclude(d => d.Room).ThenInclude(r => r.Site)
                .Include(p => p.Device).ThenInclude(d => d.Rack)
                .Include(p => p.Cable).ThenInclude(c => c!.Ports)
                .AsSplitQuery()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (port is not null) cache[id] = port;
            return port;
        }

        var start = await Load(portId);
        if (start is null) return null;

        var visited = new HashSet<int> { start.Id };
        var loop = false;

        // Walk one direction. Returns the ports reached and the link used to reach each one.
        async Task<List<(TraceLink Link, Port Port)>> Walk(LinkKind firstStep)
        {
            var steps = new List<(TraceLink, Port)>();
            var current = start;
            var next = firstStep;
            while (true)
            {
                int? nextId;
                Cable? cable = null;
                if (next == LinkKind.Cable)
                {
                    cable = current.Cable;
                    nextId = cable?.Ports.FirstOrDefault(p => p.Id != current.Id)?.Id;
                }
                else
                {
                    nextId = current.PairedPortId;
                }

                if (nextId is null) break;
                if (!visited.Add(nextId.Value)) { loop = true; break; }

                var port = await Load(nextId.Value);
                if (port is null) break;

                steps.Add((new TraceLink(next, cable), port));
                current = port;
                next = next == LinkKind.Cable ? LinkKind.PassThrough : LinkKind.Cable;
            }
            return steps;
        }

        var forward = await Walk(LinkKind.Cable);
        var backward = await Walk(LinkKind.PassThrough);

        // Assemble: far end of backward walk … start … far end of forward walk.
        var hops = new List<TraceHop>();
        for (var i = backward.Count - 1; i >= 0; i--)
            hops.Add(new TraceHop(backward[i].Port, backward[i].Link));
        hops.Add(new TraceHop(start, forward.Count > 0 ? forward[0].Link : null));
        for (var i = 0; i < forward.Count; i++)
            hops.Add(new TraceHop(forward[i].Port, i + 1 < forward.Count ? forward[i + 1].Link : null));

        return new TraceResult(start, hops, loop);
    }
}
