using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

/// <summary>
/// Helpers for the port map's grid. A layout is a list of devices in reading order plus a column per device:
/// column 0 starts a new row, column n &gt; 0 sits to the right of the device before it.
/// </summary>
public static class PortMapLayout
{
    /// <summary>Makes columns consistent: the first device starts a row, and columns in a row count 0, 1, 2…</summary>
    public static List<int> Normalize(IReadOnlyList<int> columns, int count)
    {
        var result = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            var wantsBeside = i > 0 && i < columns.Count && columns[i] > 0;
            result.Add(wantsBeside ? result[i - 1] + 1 : 0);
        }
        return result;
    }

    public const int MaxSpan = 8;

    /// <summary>One span per device, 0 (automatic) to <see cref="MaxSpan"/>.</summary>
    public static List<int> NormalizeSpans(IReadOnlyList<int> spans, int count) =>
        Enumerable.Range(0, count).Select(i => i < spans.Count ? Math.Clamp(spans[i], 0, MaxSpan) : 0).ToList();

    /// <summary>Groups items into rows using the column numbers.</summary>
    public static List<List<T>> ToRows<T>(IReadOnlyList<T> items, IReadOnlyList<int> columns)
    {
        var cols = Normalize(columns, items.Count);
        var rows = new List<List<T>>();
        for (var i = 0; i < items.Count; i++)
        {
            if (cols[i] == 0) rows.Add([]);
            rows[^1].Add(items[i]);
        }
        return rows;
    }
}

public record PortMapViewInfo(int Id, string Name, string? Notes, int DeviceCount, DateTime UpdatedUtc, string? UpdatedBy);

/// <summary>Saved port maps (device stack + options).</summary>
public class PortMapViewService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user) : ServiceBase(factory, user)
{
    public async Task<List<PortMapViewInfo>> GetAllAsync()
    {
        await using var ctx = await ReadAsync();
        var views = await ctx.PortMapViews.AsNoTracking().OrderBy(v => v.Name).ToListAsync();
        return views.Select(v => new PortMapViewInfo(v.Id, v.Name, v.Notes, v.DeviceIds.Count, v.UpdatedUtc, v.UpdatedBy)).ToList();
    }

    public async Task<PortMapView?> GetAsync(int id)
    {
        await using var ctx = await ReadAsync();
        return await ctx.PortMapViews.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id);
    }

    /// <summary>Creates the map when <c>Id</c> is 0, otherwise updates it (rejecting stale copies).</summary>
    public async Task<PortMapView> SaveAsync(PortMapView model)
    {
        if (string.IsNullOrWhiteSpace(model.Name)) throw new LinktraceException("Give the port map a name.");
        if (model.DeviceIds.Count == 0) throw new LinktraceException("Add at least one device before saving.");

        await using var ctx = await WriteAsync();
        var isNew = model.Id == 0;
        PortMapView e;
        if (isNew) { e = new PortMapView(); ctx.PortMapViews.Add(e); }
        else
        {
            e = await ctx.PortMapViews.FirstOrDefaultAsync(v => v.Id == model.Id) ?? throw new NotFoundException("Port map");
            ExpectVersion(ctx, e, model.Version);
        }

        e.Name = model.Name.Trim();
        e.Notes = Clean(model.Notes);
        // Drop duplicate devices (keeping each device's first slot) and tidy the column numbers.
        var slots = model.DeviceIds
            .Select((id, i) => (Id: id, Column: i < model.Columns.Count ? model.Columns[i] : 0, Span: i < model.Spans.Count ? model.Spans[i] : 0))
            .DistinctBy(s => s.Id).ToList();
        e.DeviceIds = slots.Select(s => s.Id).ToList();
        e.Columns = PortMapLayout.Normalize(slots.Select(s => s.Column).ToList(), slots.Count);
        e.Spans = PortMapLayout.NormalizeSpans(slots.Select(s => s.Span).ToList(), slots.Count);
        e.FollowPassThrough = model.FollowPassThrough;
        e.ShowExternal = model.ShowExternal;
        await SaveAsync(ctx);

        Audit(ctx, isNew ? "Created" : "Updated", "PortMap", e.Id, $"Port map '{e.Name}' ({e.DeviceIds.Count} devices)");
        await SaveAsync(ctx);
        return e;
    }

    public async Task DeleteAsync(int id)
    {
        await using var ctx = await WriteAsync();
        var e = await ctx.PortMapViews.FindAsync(id) ?? throw new NotFoundException("Port map");
        ctx.PortMapViews.Remove(e);
        Audit(ctx, "Deleted", "PortMap", e.Id, $"Port map '{e.Name}'");
        await SaveAsync(ctx);
    }

    /// <summary>Orders devices by where they sit: site, room, rack, then top of the rack first.</summary>
    public async Task<List<int>> SortByLocationAsync(IReadOnlyCollection<int> deviceIds)
    {
        await using var ctx = await ReadAsync();
        var devices = await ctx.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, Site = d.Room.Site.Name, Room = d.Room.Name, Rack = d.Rack != null ? d.Rack.Name : null, d.RackUnit, d.HeightU })
            .ToListAsync();
        return devices
            .OrderBy(d => d.Site).ThenBy(d => d.Room)
            .ThenBy(d => d.Rack is null).ThenBy(d => d.Rack)
            .ThenByDescending(d => d.RackUnit is int u ? u + d.HeightU - 1 : int.MinValue)
            .ThenBy(d => d.Name)
            .Select(d => d.Id).ToList();
    }
}
