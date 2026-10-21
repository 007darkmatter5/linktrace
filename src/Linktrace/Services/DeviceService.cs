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
        e.Manufacturer = Clean(model.Manufacturer);
        e.Model = Clean(model.Model);
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
