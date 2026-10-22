using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public record FaceplateTemplateInfo(int Id, string Name, string? Model, int Columns, int Rows, int DeviceCount);

/// <summary>Hand-built faceplate layouts (grid templates) and which devices use them.</summary>
public class FaceplateService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user) : ServiceBase(factory, user)
{
    public async Task<List<FaceplateTemplateInfo>> GetTemplatesAsync()
    {
        await using var ctx = await ReadAsync();
        return await ctx.FaceplateTemplates.AsNoTracking().OrderBy(t => t.Name)
            .Select(t => new FaceplateTemplateInfo(t.Id, t.Name, t.Model, t.Columns, t.Rows, t.Devices.Count))
            .ToListAsync();
    }

    /// <summary>Other devices with the same model (case-insensitive), for "use this layout for all of them".</summary>
    public async Task<List<int>> SameModelDeviceIdsAsync(string? model, int exceptDeviceId)
    {
        if (string.IsNullOrWhiteSpace(model)) return [];
        var m = model.Trim().ToLower();
        await using var ctx = await ReadAsync();
        return await ctx.Devices.Where(d => d.Id != exceptDeviceId && d.Model != null && d.Model.ToLower() == m)
            .Select(d => d.Id).ToListAsync();
    }

    public async Task<int> UsageCountAsync(int templateId)
    {
        await using var ctx = await ReadAsync();
        return await ctx.Devices.CountAsync(d => d.FaceplateTemplateId == templateId);
    }

    /// <summary>
    /// Saves a grid layout and assigns it to the device.
    /// </summary>
    /// <param name="asNew">Save as a new template instead of updating the one being edited.</param>
    /// <param name="applyToSameModel">Also use it for every other device with the template's model.</param>
    public async Task<FaceplateTemplate> SaveAsync(FaceplateTemplate model, int deviceId, bool asNew, bool applyToSameModel)
    {
        Validate(model);

        await using var ctx = await WriteAsync();
        var device = await ctx.Devices.FindAsync(deviceId) ?? throw new NotFoundException("Device");

        FaceplateTemplate t;
        var isNew = asNew || model.Id == 0;
        if (isNew)
        {
            t = new FaceplateTemplate();
            ctx.FaceplateTemplates.Add(t);
        }
        else
        {
            t = await ctx.FaceplateTemplates.FirstOrDefaultAsync(x => x.Id == model.Id) ?? throw new NotFoundException("Faceplate layout");
            ExpectVersion(ctx, t, model.Version);
        }

        t.Name = model.Name.Trim();
        t.Model = Clean(model.Model);
        t.Columns = model.Columns;
        t.Rows = model.Rows;
        t.Placements = model.Placements.Select(p => new FaceplatePlacement { PortName = p.PortName.Trim(), Face = p.Face, Col = p.Col, Row = p.Row, Span = p.Width > 1 ? p.Width : null }).ToList();
        t.Labels = model.Labels.Select(l => new FaceplateLabel { Text = l.Text.Trim(), Col = l.Col, Row = l.Row, Span = l.Span }).ToList();
        // Owned JSON collections were replaced wholesale; make sure the row is saved with a new version.
        if (!isNew) ctx.Entry(t).State = EntityState.Modified;

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await SaveAsync(ctx);

        device.FaceplateTemplate = t;
        var applied = 0;
        if (applyToSameModel && t.Model is { } m)
        {
            var lower = m.ToLower();
            var others = await ctx.Devices.Where(d => d.Id != deviceId && d.Model != null && d.Model.ToLower() == lower).ToListAsync();
            foreach (var d in others) d.FaceplateTemplate = t;
            applied = others.Count;
        }

        Audit(ctx, isNew ? "Created" : "Updated", "Faceplate", t.Id,
            $"Faceplate layout '{t.Name}' ({t.Columns}×{t.Rows}, {t.Placements.Count} ports) for '{device.Name}'"
            + (applied > 0 ? $" and {applied} other {t.Model} device(s)" : ""));
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return t;
    }

    /// <summary>Use an existing template for a device, or null to go back to the automatic layout.</summary>
    public async Task AssignAsync(int deviceId, int? templateId)
    {
        await using var ctx = await WriteAsync();
        var device = await ctx.Devices.FindAsync(deviceId) ?? throw new NotFoundException("Device");
        string what = "automatic layout";
        if (templateId is int id)
        {
            var t = await ctx.FaceplateTemplates.FindAsync(id) ?? throw new NotFoundException("Faceplate layout");
            what = $"layout '{t.Name}'";
        }
        device.FaceplateTemplateId = templateId;
        Audit(ctx, "Updated", nameof(Device), device.Id, $"Device '{device.Name}' now uses the {what}");
        await SaveAsync(ctx);
    }

    public async Task DeleteTemplateAsync(int templateId)
    {
        await using var ctx = await WriteAsync();
        var t = await ctx.FaceplateTemplates.Include(x => x.Devices).FirstOrDefaultAsync(x => x.Id == templateId)
            ?? throw new NotFoundException("Faceplate layout");
        foreach (var d in t.Devices) d.FaceplateTemplateId = null;
        ctx.FaceplateTemplates.Remove(t);
        Audit(ctx, "Deleted", "Faceplate", t.Id, $"Faceplate layout '{t.Name}' ({t.Devices.Count} device(s) back to automatic)");
        await SaveAsync(ctx);
    }

    /// <summary>Grid rules: everything inside the grid, one thing per cell, each port placed once.</summary>
    public static void Validate(FaceplateTemplate t)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) throw new LinktraceException("Give the layout a name.");
        if (t.Columns is < 1 or > FaceplateGrid.MaxColumns || t.Rows is < 1 or > FaceplateGrid.MaxRows)
            throw new LinktraceException($"The grid can be up to {FaceplateGrid.MaxColumns} columns by {FaceplateGrid.MaxRows} rows.");

        var used = new HashSet<(int, int)>();
        void Take(int col, int row, string what)
        {
            if (col < 0 || row < 0 || col >= t.Columns || row >= t.Rows)
                throw new LinktraceException($"{what} is outside the grid.");
            if (!used.Add((col, row)))
                throw new LinktraceException($"Two items share the cell at column {col + 1}, row {row + 1}.");
        }

        var ports = new HashSet<string>();
        foreach (var p in t.Placements)
        {
            for (var c = p.Col; c < p.Col + p.Width; c++) Take(c, p.Row, $"Port {p.PortName}");
            if (!ports.Add(FaceplateLayout.Key(p.Face, p.PortName)))
                throw new LinktraceException($"Port {p.PortName} is placed twice.");
        }
        foreach (var l in t.Labels)
        {
            if (string.IsNullOrWhiteSpace(l.Text)) throw new LinktraceException("Labels need some text.");
            if (l.Span < 1) throw new LinktraceException("A label must span at least one cell.");
            for (var c = l.Col; c < l.Col + l.Span; c++) Take(c, l.Row, $"Label '{l.Text}'");
        }
    }
}
