using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

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
        e.DeviceIds = model.DeviceIds.Distinct().ToList();
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
