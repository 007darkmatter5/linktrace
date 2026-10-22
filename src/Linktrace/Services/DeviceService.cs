using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public record DeviceListItem(
    int Id, string Name, DeviceKind Kind, string? Manufacturer, string? Model, string? ManagementAddress,
    int SiteId, string SiteName, int RoomId, string RoomName, int? RackId, string? RackName, int? RackUnit,
    int PortCount, int ConnectedCount);

public class DeviceFilter
{
    public string? Text { get; set; }
    public DeviceKind? Kind { get; set; }
    public int? SiteId { get; set; }
    public int? RoomId { get; set; }
    public int? RackId { get; set; }
}

/// <summary>How to bulk-create ports on a device.</summary>
public class PortGenerationRequest
{
    /// <summary>Text placed before the number, e.g. "Gi1/0/".</summary>
    public string? Prefix { get; set; }
    public int Start { get; set; } = 1;
    public int Count { get; set; } = 24;
    public MediaType MediaType { get; set; } = MediaType.RJ45;

    /// <summary>
    /// Create a front and a rear port for each number and pair them (patch panels,
    /// wall jacks, couplers). Signals pass through from front to rear.
    /// </summary>
    public bool FrontAndRearPairs { get; set; }
    public MediaType RearMediaType { get; set; } = MediaType.PunchDown;
}

/// <summary>Changes to apply to several ports at once. Null means "leave as is".</summary>
public class BulkPortChange
{
    public PortFace? Face { get; set; }
    public MediaType? MediaType { get; set; }
    /// <summary>When true, every selected port's description becomes <see cref="Description"/> (empty clears it).</summary>
    public bool SetDescription { get; set; }
    public string? Description { get; set; }

    public bool HasChanges => Face is not null || MediaType is not null || SetDescription;
}

/// <summary>Put a device in a rack, optionally at a rack position (lowest U it occupies) and with a new height in U.</summary>
public record RackPlacement(int DeviceId, int? RackUnit, int? HeightU = null);

public static class RackSlots
{
    /// <summary>
    /// The highest free position for a device <paramref name="height"/> U tall, scanning from the top of the
    /// rack down, or null if it doesn't fit anywhere.
    /// </summary>
    public static int? SuggestUnit(int rackHeight, IEnumerable<(int Bottom, int Height)> occupied, int height)
    {
        var taken = new HashSet<int>();
        foreach (var (bottom, h) in occupied)
            for (var u = bottom; u < bottom + Math.Max(1, h); u++) taken.Add(u);
        height = Math.Max(1, height);
        for (var bottom = rackHeight - height + 1; bottom >= 1; bottom--)
            if (Enumerable.Range(bottom, height).All(u => !taken.Contains(u))) return bottom;
        return null;
    }
}

public class DuplicateRequest
{
    /// <summary>Name of the (first) copy; further copies count up from it.</summary>
    public string Name { get; set; } = "";
    public int Copies { get; set; } = 1;
    public int RoomId { get; set; }
    public int? RackId { get; set; }
    /// <summary>Only used for a single copy (several copies can't share a rack position).</summary>
    public int? RackUnit { get; set; }
    public bool CopyPortDescriptions { get; set; }
    public bool CopyNotes { get; set; } = true;

    /// <summary>
    /// "SW01" → next is "SW02"; "J-2.14-09" ×3 → J-2.14-09, J-2.14-10, J-2.14-11 (zero padding kept);
    /// a name without a number gets "-2", "-3"… after the first.
    /// </summary>
    public static List<string> NamesFor(string first, int count)
    {
        first = first.Trim();
        var (prefix, number) = FaceplateLayout.Split(first);
        if (number is int n)
        {
            var digits = first.Length - prefix.Length;
            return Enumerable.Range(0, count).Select(i => prefix + (n + i).ToString().PadLeft(digits, '0')).ToList();
        }
        return Enumerable.Range(0, count).Select(i => i == 0 ? first : $"{first}-{i + 1}").ToList();
    }

    /// <summary>A sensible name for the next copy of <paramref name="source"/>.</summary>
    public static string Suggest(string source)
    {
        var (_, number) = FaceplateLayout.Split(source.Trim());
        return number is not null ? NamesFor(source, 2)[1] : $"{source.Trim()} copy";
    }
}

public class DeviceService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user) : ServiceBase(factory, user)
{
    public async Task<List<DeviceListItem>> SearchAsync(DeviceFilter filter)
    {
        await using var ctx = await ReadAsync();
        var q = ctx.Devices.AsNoTracking().AsQueryable();

        if (filter.Kind is { } kind) q = q.Where(d => d.Kind == kind);
        if (filter.RackId is { } rackId) q = q.Where(d => d.RackId == rackId);
        if (filter.RoomId is { } roomId) q = q.Where(d => d.RoomId == roomId);
        if (filter.SiteId is { } siteId) q = q.Where(d => d.Room.SiteId == siteId);
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var t = $"%{filter.Text.Trim()}%";
            q = q.Where(d => EF.Functions.Like(d.Name, t)
                || EF.Functions.Like(d.Model ?? "", t)
                || EF.Functions.Like(d.Manufacturer ?? "", t)
                || EF.Functions.Like(d.SerialNumber ?? "", t)
                || EF.Functions.Like(d.ManagementAddress ?? "", t));
        }

        return await q.OrderBy(d => d.Name)
            .Select(d => new DeviceListItem(
                d.Id, d.Name, d.Kind, d.Manufacturer, d.Model, d.ManagementAddress,
                d.Room.SiteId, d.Room.Site.Name, d.RoomId, d.Room.Name, d.RackId, d.Rack != null ? d.Rack.Name : null, d.RackUnit,
                d.Ports.Count, d.Ports.Count(p => p.CableId != null)))
            .ToListAsync();
    }

    /// <summary>Lightweight lookup for autocomplete pickers.</summary>
    public async Task<List<Device>> FindAsync(string? text, int max = 30)
    {
        await using var ctx = await ReadAsync();
        var q = ctx.Devices.AsNoTracking().Include(d => d.Room).ThenInclude(r => r.Site).Include(d => d.Rack).AsQueryable();
        if (!string.IsNullOrWhiteSpace(text))
            q = q.Where(d => EF.Functions.Like(d.Name, $"%{text.Trim()}%"));
        return await q.OrderBy(d => d.Name).Take(max).ToListAsync();
    }

    /// <summary>Device with location, ports, and the far end of every connected cable.</summary>
    public async Task<Device?> GetAsync(int id)
    {
        await using var ctx = await ReadAsync();
        // Identity resolution is required because Port → Cable → Ports loops back to the same ports.
        return await ctx.Devices.AsNoTrackingWithIdentityResolution()
            .Include(d => d.Room).ThenInclude(r => r.Site)
            .Include(d => d.Rack)
            .Include(d => d.FaceplateTemplate)
            .Include(d => d.Ports.OrderBy(p => p.Position)).ThenInclude(p => p.Cable).ThenInclude(c => c!.Ports).ThenInclude(p => p.Device)
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<List<Port>> GetPortsAsync(int deviceId)
    {
        await using var ctx = await ReadAsync();
        return await ctx.Ports.AsNoTracking().Where(p => p.DeviceId == deviceId).OrderBy(p => p.Position).ToListAsync();
    }

    public async Task<Device> SaveAsync(Device model)
    {
        await using var ctx = await WriteAsync();
        var isNew = model.Id == 0;
        Device e;
        if (isNew) { e = new Device(); ctx.Devices.Add(e); }
        else
        {
            e = await ctx.Devices.FindAsync(model.Id) ?? throw new NotFoundException("Device");
            ExpectVersion(ctx, e, model.Version);
        }

        await ValidatePlacementAsync(ctx, model.Id, model.RoomId, model.RackId, model.RackUnit, model.HeightU);

        e.Name = model.Name.Trim();
        e.Kind = model.Kind;
        // Reuse the existing spelling when the same name was already used ("ubiquiti" → "Ubiquiti").
        e.Manufacturer = await ExistingSpellingAsync(ctx, Clean(model.Manufacturer), ctx.Devices.Where(d => d.Id != e.Id).Select(d => d.Manufacturer));
        e.Model = await ExistingSpellingAsync(ctx, Clean(model.Model), ctx.Devices.Where(d => d.Id != e.Id).Select(d => d.Model));
        e.SerialNumber = Clean(model.SerialNumber);
        e.ManagementAddress = Clean(model.ManagementAddress);
        e.RoomId = model.RoomId;
        e.RackId = model.RackId;
        e.RackUnit = model.RackId is null ? null : model.RackUnit;
        e.HeightU = Math.Max(1, model.HeightU);
        e.FaceplateRows = Math.Clamp(model.FaceplateRows, 0, 8);
        e.FaceplateNumbering = model.FaceplateNumbering;
        e.FaceplateTemplateId = model.FaceplateTemplateId;
        // New devices pick up a hand-built layout made for their model.
        if (isNew && e.FaceplateTemplateId is null && e.Model is { } m)
        {
            var lower = m.ToLower();
            e.FaceplateTemplateId = await ctx.FaceplateTemplates.Where(t => t.Model != null && t.Model.ToLower() == lower)
                .Select(t => (int?)t.Id).FirstOrDefaultAsync();
        }
        e.Notes = Clean(model.Notes);
        await SaveAsync(ctx);

        Audit(ctx, isNew ? "Created" : "Updated", nameof(Device), e.Id, $"Device '{e.Name}'");
        await SaveAsync(ctx);
        return e;
    }

    /// <summary>Every manufacturer used so far, A–Z, with how many devices each has (for the picker).</summary>
    public async Task<List<(string Name, int Count)>> ManufacturersAsync()
    {
        await using var ctx = await ReadAsync();
        var rows = await ctx.Devices.Where(d => d.Manufacturer != null)
            .GroupBy(d => d.Manufacturer!).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        return rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => (r.Key, r.Count)).ToList();
    }

    /// <summary>Models used so far, those from <paramref name="manufacturer"/> first, A–Z.</summary>
    public async Task<List<(string Model, string? Manufacturer)>> ModelsAsync(string? manufacturer)
    {
        await using var ctx = await ReadAsync();
        var rows = await ctx.Devices.Where(d => d.Model != null)
            .Select(d => new { Model = d.Model!, d.Manufacturer }).Distinct().ToListAsync();
        var maker = manufacturer?.Trim();
        return rows
            .GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(r => string.IsNullOrEmpty(maker) || string.Equals(r.Manufacturer, maker, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
            .Select(r => (r.Model, r.Manufacturer)).ToList();
    }

    /// <summary>If <paramref name="value"/> matches an existing value ignoring case, returns the existing spelling.</summary>
    private static async Task<string?> ExistingSpellingAsync(ApplicationDbContext ctx, string? value, IQueryable<string?> existing)
    {
        if (value is null) return null;
        var lower = value.ToLower();
        return await existing.Where(v => v != null && v.ToLower() == lower).FirstOrDefaultAsync() ?? value;
    }

    /// <summary>Room must exist; rack must be in that room; the rack position must fit and not overlap another device.</summary>
    private static async Task ValidatePlacementAsync(ApplicationDbContext ctx, int deviceId, int roomId, int? rackId, int? rackUnit, int heightU)
    {
        if (!await ctx.Rooms.AnyAsync(r => r.Id == roomId)) throw new LinktraceException("Pick a room for the device.");
        if (rackId is not int rid) return;

        var rack = await ctx.Racks.Include(r => r.Devices).FirstOrDefaultAsync(r => r.Id == rid) ?? throw new NotFoundException("Rack");
        if (rack.RoomId != roomId) throw new LinktraceException("The selected rack is in a different room.");
        if (rackUnit is not int u) return;

        var top = u + heightU - 1;
        if (u < 1 || top > rack.HeightU)
            throw new LinktraceException($"Rack '{rack.Name}' is {rack.HeightU}U; the device doesn't fit at U{u}.");
        var clash = rack.Devices.FirstOrDefault(d => d.Id != deviceId && d.RackUnit is int du && du <= top && u <= du + d.HeightU - 1);
        if (clash is not null)
            throw new LinktraceException($"U{u} overlaps '{clash.Name}' in rack '{rack.Name}'.");
    }

    /// <summary>
    /// Copies a device: its details, ports (with front/rear pairings) and faceplate layout. Cables are not copied,
    /// since a port can only hold one. With several copies, names count up from <see cref="DuplicateRequest.Name"/>.
    /// </summary>
    public async Task<List<Device>> DuplicateAsync(int sourceId, DuplicateRequest req)
    {
        if (req.Copies is < 1 or > 100) throw new LinktraceException("You can make between 1 and 100 copies at a time.");
        if (string.IsNullOrWhiteSpace(req.Name)) throw new LinktraceException("Give the copy a name.");

        await using var ctx = await WriteAsync();
        var source = await ctx.Devices.AsNoTracking().Include(d => d.Ports).FirstOrDefaultAsync(d => d.Id == sourceId)
            ?? throw new NotFoundException("Device");

        var rackUnit = req.Copies == 1 && req.RackId is not null ? req.RackUnit : null;
        await ValidatePlacementAsync(ctx, 0, req.RoomId, req.RackId, rackUnit, source.HeightU);

        var names = DuplicateRequest.NamesFor(req.Name, req.Copies);
        var copies = new List<(Device Copy, Dictionary<int, Port> PortMap)>();
        foreach (var name in names)
        {
            var copy = new Device
            {
                Name = name,
                Kind = source.Kind,
                Manufacturer = source.Manufacturer,
                Model = source.Model,
                ManagementAddress = null,   // unique per device
                SerialNumber = null,        // unique per device
                RoomId = req.RoomId,
                RackId = req.RackId,
                RackUnit = rackUnit,
                HeightU = source.HeightU,
                FaceplateRows = source.FaceplateRows,
                FaceplateNumbering = source.FaceplateNumbering,
                FaceplateTemplateId = source.FaceplateTemplateId,
                Notes = req.CopyNotes ? source.Notes : null
            };
            var map = new Dictionary<int, Port>();
            foreach (var p in source.Ports.OrderBy(p => p.Position))
            {
                var np = new Port
                {
                    Name = p.Name, Face = p.Face, MediaType = p.MediaType, Position = p.Position,
                    Description = req.CopyPortDescriptions ? p.Description : null
                };
                copy.Ports.Add(np);
                map[p.Id] = np;
            }
            ctx.Devices.Add(copy);
            copies.Add((copy, map));
        }

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await SaveAsync(ctx);

        // Recreate pass-through pairings between the new ports.
        foreach (var (_, map) in copies)
            foreach (var p in source.Ports.Where(p => p.PairedPortId is int pid && map.ContainsKey(pid)))
                map[p.Id].PairedPortId = map[p.PairedPortId!.Value].Id;

        foreach (var (copy, _) in copies)
            Audit(ctx, "Created", nameof(Device), copy.Id, $"Device '{copy.Name}' (copy of '{source.Name}', {copy.Ports.Count} ports)");
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return copies.Select(c => c.Copy).ToList();
    }

    /// <summary>
    /// Devices that can be given a place in this rack: ones already in it without a position (listed first),
    /// then devices at the same site that aren't in it (unracked before racked elsewhere, then by name).
    /// </summary>
    public async Task<List<Device>> RackCandidatesAsync(int rackId)
    {
        await using var ctx = await ReadAsync();
        var rack = await ctx.Racks.AsNoTracking().Include(r => r.Room).FirstOrDefaultAsync(r => r.Id == rackId)
            ?? throw new NotFoundException("Rack");
        return await ctx.Devices.AsNoTracking()
            .Include(d => d.Room).Include(d => d.Rack)
            .Where(d => d.Room.SiteId == rack.Room.SiteId && (d.RackId != rackId || d.RackUnit == null))
            .OrderBy(d => d.RackId != rackId).ThenBy(d => d.RackId != null).ThenBy(d => d.RoomId != rack.RoomId).ThenBy(d => d.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Moves existing devices into a rack (and into the rack's room), each at its given position or unplaced.
    /// All or nothing: positions must fit the rack and not overlap each other or anything already there.
    /// </summary>
    public async Task<int> PlaceInRackAsync(int rackId, IReadOnlyList<RackPlacement> placements)
    {
        if (placements.Count == 0) return 0;
        await using var ctx = await WriteAsync();
        var rack = await ctx.Racks.Include(r => r.Room).Include(r => r.Devices).FirstOrDefaultAsync(r => r.Id == rackId)
            ?? throw new NotFoundException("Rack");
        var ids = placements.Select(p => p.DeviceId).Distinct().ToList();
        var devices = await ctx.Devices.Include(d => d.Room).Where(d => ids.Contains(d.Id)).ToListAsync();
        if (devices.Count != ids.Count) throw new NotFoundException("Device");

        // Apply any new heights first, so the checks below use them.
        foreach (var p in placements.Where(p => p.HeightU is not null))
        {
            if (p.HeightU is < 1 or > 60) throw new LinktraceException("A device's height must be between 1 and 60 U.");
            devices.Single(x => x.Id == p.DeviceId).HeightU = p.HeightU!.Value;
        }

        // Check positions against what's already in the rack and against each other.
        var occupied = rack.Devices.Where(d => !ids.Contains(d.Id) && d.RackUnit is not null)
            .Select(d => (d.Name, Bottom: d.RackUnit!.Value, Top: d.RackUnit.Value + d.HeightU - 1)).ToList();
        foreach (var p in placements.Where(p => p.RackUnit is not null))
        {
            var d = devices.Single(x => x.Id == p.DeviceId);
            var (bottom, top) = (p.RackUnit!.Value, p.RackUnit.Value + d.HeightU - 1);
            if (bottom < 1 || top > rack.HeightU)
                throw new LinktraceException($"'{d.Name}' ({d.HeightU}U) doesn't fit at U{bottom} in {rack.Name} ({rack.HeightU}U).");
            var clash = occupied.FirstOrDefault(o => o.Bottom <= top && bottom <= o.Top);
            if (clash.Name is not null)
                throw new LinktraceException($"'{d.Name}' at U{bottom} overlaps '{clash.Name}'.");
            occupied.Add((d.Name, bottom, top));
        }
        if (devices.Any(d => d.Room.SiteId != rack.Room.SiteId))
            throw new LinktraceException("Only devices from the rack's own site can be added.");

        foreach (var d in devices)
        {
            var p = placements.First(x => x.DeviceId == d.Id);
            var from = d.RoomId != rack.RoomId ? $" (moved from {d.Room.Name})" : "";
            d.RoomId = rack.RoomId;
            d.RackId = rack.Id;
            d.RackUnit = p.RackUnit;
            Audit(ctx, "Updated", nameof(Device), d.Id,
                $"Device '{d.Name}' ({d.HeightU}U) put in rack '{rack.Name}'{(p.RackUnit is int u ? $" at U{u}" : "")}{from}");
        }
        await SaveAsync(ctx);
        return devices.Count;
    }

    /// <summary>Takes a device out of its rack; it stays in the same room.</summary>
    public async Task RemoveFromRackAsync(int deviceId, Guid version)
    {
        await using var ctx = await WriteAsync();
        var d = await ctx.Devices.Include(x => x.Rack).FirstOrDefaultAsync(x => x.Id == deviceId) ?? throw new NotFoundException("Device");
        ExpectVersion(ctx, d, version);
        if (d.Rack is null) return;
        Audit(ctx, "Updated", nameof(Device), d.Id, $"Device '{d.Name}' taken out of rack '{d.Rack.Name}'");
        d.RackId = null;
        d.RackUnit = null;
        await SaveAsync(ctx);
    }

    /// <summary>Deletes the device, its ports, and any cables plugged into them.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var ctx = await WriteAsync();
        var e = await ctx.Devices.Include(d => d.Ports).FirstOrDefaultAsync(d => d.Id == id) ?? throw new NotFoundException("Device");

        await using var tx = await ctx.Database.BeginTransactionAsync();
        var cableIds = e.Ports.Where(p => p.CableId != null).Select(p => p.CableId!.Value).ToList();
        await RemoveCablesAsync(ctx, cableIds);

        ctx.Devices.Remove(e);
        Audit(ctx, "Deleted", nameof(Device), e.Id,
            $"Device '{e.Name}' with {e.Ports.Count} port(s); {cableIds.Count} cable(s) disconnected");
        await SaveAsync(ctx);
        await tx.CommitAsync();
    }

    // ---- Ports ----

    public async Task<int> AddPortsAsync(int deviceId, PortGenerationRequest req)
    {
        if (req.Count is < 1 or > 1000) throw new LinktraceException("Port count must be between 1 and 1000.");

        await using var ctx = await WriteAsync();
        var device = await ctx.Devices.Include(d => d.Ports).FirstOrDefaultAsync(d => d.Id == deviceId)
            ?? throw new NotFoundException("Device");

        var prefix = req.Prefix?.Trim() ?? "";
        var position = device.Ports.Count == 0 ? 0 : device.Ports.Max(p => p.Position);
        var existing = device.Ports.Select(p => (p.Face, p.Name.ToLowerInvariant())).ToHashSet();
        var created = new List<(Port A, Port? B)>();

        for (var n = req.Start; n < req.Start + req.Count; n++)
        {
            var name = $"{prefix}{n}";
            if (req.FrontAndRearPairs)
            {
                if (existing.Contains((PortFace.Front, name.ToLowerInvariant())) || existing.Contains((PortFace.Rear, name.ToLowerInvariant())))
                    throw new LinktraceException($"Port '{name}' already exists on this device.");
                var front = new Port { Name = name, Face = PortFace.Front, MediaType = req.MediaType, Position = ++position };
                var rear = new Port { Name = name, Face = PortFace.Rear, MediaType = req.RearMediaType, Position = ++position };
                device.Ports.Add(front);
                device.Ports.Add(rear);
                created.Add((front, rear));
            }
            else
            {
                if (existing.Contains((PortFace.Standard, name.ToLowerInvariant())))
                    throw new LinktraceException($"Port '{name}' already exists on this device.");
                var port = new Port { Name = name, Face = PortFace.Standard, MediaType = req.MediaType, Position = ++position };
                device.Ports.Add(port);
                created.Add((port, null));
            }
        }

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await SaveAsync(ctx);

        // Pair front/rear now that both have ids.
        foreach (var (a, b) in created.Where(x => x.B is not null))
        {
            a.PairedPortId = b!.Id;
            b.PairedPortId = a.Id;
        }

        var total = created.Count * (req.FrontAndRearPairs ? 2 : 1);
        Audit(ctx, "Created", nameof(Port), device.Id,
            $"Added {total} port(s) to '{device.Name}' ({prefix}{req.Start}–{prefix}{req.Start + req.Count - 1}{(req.FrontAndRearPairs ? ", front/rear pairs" : "")})");
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return total;
    }

    public async Task<Port> SavePortAsync(Port model)
    {
        await using var ctx = await WriteAsync();
        var isNew = model.Id == 0;
        Port e;
        if (isNew)
        {
            var maxPos = await ctx.Ports.Where(p => p.DeviceId == model.DeviceId).MaxAsync(p => (int?)p.Position) ?? 0;
            e = new Port { DeviceId = model.DeviceId, Position = maxPos + 1 };
            ctx.Ports.Add(e);
        }
        else
        {
            e = await ctx.Ports.FindAsync(model.Id) ?? throw new NotFoundException("Port");
            ExpectVersion(ctx, e, model.Version);
        }

        var device = await ctx.Devices.FindAsync(model.DeviceId) ?? throw new NotFoundException("Device");

        e.Name = model.Name.Trim();
        e.Face = model.Face;
        e.MediaType = model.MediaType;
        e.Description = Clean(model.Description);

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await SaveAsync(ctx);

        // Update the pass-through pairing on both sides.
        if (e.PairedPortId != model.PairedPortId)
        {
            if (model.PairedPortId == e.Id) throw new LinktraceException("A port can't be paired with itself.");

            if (e.PairedPortId is int oldPartnerId && await ctx.Ports.FindAsync(oldPartnerId) is { } oldPartner)
                oldPartner.PairedPortId = null;

            if (model.PairedPortId is int newPartnerId)
            {
                var partner = await ctx.Ports.FindAsync(newPartnerId) ?? throw new NotFoundException("Paired port");
                if (partner.DeviceId != e.DeviceId) throw new LinktraceException("Paired ports must be on the same device.");
                if (partner.PairedPortId is int theirOld && theirOld != e.Id && await ctx.Ports.FindAsync(theirOld) is { } theirOldPort)
                    theirOldPort.PairedPortId = null;
                partner.PairedPortId = e.Id;
            }
            e.PairedPortId = model.PairedPortId;
        }

        Audit(ctx, isNew ? "Created" : "Updated", nameof(Port), e.Id, $"Port {Display.Port(device.Name, e.Name, e.Face)}");
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return e;
    }

    /// <summary>
    /// Applies the same change to several ports of one device. All or nothing: if moving ports to another
    /// side would give two ports on that side the same name, nothing is changed. A hand-built faceplate used
    /// only by this device is updated so moved ports keep their place on it.
    /// </summary>
    public async Task<int> UpdatePortsAsync(int deviceId, IReadOnlyCollection<int> portIds, BulkPortChange change)
    {
        if (portIds.Count == 0 || !change.HasChanges) return 0;

        await using var ctx = await WriteAsync();
        var device = await ctx.Devices.Include(d => d.Ports).Include(d => d.FaceplateTemplate)
            .FirstOrDefaultAsync(d => d.Id == deviceId) ?? throw new NotFoundException("Device");
        var ports = device.Ports.Where(p => portIds.Contains(p.Id)).ToList();
        if (ports.Count != portIds.Distinct().Count()) throw new NotFoundException("Port");

        var oldKeys = ports.ToDictionary(p => p.Id, p => FaceplateLayout.Key(p.Face, p.Name));
        if (change.Face is PortFace face)
        {
            // Names must stay unique per side: check against ports staying put and among the moved ones.
            var staying = device.Ports.Where(p => !portIds.Contains(p.Id))
                .Select(p => FaceplateLayout.Key(p.Face, p.Name)).ToHashSet();
            var clashes = ports.Where(p => staying.Contains(FaceplateLayout.Key(face, p.Name)))
                .Select(p => p.Name)
                .Concat(ports.GroupBy(p => p.Name.Trim().ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.First().Name))
                .Distinct().ToList();
            if (clashes.Count > 0)
                throw new LinktraceException(
                    $"{Display.Face(face) switch { "" => "Standard", var f => char.ToUpper(f[0]) + f[1..] }} already has (or would get two) " +
                    $"port(s) named {string.Join(", ", clashes.Take(8))}{(clashes.Count > 8 ? "…" : "")}. Rename those first.");
            foreach (var p in ports) p.Face = face;
        }
        if (change.MediaType is MediaType media) foreach (var p in ports) p.MediaType = media;
        if (change.SetDescription)
        {
            var text = Clean(change.Description);
            foreach (var p in ports) p.Description = text;
        }

        // Keep a layout that only this device uses in step with the new sides.
        var layoutNote = "";
        if (change.Face is not null && device.FaceplateTemplate is { } t)
        {
            var users = await ctx.Devices.CountAsync(d => d.FaceplateTemplateId == t.Id);
            if (users == 1)
            {
                var moved = ports.ToDictionary(p => oldKeys[p.Id], p => p.Face);
                var newPlacements = t.Placements.Select(pl => new FaceplatePlacement
                {
                    PortName = pl.PortName, Col = pl.Col, Row = pl.Row, Span = pl.Span,
                    Face = moved.TryGetValue(FaceplateLayout.Key(pl.Face, pl.PortName), out var nf) ? nf : pl.Face
                }).ToList();
                t.Placements = newPlacements;
                ctx.Entry(t).State = EntityState.Modified;
            }
            else layoutNote = $"; layout '{t.Name}' is shared, so moved ports show under \"Not placed\" until it's updated";
        }

        var what = string.Join(", ", new[]
        {
            change.Face is PortFace f2 ? $"side → {(f2 == PortFace.Standard ? "standard" : Display.Face(f2))}" : null,
            change.MediaType is MediaType m2 ? $"connector → {Display.Media(m2)}" : null,
            change.SetDescription ? (Clean(change.Description) is { } d2 ? $"description → \"{d2}\"" : "description cleared") : null
        }.Where(s => s is not null));
        Audit(ctx, "Updated", nameof(Port), ports.Count == 1 ? ports[0].Id : null,
            $"{ports.Count} port(s) on '{device.Name}': {what}{layoutNote}");
        await SaveAsync(ctx);
        return ports.Count;
    }

    public async Task DeletePortsAsync(IEnumerable<int> portIds)
    {
        var ids = portIds.Distinct().ToList();
        if (ids.Count == 0) return;

        await using var ctx = await WriteAsync();
        var ports = await ctx.Ports.Include(p => p.Device).Where(p => ids.Contains(p.Id)).ToListAsync();
        if (ports.Count == 0) throw new NotFoundException("Port");

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await RemoveCablesAsync(ctx, ports.Where(p => p.CableId != null).Select(p => p.CableId!.Value).ToList());

        var partnerIds = ports.Where(p => p.PairedPortId != null).Select(p => p.PairedPortId!.Value).Except(ids).ToList();
        foreach (var partner in await ctx.Ports.Where(p => partnerIds.Contains(p.Id)).ToListAsync())
            partner.PairedPortId = null;

        ctx.Ports.RemoveRange(ports);
        var names = string.Join(", ", ports.Take(10).Select(Display.Port));
        Audit(ctx, "Deleted", nameof(Port), ports.Count == 1 ? ports[0].Id : null,
            ports.Count == 1 ? $"Port {names}" : $"{ports.Count} ports: {names}{(ports.Count > 10 ? "…" : "")}");
        await SaveAsync(ctx);
        await tx.CommitAsync();
    }

    /// <summary>Unplug and delete cables, logging each one.</summary>
    internal static async Task RemoveCablesAsync(ApplicationDbContext ctx, List<int> cableIds)
    {
        if (cableIds.Count == 0) return;
        var cables = await ctx.Cables.Include(c => c.Ports).ThenInclude(p => p.Device)
            .Where(c => cableIds.Contains(c.Id)).ToListAsync();
        foreach (var cable in cables)
        {
            Audit(ctx, "Disconnected", nameof(Cable), cable.Id, CableService.Describe(cable));
            foreach (var p in cable.Ports) p.CableId = null;
            ctx.Cables.Remove(cable);
        }
    }
}
