using System.Text.Json;
using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

// ---------------- Template file format ----------------
// A small JSON document holding one or more device templates, each with its faceplate layout embedded,
// so it can be shared on its own (email, forum, a folder) and imported into any Linktrace install.

public record TemplateFile
{
    public const string FormatName = "linktrace-template";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = CurrentVersion;
    public DateTime ExportedUtc { get; init; }
    public string? ExportedBy { get; init; }
    public List<TemplateDto> Templates { get; init; } = [];
}

public record TemplateDto(string Name, DeviceKind Kind, string? Manufacturer, string? Model, int HeightU,
    int FaceplateRows, PortNumbering FaceplateNumbering, string? Notes, List<TemplatePortDto> Ports, TemplateLayoutDto? Layout);

public record TemplatePortDto(string Name, PortFace Face, MediaType MediaType, int Position, string? Description,
    string? PairName = null, PortFace? PairFace = null);

public record TemplateLayoutDto(string Name, string? Model, int Columns, int Rows, List<FaceplatePlacement> Placements, List<FaceplateLabel> Labels);

public record DeviceTemplateInfo(int Id, string Name, DeviceKind Kind, string? Manufacturer, string? Model, int HeightU,
    int PortCount, string PortSummary, string? LayoutName, DateTime UpdatedUtc, string? UpdatedBy);

/// <summary>What updating one device to match a template would change.</summary>
public record TemplateSyncPlan(int DeviceId, string DeviceName, string? Model, bool SameModel,
    List<string> AddPorts, List<string> ChangePorts, List<string> ExtraPorts, List<string> ExtraCabledPorts, List<string> DetailChanges)
{
    public bool HasChanges => AddPorts.Count + ChangePorts.Count + DetailChanges.Count > 0;
}

public record TemplateImportPreview(TemplateFile File, List<string> Problems, List<string> Renames)
{
    public bool CanImport => Problems.Count == 0 && File.Templates.Count > 0;
}

/// <summary>Device templates: save a device as one, create devices from one, and share them as files.</summary>
public class DeviceTemplateService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user, DeviceService devices)
    : ServiceBase(factory, user)
{
    public async Task<List<DeviceTemplateInfo>> GetAllAsync()
    {
        await using var ctx = await ReadAsync();
        var templates = await ctx.DeviceTemplates.AsNoTracking().Include(t => t.FaceplateTemplate).OrderBy(t => t.Name).ToListAsync();
        return templates.Select(t => new DeviceTemplateInfo(t.Id, t.Name, t.Kind, t.Manufacturer, t.Model, t.HeightU,
            t.Ports.Count, Summarize(t.Ports), t.FaceplateTemplate?.Name, t.UpdatedUtc, t.UpdatedBy)).ToList();
    }

    public async Task<DeviceTemplate?> GetAsync(int id)
    {
        await using var ctx = await ReadAsync();
        return await ctx.DeviceTemplates.AsNoTracking().Include(t => t.FaceplateTemplate).FirstOrDefaultAsync(t => t.Id == id);
    }

    /// <summary>"24 × RJ45, 2 × USB-C, 8 × Outlet (NEMA 5-15)"</summary>
    public static string Summarize(IEnumerable<TemplatePort> ports) => string.Join(", ",
        ports.GroupBy(p => p.MediaType).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} × {Display.Media(g.Key)}"));

    /// <summary>
    /// Saves a device's model, ports and faceplate layout as a template. Nothing specific to the one device
    /// is kept (name, serial, address, location, cables; port descriptions only if asked).
    /// </summary>
    public async Task<DeviceTemplate> SaveFromDeviceAsync(int deviceId, string name, bool includeDescriptions, bool replaceExisting)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new LinktraceException("Give the template a name.");
        await using var ctx = await WriteAsync();
        var device = await ctx.Devices.AsNoTracking().Include(d => d.Ports).FirstOrDefaultAsync(d => d.Id == deviceId)
            ?? throw new NotFoundException("Device");

        name = name.Trim();
        var lower = name.ToLower();
        var t = await ctx.DeviceTemplates.FirstOrDefaultAsync(x => x.Name.ToLower() == lower);
        var isNew = t is null;
        if (t is not null && !replaceExisting)
            throw new LinktraceException($"There's already a template called '{t.Name}'. Pick another name, or choose to replace it.");
        if (t is null) { t = new DeviceTemplate(); ctx.DeviceTemplates.Add(t); }

        t.Name = name;
        t.Kind = device.Kind;
        t.Manufacturer = device.Manufacturer;
        t.Model = device.Model;
        t.HeightU = Math.Max(1, device.HeightU);
        t.FaceplateRows = device.FaceplateRows;
        t.FaceplateNumbering = device.FaceplateNumbering;
        t.FaceplateTemplateId = device.FaceplateTemplateId;
        t.Ports = ToTemplatePorts(device.Ports, includeDescriptions);
        if (!isNew) ctx.Entry(t).State = EntityState.Modified;

        await SaveAsync(ctx);
        Audit(ctx, isNew ? "Created" : "Updated", "Template", t.Id, $"Device template '{t.Name}' from '{device.Name}' ({t.Ports.Count} ports)");
        await SaveAsync(ctx);
        return t;
    }

    private static List<TemplatePort> ToTemplatePorts(IEnumerable<Port> ports, bool includeDescriptions)
    {
        var list = ports.OrderBy(p => p.Position).ToList();
        var byId = list.ToDictionary(p => p.Id);
        return list.Select((p, i) =>
        {
            var partner = p.PairedPortId is int pid && byId.TryGetValue(pid, out var pp) ? pp : null;
            return new TemplatePort
            {
                Name = p.Name, Face = p.Face, MediaType = p.MediaType, Position = i + 1,
                Description = includeDescriptions ? p.Description : null,
                PairName = partner?.Name, PairFace = partner?.Face
            };
        }).ToList();
    }

    /// <summary>Creates a device from a template: the device's own details from the form, ports and layout from the template.</summary>
    public async Task<Device> CreateDeviceAsync(Device model, int templateId)
    {
        var template = await GetAsync(templateId) ?? throw new NotFoundException("Template");
        model.FaceplateTemplateId ??= template.FaceplateTemplateId;
        var device = await devices.SaveAsync(model);

        try
        {
            await using var ctx = await WriteAsync();
            var ports = template.Ports.OrderBy(p => p.Position).Select((p, i) => new Port
            {
                DeviceId = device.Id, Name = p.Name, Face = p.Face, MediaType = p.MediaType, Position = i + 1, Description = p.Description
            }).ToList();
            ctx.Ports.AddRange(ports);
            await SaveAsync(ctx);

            // Recreate front/rear pairs now that the ports have ids.
            var byKey = ports.ToDictionary(p => FaceplateLayout.Key(p.Face, p.Name));
            foreach (var tp in template.Ports.Where(tp => tp.PairName is not null && tp.PairFace is not null))
                if (byKey.TryGetValue(FaceplateLayout.Key(tp.Face, tp.Name), out var a) &&
                    byKey.TryGetValue(FaceplateLayout.Key(tp.PairFace!.Value, tp.PairName!), out var b))
                    a.PairedPortId = b.Id;

            Audit(ctx, "Created", nameof(Device), device.Id, $"Device '{device.Name}' from template '{template.Name}' ({ports.Count} ports)");
            await SaveAsync(ctx);
        }
        catch
        {
            await devices.DeleteAsync(device.Id); // don't leave a half-made device behind
            throw;
        }
        return device;
    }

    // ---------------- Bringing existing devices in line with a template ----------------

    /// <summary>A template made for this device's model, if there is one (most recently changed first).</summary>
    public async Task<DeviceTemplateInfo?> FindForModelAsync(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var lower = model.Trim().ToLower();
        return (await GetAllAsync()).Where(t => t.Model?.ToLower() == lower).OrderByDescending(t => t.UpdatedUtc).FirstOrDefault();
    }

    /// <summary>
    /// Works out what updating devices to match a template would do: devices with the template's model,
    /// plus any extra devices asked for. Nothing is changed.
    /// </summary>
    public async Task<List<TemplateSyncPlan>> PlanSyncAsync(int templateId, IReadOnlyCollection<int>? alsoDeviceIds = null)
    {
        await using var ctx = await ReadAsync();
        var t = await ctx.DeviceTemplates.AsNoTracking().Include(x => x.FaceplateTemplate).FirstOrDefaultAsync(x => x.Id == templateId)
            ?? throw new NotFoundException("Template");
        var model = t.Model?.Trim().ToLower();
        var extra = alsoDeviceIds?.ToList() ?? [];
        var devices = await ctx.Devices.AsNoTracking().Include(d => d.Ports).Include(d => d.FaceplateTemplate)
            .Where(d => (model != null && d.Model != null && d.Model.ToLower() == model) || extra.Contains(d.Id))
            .OrderBy(d => d.Name).AsSplitQuery().ToListAsync();
        return devices.Select(d => Plan(t, d)).ToList();
    }

    private static TemplateSyncPlan Plan(DeviceTemplate t, Device d)
    {
        var byKey = d.Ports.ToDictionary(p => FaceplateLayout.Key(p.Face, p.Name));
        var byName = d.Ports.GroupBy(p => p.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        var templateKeys = t.Ports.Select(p => FaceplateLayout.Key(p.Face, p.Name)).ToHashSet();
        var add = new List<string>();
        var change = new List<string>();
        var claimed = new HashSet<int>();

        foreach (var tp in t.Ports.OrderBy(p => p.Position))
        {
            var label = Display.PortOnly(new Port { Name = tp.Name, Face = tp.Face });
            var existing = MatchPort(tp, byKey, byName, templateKeys, claimed);
            if (existing is null) { add.Add(label); continue; }
            claimed.Add(existing.Id);
            if (existing.Face != tp.Face) change.Add($"{Display.PortOnly(existing)} → {(tp.Face == PortFace.Standard ? "standard" : Display.Face(tp.Face))} side");
            if (existing.MediaType != tp.MediaType) change.Add($"{label} → {Display.Media(tp.MediaType)}");
        }
        var extras = d.Ports.Where(p => !claimed.Contains(p.Id)).ToList();

        var details = new List<string>();
        if (d.FaceplateTemplateId != t.FaceplateTemplateId)
            details.Add(t.FaceplateTemplate is { } l ? $"layout → {l.Name}" : "layout → automatic");
        if (d.HeightU != t.HeightU) details.Add($"height {d.HeightU}U → {t.HeightU}U");
        if (d.Kind != t.Kind) details.Add($"type → {Display.Kind(t.Kind)}");
        if (!string.Equals(d.Manufacturer, t.Manufacturer, StringComparison.Ordinal) && t.Manufacturer is not null) details.Add($"manufacturer → {t.Manufacturer}");
        if (!string.Equals(d.Model, t.Model, StringComparison.Ordinal) && t.Model is not null) details.Add($"model → {t.Model}");

        var sameModel = t.Model is not null && string.Equals(d.Model?.Trim(), t.Model.Trim(), StringComparison.OrdinalIgnoreCase);
        return new TemplateSyncPlan(d.Id, d.Name, d.Model, sameModel, add, change,
            extras.Where(p => p.CableId is null).Select(Display.PortOnly).ToList(),
            extras.Where(p => p.CableId is not null).Select(Display.PortOnly).ToList(), details);
    }

    /// <summary>
    /// The device port that a template port corresponds to: same name and side, or else the same name on
    /// another side when that port isn't itself in the template (it'll be moved to the template's side).
    /// </summary>
    private static Port? MatchPort(TemplatePort tp, Dictionary<string, Port> byKey, Dictionary<string, List<Port>> byName,
        HashSet<string> templateKeys, HashSet<int> claimed)
    {
        if (byKey.TryGetValue(FaceplateLayout.Key(tp.Face, tp.Name), out var exact) && !claimed.Contains(exact.Id)) return exact;
        return byName.TryGetValue(tp.Name.Trim().ToLowerInvariant(), out var same)
            ? same.FirstOrDefault(p => !claimed.Contains(p.Id) && !templateKeys.Contains(FaceplateLayout.Key(p.Face, p.Name)))
            : null;
    }

    /// <summary>
    /// Updates devices to match a template. Ports are matched by name, so existing ports keep their cables,
    /// descriptions and places in port maps; missing ports are added, connectors, sides and front/rear pairs
    /// corrected, and the device gets the template's layout and details. Ports the template doesn't have are
    /// kept, unless <paramref name="removeExtras"/> is set, and even then only ones without a cable.
    /// </summary>
    public async Task<int> SyncDevicesAsync(int templateId, IReadOnlyCollection<int> deviceIds, bool removeExtras)
    {
        if (deviceIds.Count == 0) return 0;
        await using var ctx = await WriteAsync();
        var t = await ctx.DeviceTemplates.AsNoTracking().Include(x => x.FaceplateTemplate).FirstOrDefaultAsync(x => x.Id == templateId)
            ?? throw new NotFoundException("Template");
        var devices = await ctx.Devices.Include(d => d.Ports).Include(d => d.FaceplateTemplate)
            .Where(d => deviceIds.Contains(d.Id)).AsSplitQuery().ToListAsync();
        if (devices.Count != deviceIds.Distinct().Count()) throw new NotFoundException("Device");

        await using var tx = await ctx.Database.BeginTransactionAsync();
        foreach (var d in devices)
        {
            var plan = Plan(t, d);
            var byKey = d.Ports.ToDictionary(p => FaceplateLayout.Key(p.Face, p.Name));
            var byName = d.Ports.GroupBy(p => p.Name.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());
            var templateKeys = t.Ports.Select(p => FaceplateLayout.Key(p.Face, p.Name)).ToHashSet();
            var claimed = new HashSet<int>();
            var matched = new Dictionary<string, Port>();   // template key → device port

            foreach (var tp in t.Ports.OrderBy(p => p.Position))
            {
                var port = MatchPort(tp, byKey, byName, templateKeys, claimed);
                if (port is null)
                {
                    port = new Port { DeviceId = d.Id, Name = tp.Name, Face = tp.Face, MediaType = tp.MediaType, Description = tp.Description };
                    d.Ports.Add(port);
                }
                else
                {
                    claimed.Add(port.Id);
                    port.Face = tp.Face;
                    port.MediaType = tp.MediaType;
                    port.Description ??= tp.Description;   // never overwrite the device's own notes
                }
                port.Position = tp.Position;
                matched[FaceplateLayout.Key(tp.Face, tp.Name)] = port;
            }

            // Ports the template doesn't have: keep them after the template's ports, or drop the uncabled ones.
            var extras = d.Ports.Where(p => p.Id != 0 && !claimed.Contains(p.Id)).OrderBy(p => p.Position).ToList();
            var next = t.Ports.Count == 0 ? 0 : t.Ports.Max(p => p.Position);
            foreach (var p in extras)
            {
                if (removeExtras && p.CableId is null) ctx.Ports.Remove(p);
                else p.Position = ++next;
            }

            d.FaceplateTemplateId = t.FaceplateTemplateId;
            d.HeightU = t.HeightU;
            d.Kind = t.Kind;
            if (t.Manufacturer is not null) d.Manufacturer = t.Manufacturer;
            if (t.Model is not null) d.Model = t.Model;
            d.FaceplateRows = t.FaceplateRows;
            d.FaceplateNumbering = t.FaceplateNumbering;
            await SaveAsync(ctx);

            // Front/rear pairs as the template says, now that new ports have ids.
            foreach (var tp in t.Ports)
            {
                var port = matched[FaceplateLayout.Key(tp.Face, tp.Name)];
                port.PairedPortId = tp.PairName is not null && tp.PairFace is not null
                    && matched.TryGetValue(FaceplateLayout.Key(tp.PairFace.Value, tp.PairName), out var partner) ? partner.Id : null;
            }
            // Leave no half-pairs behind (e.g. a kept extra port that pointed at a re-paired port).
            var live = d.Ports.Where(p => ctx.Entry(p).State != EntityState.Deleted).ToDictionary(p => p.Id);
            foreach (var p in live.Values.Where(p => p.PairedPortId is int pid && (!live.TryGetValue(pid, out var other) || other.PairedPortId != p.Id)))
                p.PairedPortId = null;

            var removed = removeExtras ? plan.ExtraPorts.Count : 0;
            Audit(ctx, "Updated", nameof(Device), d.Id, $"Device '{d.Name}' updated from template '{t.Name}': " +
                string.Join("; ", new[]
                {
                    plan.AddPorts.Count > 0 ? $"added {string.Join(", ", plan.AddPorts)}" : null,
                    plan.ChangePorts.Count > 0 ? string.Join(", ", plan.ChangePorts) : null,
                    removed > 0 ? $"removed {string.Join(", ", plan.ExtraPorts)}" : null,
                    plan.DetailChanges.Count > 0 ? string.Join(", ", plan.DetailChanges) : null
                }.Where(s => s is not null).DefaultIfEmpty("already matched")));
            await SaveAsync(ctx);
        }
        await tx.CommitAsync();
        return devices.Count;
    }

    public async Task DeleteAsync(int id)
    {
        await using var ctx = await WriteAsync();
        var t = await ctx.DeviceTemplates.FindAsync(id) ?? throw new NotFoundException("Template");
        ctx.DeviceTemplates.Remove(t);
        Audit(ctx, "Deleted", "Template", t.Id, $"Device template '{t.Name}' (devices made from it are not affected)");
        await SaveAsync(ctx);
    }

    // ---------------- Export / import ----------------

    public async Task<byte[]> ExportJsonAsync(IReadOnlyCollection<int>? ids, string? exportedBy)
    {
        await using var ctx = await ReadAsync();
        var q = ctx.DeviceTemplates.AsNoTracking().Include(t => t.FaceplateTemplate).AsQueryable();
        if (ids is { Count: > 0 }) q = q.Where(t => ids.Contains(t.Id));
        var templates = await q.OrderBy(t => t.Name).ToListAsync();
        var file = new TemplateFile
        {
            ExportedUtc = DateTime.UtcNow,
            ExportedBy = exportedBy,
            Templates = templates.Select(ToDto).ToList()
        };
        return JsonSerializer.SerializeToUtf8Bytes(file, TransferService.Json);
    }

    public static TemplateDto ToDto(DeviceTemplate t) => new(t.Name, t.Kind, t.Manufacturer, t.Model, t.HeightU, t.FaceplateRows,
        t.FaceplateNumbering, t.Notes,
        t.Ports.OrderBy(p => p.Position).Select(p => new TemplatePortDto(p.Name, p.Face, p.MediaType, p.Position, p.Description, p.PairName, p.PairFace)).ToList(),
        t.FaceplateTemplate is { } l ? new TemplateLayoutDto(l.Name, l.Model, l.Columns, l.Rows, l.Placements, l.Labels) : null);

    public static string FileName(IReadOnlyList<DeviceTemplateInfo> templates) =>
        templates.Count == 1 ? $"{Safe(templates[0].Name)}.linktrace-template.json" : $"linktrace-templates-{DateTime.UtcNow:yyyyMMdd}.json";

    private static string Safe(string s) => FileNames.Safe(s).Replace(' ', '-').Trim('-');

    /// <summary>Reads and checks a template file without changing anything.</summary>
    public async Task<TemplateImportPreview> PreviewAsync(Stream json)
    {
        TemplateFile? file;
        try { file = await JsonSerializer.DeserializeAsync<TemplateFile>(json, TransferService.Json); }
        catch (JsonException ex) { throw new LinktraceException($"That file isn't a valid Linktrace template ({ex.Message})."); }
        if (file is null || file.Format != TemplateFile.FormatName)
            throw new LinktraceException("That file isn't a Linktrace device template. (Whole-install exports go in Export / import.)");
        if (file.Version > TemplateFile.CurrentVersion)
            throw new LinktraceException("That template came from a newer version of Linktrace. Update this install first.");

        var problems = file.Templates.SelectMany(Validate).ToList();
        await using var ctx = await ReadAsync();
        var existing = (await ctx.DeviceTemplates.Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renames = file.Templates.Where(t => existing.Contains(t.Name)).Select(t => t.Name).ToList();
        return new TemplateImportPreview(file, problems, renames);
    }

    /// <summary>Adds the file's templates (and their layouts). Names already in use get "(imported)" added. All or nothing.</summary>
    public async Task<List<string>> ImportAsync(TemplateFile file)
    {
        var problems = file.Templates.SelectMany(Validate).ToList();
        if (problems.Count > 0) throw new LinktraceException($"The file has problems: {problems[0]}");

        await using var ctx = await WriteAsync();
        await using var tx = await ctx.Database.BeginTransactionAsync();
        var templateNames = (await ctx.DeviceTemplates.Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var layoutNames = (await ctx.FaceplateTemplates.Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();

        foreach (var dto in file.Templates)
        {
            FaceplateTemplate? layout = null;
            if (dto.Layout is { } l)
            {
                layout = new FaceplateTemplate
                {
                    Name = Unique(l.Name, layoutNames), Model = l.Model, Columns = l.Columns, Rows = l.Rows,
                    Placements = l.Placements ?? [], Labels = l.Labels ?? []
                };
                ctx.FaceplateTemplates.Add(layout);
            }
            var t = new DeviceTemplate
            {
                Name = Unique(dto.Name, templateNames), Kind = dto.Kind, Manufacturer = dto.Manufacturer, Model = dto.Model,
                HeightU = Math.Clamp(dto.HeightU, 1, 60), FaceplateRows = Math.Clamp(dto.FaceplateRows, 0, 8),
                FaceplateNumbering = dto.FaceplateNumbering, Notes = dto.Notes, FaceplateTemplate = layout,
                Ports = dto.Ports.Select(p => new TemplatePort
                {
                    Name = p.Name.Trim(), Face = p.Face, MediaType = p.MediaType, Position = p.Position,
                    Description = p.Description, PairName = p.PairName, PairFace = p.PairFace
                }).ToList()
            };
            ctx.DeviceTemplates.Add(t);
            added.Add(t.Name);
        }
        await SaveAsync(ctx);
        Audit(ctx, "Imported", "Template", null, $"{added.Count} device template(s): {string.Join(", ", added)}");
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return added;
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        name = name.Trim();
        var result = name;
        for (var i = 1; taken.Contains(result); i++) result = i == 1 ? $"{name} (imported)" : $"{name} (imported {i})";
        taken.Add(result);
        return result;
    }

    /// <summary>Rules a template must follow: a name, unique port names per side, pairs that exist, a valid layout.</summary>
    public static List<string> Validate(TemplateDto t)
    {
        var problems = new List<string>();
        var label = string.IsNullOrWhiteSpace(t.Name) ? "A template" : $"'{t.Name}'";
        if (string.IsNullOrWhiteSpace(t.Name)) problems.Add("A template has no name.");
        if (t.HeightU is < 1 or > 60) problems.Add($"{label} has an impossible height ({t.HeightU}U).");

        var keys = new HashSet<string>();
        foreach (var p in t.Ports)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) problems.Add($"{label} has a port without a name.");
            else if (!keys.Add(FaceplateLayout.Key(p.Face, p.Name))) problems.Add($"{label} has two {p.Face} ports named '{p.Name}'.");
        }
        foreach (var p in t.Ports.Where(p => p.PairName is not null))
            if (p.PairFace is null || !keys.Contains(FaceplateLayout.Key(p.PairFace.Value, p.PairName!)))
                problems.Add($"{label}: port '{p.Name}' is paired with a port that isn't in the template.");

        if (t.Layout is { } l)
        {
            try
            {
                FaceplateService.Validate(new FaceplateTemplate
                {
                    Name = string.IsNullOrWhiteSpace(l.Name) ? "layout" : l.Name, Columns = l.Columns, Rows = l.Rows,
                    Placements = l.Placements ?? [], Labels = l.Labels ?? []
                });
            }
            catch (LinktraceException ex) { problems.Add($"{label}'s layout: {ex.Message}"); }
        }
        return problems.Take(20).ToList();
    }
}
