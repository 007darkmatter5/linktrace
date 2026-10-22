using System.Text.Json;
using System.Text.Json.Serialization;
using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

// ---------------- File format ----------------
// One JSON document with flat lists. Records reference each other by the ids they had in the
// exporting instance; the importer gives everything new ids and rewires the references.

public record ExportFile
{
    public const string FormatName = "linktrace";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public DateTime ExportedUtc { get; init; }
    public string? ExportedBy { get; init; }

    public List<SiteDto> Sites { get; init; } = [];
    public List<RoomDto> Rooms { get; init; } = [];
    public List<RackDto> Racks { get; init; } = [];
    public List<FaceplateTemplateDto> FaceplateTemplates { get; init; } = [];
    public List<DeviceDto> Devices { get; init; } = [];
    public List<PortDto> Ports { get; init; } = [];
    public List<CableDto> Cables { get; init; } = [];
    public List<PortMapDto> PortMaps { get; init; } = [];
    /// <summary>Added later; older files simply have none.</summary>
    public List<DeviceTemplateDto> DeviceTemplates { get; init; } = [];
}

public record SiteDto(int Id, string Name, string? Address, string? Notes);
public record RoomDto(int Id, int SiteId, string Name, string? Floor, string? Notes);
public record RackDto(int Id, int RoomId, string Name, int HeightU, string? Notes);
public record FaceplateTemplateDto(int Id, string Name, string? Model, int Columns, int Rows,
    List<FaceplatePlacement> Placements, List<FaceplateLabel> Labels);
public record DeviceDto(int Id, string Name, DeviceKind Kind, string? Manufacturer, string? Model, string? SerialNumber,
    string? ManagementAddress, int RoomId, int? RackId, int? RackUnit, int HeightU, int FaceplateRows,
    PortNumbering FaceplateNumbering, int? FaceplateTemplateId, string? Notes);
public record PortDto(int Id, int DeviceId, string Name, int Position, PortFace Face, MediaType MediaType,
    string? Description, int? PairedPortId);
public record CableDto(int Id, string? Label, CableType CableType, string? Color, decimal? LengthMeters, string? Notes,
    int PortAId, int PortBId);
/// <summary>A device template in a whole-install export; its layout is referenced by id in <see cref="ExportFile.FaceplateTemplates"/>.</summary>
public record DeviceTemplateDto(string Name, DeviceKind Kind, string? Manufacturer, string? Model, int HeightU, int FaceplateRows,
    PortNumbering FaceplateNumbering, string? Notes, int? FaceplateTemplateId, List<TemplatePortDto> Ports);

public record PortMapDto(int Id, string Name, string? Notes, List<int> DeviceIds, bool FollowPassThrough, bool ShowExternal,
    List<int>? Columns = null, List<int>? Spans = null); // added later; missing in older files means one device per row, automatic spans

public enum ImportMode
{
    /// <summary>Keep what's here and add the file's contents. Clashing names get "(imported)" added.</summary>
    Merge,
    /// <summary>Delete all network data here first, then load the file.</summary>
    Replace
}

public record ImportPreview(ExportFile File, Dictionary<string, int> Counts, List<string> Problems, List<string> MergeRenames)
{
    public bool CanImport => Problems.Count == 0;
}

public record ImportResult(Dictionary<string, int> Counts, List<string> Renamed);

/// <summary>Exports all network data to a single JSON file and imports such files into this instance.</summary>
public class TransferService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user) : ServiceBase(factory, user)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---------------- Export ----------------

    /// <summary>Everything except user accounts and the change log.</summary>
    public async Task<ExportFile> BuildExportAsync(string? exportedBy)
    {
        await using var ctx = await ReadAsync();
        return new ExportFile
        {
            ExportedUtc = DateTime.UtcNow,
            ExportedBy = exportedBy,
            Sites = await ctx.Sites.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new SiteDto(x.Id, x.Name, x.Address, x.Notes)).ToListAsync(),
            Rooms = await ctx.Rooms.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new RoomDto(x.Id, x.SiteId, x.Name, x.Floor, x.Notes)).ToListAsync(),
            Racks = await ctx.Racks.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new RackDto(x.Id, x.RoomId, x.Name, x.HeightU, x.Notes)).ToListAsync(),
            FaceplateTemplates = (await ctx.FaceplateTemplates.AsNoTracking().OrderBy(x => x.Id).ToListAsync())
                .Select(x => new FaceplateTemplateDto(x.Id, x.Name, x.Model, x.Columns, x.Rows, x.Placements, x.Labels)).ToList(),
            Devices = await ctx.Devices.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new DeviceDto(x.Id, x.Name, x.Kind, x.Manufacturer, x.Model, x.SerialNumber, x.ManagementAddress,
                    x.RoomId, x.RackId, x.RackUnit, x.HeightU, x.FaceplateRows, x.FaceplateNumbering, x.FaceplateTemplateId, x.Notes))
                .ToListAsync(),
            Ports = await ctx.Ports.AsNoTracking().OrderBy(x => x.DeviceId).ThenBy(x => x.Position)
                .Select(x => new PortDto(x.Id, x.DeviceId, x.Name, x.Position, x.Face, x.MediaType, x.Description, x.PairedPortId))
                .ToListAsync(),
            Cables = (await ctx.Cables.AsNoTracking().Include(c => c.Ports).OrderBy(x => x.Id).ToListAsync())
                .Where(c => c.Ports.Count == 2)
                .Select(c =>
                {
                    var ends = c.Ports.OrderBy(p => p.Id).ToList();
                    return new CableDto(c.Id, c.Label, c.CableType, c.Color, c.LengthMeters, c.Notes, ends[0].Id, ends[1].Id);
                }).ToList(),
            PortMaps = await ctx.PortMapViews.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new PortMapDto(x.Id, x.Name, x.Notes, x.DeviceIds, x.FollowPassThrough, x.ShowExternal, x.Columns, x.Spans)).ToListAsync(),
            DeviceTemplates = (await ctx.DeviceTemplates.AsNoTracking().OrderBy(x => x.Name).ToListAsync())
                .Select(t => new DeviceTemplateDto(t.Name, t.Kind, t.Manufacturer, t.Model, t.HeightU, t.FaceplateRows, t.FaceplateNumbering,
                    t.Notes, t.FaceplateTemplateId,
                    t.Ports.OrderBy(p => p.Position).Select(p => new TemplatePortDto(p.Name, p.Face, p.MediaType, p.Position, p.Description, p.PairName, p.PairFace)).ToList()))
                .ToList()
        };
    }

    public async Task<byte[]> ExportJsonAsync(string? exportedBy) =>
        JsonSerializer.SerializeToUtf8Bytes(await BuildExportAsync(exportedBy), Json);

    public static string FileName(DateTime utc) => $"linktrace-export-{utc:yyyyMMdd-HHmm}.json";

    // ---------------- Import ----------------

    /// <summary>Reads and checks a file without changing anything.</summary>
    public async Task<ImportPreview> PreviewAsync(Stream json)
    {
        ExportFile? file;
        try
        {
            file = await JsonSerializer.DeserializeAsync<ExportFile>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new LinktraceException($"That file isn't a valid Linktrace export ({ex.Message}).");
        }
        if (file is null || file.Format != ExportFile.FormatName)
            throw new LinktraceException("That file isn't a Linktrace export.");
        if (file.Version > ExportFile.CurrentVersion)
            throw new LinktraceException("That file came from a newer version of Linktrace. Update this instance first.");

        var problems = Validate(file);
        var counts = Counts(file);

        await using var ctx = await ReadAsync();
        var renames = new List<string>();
        void Clash(string what, IEnumerable<string> incoming, IEnumerable<string> existing)
        {
            var here = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
            renames.AddRange(incoming.Where(here.Contains).Select(n => $"{what} '{n}'"));
        }
        Clash("Site", file.Sites.Select(s => s.Name), await ctx.Sites.Select(s => s.Name).ToListAsync());
        Clash("Faceplate layout", file.FaceplateTemplates.Select(t => t.Name), await ctx.FaceplateTemplates.Select(t => t.Name).ToListAsync());
        Clash("Port map", file.PortMaps.Select(m => m.Name), await ctx.PortMapViews.Select(m => m.Name).ToListAsync());
        Clash("Device template", file.DeviceTemplates.Select(t => t.Name), await ctx.DeviceTemplates.Select(t => t.Name).ToListAsync());
        Clash("Cable label", file.Cables.Where(c => c.Label != null).Select(c => c.Label!),
            await ctx.Cables.Where(c => c.Label != null).Select(c => c.Label!).ToListAsync());

        return new ImportPreview(file, counts, problems, renames);
    }

    /// <summary>Loads a checked file in one transaction: either everything is imported or nothing is.</summary>
    public async Task<ImportResult> ImportAsync(ExportFile file, ImportMode mode)
    {
        if (!await CurrentUser.IsAdminAsync()) throw new LinktraceException("Only admins can import data.");
        var problems = Validate(file);
        if (problems.Count > 0) throw new LinktraceException($"The file has problems: {problems[0]}");

        await using var ctx = await WriteAsync();
        await using var tx = await ctx.Database.BeginTransactionAsync();

        if (mode == ImportMode.Replace)
        {
            // Order matters: ports reference cables, devices reference templates and racks, etc.
            await ctx.Ports.ExecuteUpdateAsync(s => s.SetProperty(p => p.CableId, (int?)null));
            await ctx.Cables.ExecuteDeleteAsync();
            await ctx.Ports.ExecuteDeleteAsync();
            await ctx.Devices.ExecuteDeleteAsync();
            await ctx.DeviceTemplates.ExecuteDeleteAsync();
            await ctx.FaceplateTemplates.ExecuteDeleteAsync();
            await ctx.Racks.ExecuteDeleteAsync();
            await ctx.Rooms.ExecuteDeleteAsync();
            await ctx.Sites.ExecuteDeleteAsync();
            await ctx.PortMapViews.ExecuteDeleteAsync();
        }

        var renamed = new List<string>();
        var siteNames = (await ctx.Sites.Select(s => s.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templateNames = (await ctx.FaceplateTemplates.Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapNames = (await ctx.PortMapViews.Select(m => m.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deviceTemplateNames = (await ctx.DeviceTemplates.Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var labels = (await ctx.Cables.Where(c => c.Label != null).Select(c => c.Label!).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string Unique(string what, string name, HashSet<string> taken)
        {
            var result = name;
            for (var i = 1; taken.Contains(result); i++)
                result = i == 1 ? $"{name} (imported)" : $"{name} (imported {i})";
            if (result != name) renamed.Add($"{what} '{name}' → '{result}'");
            taken.Add(result);
            return result;
        }

        // 1. Sites, rooms, racks, layouts (saved in stages so new ids are known).
        var sites = file.Sites.ToDictionary(s => s.Id, s => new Site
        { Name = Unique("Site", s.Name.Trim(), siteNames), Address = s.Address, Notes = s.Notes });
        ctx.Sites.AddRange(sites.Values);
        var templates = file.FaceplateTemplates.ToDictionary(t => t.Id, t => new FaceplateTemplate
        {
            Name = Unique("Faceplate layout", t.Name.Trim(), templateNames), Model = t.Model, Columns = t.Columns, Rows = t.Rows,
            Placements = t.Placements ?? [], Labels = t.Labels ?? []
        });
        ctx.FaceplateTemplates.AddRange(templates.Values);
        await SaveAsync(ctx);

        var rooms = file.Rooms.ToDictionary(r => r.Id, r => new Room
        { SiteId = sites[r.SiteId].Id, Name = r.Name, Floor = r.Floor, Notes = r.Notes });
        ctx.Rooms.AddRange(rooms.Values);
        await SaveAsync(ctx);

        var racks = file.Racks.ToDictionary(k => k.Id, k => new Rack
        { RoomId = rooms[k.RoomId].Id, Name = k.Name, HeightU = k.HeightU, Notes = k.Notes });
        ctx.Racks.AddRange(racks.Values);
        await SaveAsync(ctx);

        // 2. Devices and their ports.
        var devices = file.Devices.ToDictionary(d => d.Id, d => new Device
        {
            Name = d.Name, Kind = d.Kind, Manufacturer = d.Manufacturer, Model = d.Model, SerialNumber = d.SerialNumber,
            ManagementAddress = d.ManagementAddress, RoomId = rooms[d.RoomId].Id,
            RackId = d.RackId is int rk ? racks[rk].Id : null, RackUnit = d.RackId is null ? null : d.RackUnit,
            HeightU = Math.Max(1, d.HeightU), FaceplateRows = d.FaceplateRows, FaceplateNumbering = d.FaceplateNumbering,
            FaceplateTemplateId = d.FaceplateTemplateId is int ft ? templates[ft].Id : null, Notes = d.Notes
        });
        ctx.Devices.AddRange(devices.Values);
        await SaveAsync(ctx);

        var ports = file.Ports.ToDictionary(p => p.Id, p => new Port
        {
            DeviceId = devices[p.DeviceId].Id, Name = p.Name, Position = p.Position, Face = p.Face,
            MediaType = p.MediaType, Description = p.Description
        });
        ctx.Ports.AddRange(ports.Values);
        await SaveAsync(ctx);

        // 3. Pass-through pairs and cables, now that every port has its new id.
        foreach (var p in file.Ports.Where(p => p.PairedPortId is int pid && ports.ContainsKey(pid)))
            ports[p.Id].PairedPortId = ports[p.PairedPortId!.Value].Id;

        foreach (var c in file.Cables)
        {
            var cable = new Cable
            {
                Label = c.Label is null ? null : Unique("Cable label", c.Label.Trim(), labels),
                CableType = c.CableType, Color = c.Color, LengthMeters = c.LengthMeters, Notes = c.Notes
            };
            ctx.Cables.Add(cable);
            ports[c.PortAId].Cable = cable;
            ports[c.PortBId].Cable = cable;
        }
        await SaveAsync(ctx);

        // 4. Saved port maps, pointing at the new device ids.
        foreach (var m in file.PortMaps)
        {
            // Keep each device's column alongside it while dropping devices that aren't in the file.
            var slots = m.DeviceIds
                .Select((id, i) => (Id: id,
                    Column: m.Columns is { } cols && i < cols.Count ? cols[i] : 0,
                    Span: m.Spans is { } spans && i < spans.Count ? spans[i] : 0))
                .Where(s => devices.ContainsKey(s.Id)).ToList();
            ctx.PortMapViews.Add(new PortMapView
            {
                Name = Unique("Port map", m.Name.Trim(), mapNames), Notes = m.Notes,
                DeviceIds = slots.Select(s => devices[s.Id].Id).ToList(),
                Columns = PortMapLayout.Normalize(slots.Select(s => s.Column).ToList(), slots.Count),
                Spans = PortMapLayout.NormalizeSpans(slots.Select(s => s.Span).ToList(), slots.Count),
                FollowPassThrough = m.FollowPassThrough, ShowExternal = m.ShowExternal
            });
        }

        // 5. Device templates, pointing at their imported layouts.
        foreach (var dt in file.DeviceTemplates)
            ctx.DeviceTemplates.Add(new DeviceTemplate
            {
                Name = Unique("Device template", dt.Name.Trim(), deviceTemplateNames), Kind = dt.Kind, Manufacturer = dt.Manufacturer,
                Model = dt.Model, HeightU = Math.Clamp(dt.HeightU, 1, 60), FaceplateRows = Math.Clamp(dt.FaceplateRows, 0, 8),
                FaceplateNumbering = dt.FaceplateNumbering, Notes = dt.Notes,
                FaceplateTemplate = dt.FaceplateTemplateId is int lid ? templates[lid] : null,
                Ports = dt.Ports.Select(p => new TemplatePort
                {
                    Name = p.Name, Face = p.Face, MediaType = p.MediaType, Position = p.Position,
                    Description = p.Description, PairName = p.PairName, PairFace = p.PairFace
                }).ToList()
            });

        var counts = Counts(file);
        Audit(ctx, "Imported", "Data", null,
            $"{(mode == ImportMode.Replace ? "Replaced all data with" : "Added")} an export from {file.ExportedUtc:yyyy-MM-dd HH:mm} UTC"
            + (file.ExportedBy is null ? "" : $" by {file.ExportedBy}") + ": "
            + string.Join(", ", counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {kv.Key.ToLowerInvariant()}"))
            + (renamed.Count > 0 ? $"; {renamed.Count} renamed" : ""));
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return new ImportResult(counts, renamed);
    }

    private static Dictionary<string, int> Counts(ExportFile f) => new()
    {
        ["Sites"] = f.Sites.Count, ["Rooms"] = f.Rooms.Count, ["Racks"] = f.Racks.Count, ["Devices"] = f.Devices.Count,
        ["Ports"] = f.Ports.Count, ["Cables"] = f.Cables.Count, ["Faceplate layouts"] = f.FaceplateTemplates.Count,
        ["Port maps"] = f.PortMaps.Count, ["Device templates"] = f.DeviceTemplates.Count
    };

    /// <summary>Checks every reference in the file points at something in the file, and the basic rules hold.</summary>
    public static List<string> Validate(ExportFile f)
    {
        var problems = new List<string>();
        void Check(bool ok, string message) { if (!ok && problems.Count < 50) problems.Add(message); }

        HashSet<int> Ids<T>(List<T> items, Func<T, int> id, string what)
        {
            var set = new HashSet<int>();
            foreach (var i in items) Check(set.Add(id(i)), $"{what} id {id(i)} appears twice.");
            return set;
        }
        var sites = Ids(f.Sites, s => s.Id, "Site");
        var rooms = Ids(f.Rooms, r => r.Id, "Room");
        var racks = Ids(f.Racks, r => r.Id, "Rack");
        var templates = Ids(f.FaceplateTemplates, t => t.Id, "Faceplate layout");
        var devices = Ids(f.Devices, d => d.Id, "Device");
        Ids(f.Cables, c => c.Id, "Cable");
        var portDevice = new Dictionary<int, int>();
        foreach (var p in f.Ports) Check(portDevice.TryAdd(p.Id, p.DeviceId), $"Port id {p.Id} appears twice.");

        foreach (var s in f.Sites) Check(!string.IsNullOrWhiteSpace(s.Name), $"Site {s.Id} has no name.");
        foreach (var r in f.Rooms) Check(sites.Contains(r.SiteId), $"Room '{r.Name}' points at a missing site.");
        foreach (var k in f.Racks) Check(rooms.Contains(k.RoomId), $"Rack '{k.Name}' points at a missing room.");
        foreach (var d in f.Devices)
        {
            Check(!string.IsNullOrWhiteSpace(d.Name), $"Device {d.Id} has no name.");
            Check(rooms.Contains(d.RoomId), $"Device '{d.Name}' points at a missing room.");
            Check(d.RackId is null || racks.Contains(d.RackId.Value), $"Device '{d.Name}' points at a missing rack.");
            Check(d.FaceplateTemplateId is null || templates.Contains(d.FaceplateTemplateId.Value), $"Device '{d.Name}' points at a missing faceplate layout.");
        }
        foreach (var p in f.Ports)
        {
            Check(devices.Contains(p.DeviceId), $"Port '{p.Name}' points at a missing device.");
            Check(p.PairedPortId is null || (portDevice.TryGetValue(p.PairedPortId.Value, out var pd) && pd == p.DeviceId),
                $"Port '{p.Name}' is paired with a port that's missing or on another device.");
        }
        foreach (var dup in f.Ports.GroupBy(p => (p.DeviceId, p.Face, Name: p.Name.ToLowerInvariant())).Where(g => g.Count() > 1))
            Check(false, $"Device {dup.Key.DeviceId} has two {dup.Key.Face} ports named '{dup.First().Name}'.");

        var cabled = new HashSet<int>();
        foreach (var c in f.Cables)
        {
            Check(c.PortAId != c.PortBId, $"Cable {c.Label ?? c.Id.ToString()} connects a port to itself.");
            foreach (var pid in new[] { c.PortAId, c.PortBId })
            {
                Check(portDevice.ContainsKey(pid), $"Cable {c.Label ?? c.Id.ToString()} points at a missing port.");
                Check(cabled.Add(pid), $"Port {pid} has more than one cable.");
            }
        }
        foreach (var m in f.PortMaps) Check(!string.IsNullOrWhiteSpace(m.Name), $"Port map {m.Id} has no name.");
        foreach (var dt in f.DeviceTemplates)
        {
            Check(dt.FaceplateTemplateId is null || templates.Contains(dt.FaceplateTemplateId.Value), $"Device template '{dt.Name}' points at a missing faceplate layout.");
            foreach (var p in DeviceTemplateService.Validate(new TemplateDto(dt.Name, dt.Kind, dt.Manufacturer, dt.Model, dt.HeightU,
                         dt.FaceplateRows, dt.FaceplateNumbering, dt.Notes, dt.Ports, null)))
                Check(false, p);
        }
        return problems;
    }
}
