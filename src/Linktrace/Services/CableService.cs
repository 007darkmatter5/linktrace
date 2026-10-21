using System.Text.RegularExpressions;
using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public record CableEnd(int PortId, string PortName, PortFace Face, int DeviceId, string DeviceName)
{
    public string Text => Display.Port(DeviceName, PortName, Face);
}

public record CableListItem(int Id, string? Label, CableType CableType, string? Color, decimal? LengthMeters,
    string? Notes, CableEnd? EndA, CableEnd? EndB, DateTime UpdatedUtc, string? UpdatedBy);

public partial class CableService(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser user) : ServiceBase(factory, user)
{
    public const string LabelPrefix = "C-";

    public async Task<List<CableListItem>> SearchAsync(string? text, int max = 500)
    {
        await using var ctx = await ReadAsync();
        var q = ctx.Cables.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var t = $"%{text.Trim()}%";
            q = q.Where(c => EF.Functions.Like(c.Label ?? "", t)
                || EF.Functions.Like(c.Color ?? "", t)
                || EF.Functions.Like(c.Notes ?? "", t)
                || c.Ports.Any(p => EF.Functions.Like(p.Name, t) || EF.Functions.Like(p.Device.Name, t)));
        }

        var cables = await q.OrderBy(c => c.Label).Take(max)
            .Include(c => c.Ports).ThenInclude(p => p.Device)
            .AsSplitQuery()
            .ToListAsync();
        return cables.Select(ToListItem).ToList();
    }

    public async Task<Cable?> GetAsync(int id)
    {
        await using var ctx = await ReadAsync();
        return await ctx.Cables.AsNoTracking().Include(c => c.Ports).ThenInclude(p => p.Device)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Suggests the next free label in the C-00001 series.</summary>
    public async Task<string> SuggestLabelAsync()
    {
        await using var ctx = await ReadAsync();
        var labels = await ctx.Cables.Where(c => c.Label != null && c.Label.StartsWith(LabelPrefix))
            .Select(c => c.Label!).ToListAsync();
        var max = labels.Select(l => LabelNumber().Match(l)).Where(m => m.Success)
            .Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"{LabelPrefix}{max + 1:D5}";
    }

    /// <summary>Plug a new cable between two free ports.</summary>
    public async Task<Cable> ConnectAsync(int portAId, int portBId, Cable details)
    {
        if (portAId == portBId) throw new LinktraceException("A cable needs two different ports.");

        await using var ctx = await WriteAsync();
        var ports = await ctx.Ports.Include(p => p.Device).Where(p => p.Id == portAId || p.Id == portBId).ToListAsync();
        var a = ports.FirstOrDefault(p => p.Id == portAId) ?? throw new NotFoundException("Port");
        var b = ports.FirstOrDefault(p => p.Id == portBId) ?? throw new NotFoundException("Port");

        foreach (var p in new[] { a, b })
            if (p.CableId is not null)
                throw new LinktraceException($"{Display.Port(p)} is already connected. Disconnect it first.");

        var label = Clean(details.Label);
        await EnsureLabelFreeAsync(ctx, label, null);

        var cable = new Cable
        {
            Label = label,
            CableType = details.CableType,
            Color = Clean(details.Color),
            LengthMeters = details.LengthMeters,
            Notes = Clean(details.Notes)
        };
        ctx.Cables.Add(cable);
        // Changing the ports bumps their Version; if someone else connected one of them
        // a moment ago, the save fails with a concurrency conflict instead of double-booking.
        a.Cable = cable;
        b.Cable = cable;

        await using var tx = await ctx.Database.BeginTransactionAsync();
        await SaveAsync(ctx);
        cable.Ports = [a, b];
        Audit(ctx, "Connected", nameof(Cable), cable.Id, Describe(cable));
        await SaveAsync(ctx);
        await tx.CommitAsync();
        return cable;
    }

    public async Task<Cable> UpdateAsync(Cable model)
    {
        await using var ctx = await WriteAsync();
        var e = await ctx.Cables.Include(c => c.Ports).ThenInclude(p => p.Device).FirstOrDefaultAsync(c => c.Id == model.Id)
            ?? throw new NotFoundException("Cable");
        ExpectVersion(ctx, e, model.Version);

        var label = Clean(model.Label);
        await EnsureLabelFreeAsync(ctx, label, e.Id);

        e.Label = label;
        e.CableType = model.CableType;
        e.Color = Clean(model.Color);
        e.LengthMeters = model.LengthMeters;
        e.Notes = Clean(model.Notes);
        Audit(ctx, "Updated", nameof(Cable), e.Id, Describe(e));
        await SaveAsync(ctx);
        return e;
    }

    public async Task DisconnectAsync(int cableId)
    {
        await using var ctx = await WriteAsync();
        if (!await ctx.Cables.AnyAsync(c => c.Id == cableId)) throw new NotFoundException("Cable");
        await DeviceService.RemoveCablesAsync(ctx, [cableId]);
        await SaveAsync(ctx);
    }

    private static async Task EnsureLabelFreeAsync(ApplicationDbContext ctx, string? label, int? exceptId)
    {
        if (label is null) return;
        var lower = label.ToLower();
        var clash = await ctx.Cables.AnyAsync(c => c.Id != exceptId && c.Label != null && c.Label.ToLower() == lower);
        if (clash) throw new LinktraceException($"Cable label '{label}' is already used by another cable.");
    }

    /// <summary>"C-00012: SW01 › Gi1/0/1 ↔ PP-A › 1 (front)"</summary>
    public static string Describe(Cable c)
    {
        var ends = c.Ports.Select(Display.Port).ToList();
        var path = ends.Count == 2 ? $"{ends[0]} ↔ {ends[1]}" : string.Join(", ", ends);
        return string.IsNullOrEmpty(c.Label) ? path : $"{c.Label}: {path}";
    }

    public static CableListItem ToListItem(Cable c)
    {
        var ends = c.Ports.OrderBy(p => p.Id)
            .Select(p => new CableEnd(p.Id, p.Name, p.Face, p.DeviceId, p.Device?.Name ?? "?")).ToList();
        return new CableListItem(c.Id, c.Label, c.CableType, c.Color, c.LengthMeters, c.Notes,
            ends.ElementAtOrDefault(0), ends.ElementAtOrDefault(1), c.UpdatedUtc, c.UpdatedBy);
    }

    [GeneratedRegex(@"^C-(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelNumber();
}
