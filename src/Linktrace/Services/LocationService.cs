using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public class LocationService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user) : ServiceBase(factory, user)
{
    /// <summary>All sites with their rooms and racks (and device counts per room/rack).</summary>
    public async Task<List<Site>> GetSitesAsync()
    {
        await using var ctx = await ReadAsync();
        return await ctx.Sites.AsNoTracking()
            .Include(s => s.Rooms.OrderBy(r => r.Name)).ThenInclude(r => r.Racks.OrderBy(k => k.Name))
            .OrderBy(s => s.Name)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <summary>Device counts keyed by room id and by rack id.</summary>
    public async Task<(Dictionary<int, int> ByRoom, Dictionary<int, int> ByRack)> GetDeviceCountsAsync()
    {
        await using var ctx = await ReadAsync();
        var byRoom = await ctx.Devices.GroupBy(d => d.RoomId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var byRack = await ctx.Devices.Where(d => d.RackId != null).GroupBy(d => d.RackId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        return (byRoom, byRack);
    }

    /// <summary>Flat list of rooms (with site) for pickers.</summary>
    public async Task<List<Room>> GetRoomsAsync()
    {
        await using var ctx = await ReadAsync();
        return await ctx.Rooms.AsNoTracking().Include(r => r.Site).Include(r => r.Racks)
            .OrderBy(r => r.Site.Name).ThenBy(r => r.Name).ToListAsync();
    }

    public async Task<Rack?> GetRackAsync(int id)
    {
        await using var ctx = await ReadAsync();
        return await ctx.Racks.AsNoTracking()
            .Include(r => r.Room).ThenInclude(r => r.Site)
            .Include(r => r.Devices).ThenInclude(d => d.Ports)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    // ---- Sites ----

    public async Task<Site> SaveSiteAsync(Site model)
    {
        await using var ctx = await WriteAsync();
        var isNew = model.Id == 0;
        Site e;
        if (isNew) { e = new Site(); ctx.Sites.Add(e); }
        else
        {
            e = await ctx.Sites.FindAsync(model.Id) ?? throw new NotFoundException("Site");
            ExpectVersion(ctx, e, model.Version);
        }

        e.Name = model.Name.Trim();
        e.Address = Clean(model.Address);
        e.Notes = Clean(model.Notes);
        await SaveAsync(ctx);

        Audit(ctx, isNew ? "Created" : "Updated", nameof(Site), e.Id, $"Site '{e.Name}'");
        await SaveAsync(ctx);
        return e;
    }

    public async Task DeleteSiteAsync(int id)
    {
        await using var ctx = await WriteAsync();
        var e = await ctx.Sites.Include(s => s.Rooms).FirstOrDefaultAsync(s => s.Id == id) ?? throw new NotFoundException("Site");
        if (e.Rooms.Count > 0)
            throw new LinktraceException($"Site '{e.Name}' still has {e.Rooms.Count} room(s). Delete or move them first.");

        ctx.Sites.Remove(e);
        Audit(ctx, "Deleted", nameof(Site), e.Id, $"Site '{e.Name}'");
        await SaveAsync(ctx);
    }

    // ---- Rooms ----

    public async Task<Room> SaveRoomAsync(Room model)
    {
        await using var ctx = await WriteAsync();
        var isNew = model.Id == 0;
        Room e;
        if (isNew) { e = new Room(); ctx.Rooms.Add(e); }
        else
        {
            e = await ctx.Rooms.FindAsync(model.Id) ?? throw new NotFoundException("Room");
            ExpectVersion(ctx, e, model.Version);
        }

        if (!await ctx.Sites.AnyAsync(s => s.Id == model.SiteId)) throw new NotFoundException("Site");
        if (!isNew && e.SiteId != model.SiteId)
            throw new LinktraceException("Moving a room to another site isn't supported.");

        e.SiteId = model.SiteId;
        e.Name = model.Name.Trim();
        e.Floor = Clean(model.Floor);
        e.Notes = Clean(model.Notes);
        await SaveAsync(ctx);

        Audit(ctx, isNew ? "Created" : "Updated", nameof(Room), e.Id, $"Room '{e.Name}'");
        await SaveAsync(ctx);
        return e;
    }

    public async Task DeleteRoomAsync(int id)
    {
        await using var ctx = await WriteAsync();
        var e = await ctx.Rooms.Include(r => r.Racks).Include(r => r.Devices).FirstOrDefaultAsync(r => r.Id == id)
            ?? throw new NotFoundException("Room");
        if (e.Racks.Count > 0 || e.Devices.Count > 0)
            throw new LinktraceException($"Room '{e.Name}' still has {e.Racks.Count} rack(s) and {e.Devices.Count} device(s). Remove them first.");

        ctx.Rooms.Remove(e);
        Audit(ctx, "Deleted", nameof(Room), e.Id, $"Room '{e.Name}'");
        await SaveAsync(ctx);
    }

    // ---- Racks ----

    public async Task<Rack> SaveRackAsync(Rack model)
    {
        await using var ctx = await WriteAsync();
        var isNew = model.Id == 0;
        Rack e;
        if (isNew) { e = new Rack(); ctx.Racks.Add(e); }
        else
        {
            e = await ctx.Racks.Include(r => r.Devices).FirstOrDefaultAsync(r => r.Id == model.Id) ?? throw new NotFoundException("Rack");
            ExpectVersion(ctx, e, model.Version);
            if (e.RoomId != model.RoomId && e.Devices.Count > 0)
                throw new LinktraceException("Can't move a rack that contains devices to another room.");
            var tooHigh = e.Devices.FirstOrDefault(d => d.RackUnit + d.HeightU - 1 > model.HeightU);
            if (tooHigh is not null)
                throw new LinktraceException($"'{tooHigh.Name}' sits above U{model.HeightU}. Move it before shrinking the rack.");
        }

        if (!await ctx.Rooms.AnyAsync(r => r.Id == model.RoomId)) throw new NotFoundException("Room");

        e.RoomId = model.RoomId;
        e.Name = model.Name.Trim();
        e.HeightU = model.HeightU;
        e.Notes = Clean(model.Notes);
        await SaveAsync(ctx);

        Audit(ctx, isNew ? "Created" : "Updated", nameof(Rack), e.Id, $"Rack '{e.Name}'");
        await SaveAsync(ctx);
        return e;
    }

    public async Task DeleteRackAsync(int id)
    {
        await using var ctx = await WriteAsync();
        var e = await ctx.Racks.Include(r => r.Devices).FirstOrDefaultAsync(r => r.Id == id) ?? throw new NotFoundException("Rack");
        if (e.Devices.Count > 0)
            throw new LinktraceException($"Rack '{e.Name}' still has {e.Devices.Count} device(s). Move or delete them first.");

        ctx.Racks.Remove(e);
        Audit(ctx, "Deleted", nameof(Rack), e.Id, $"Rack '{e.Name}'");
        await SaveAsync(ctx);
    }
}
