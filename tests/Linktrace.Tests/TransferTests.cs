using System.Text;
using System.Text.Json;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class TransferTests
{
    /// <summary>A small network touching every kind of record.</summary>
    private static async Task BuildNetworkAsync(TestDb db)
    {
        var site = await db.Locations.SaveSiteAsync(new Site { Name = "HQ", Address = "1 Main St" });
        var room = await db.Locations.SaveRoomAsync(new Room { SiteId = site.Id, Name = "IDF-1", Floor = "1" });
        var rack = await db.Locations.SaveRackAsync(new Rack { RoomId = room.Id, Name = "R1", HeightU = 24 });

        var sw = await db.Devices.SaveAsync(new Device { Name = "SW01", Kind = DeviceKind.Switch, Model = "C9300", RoomId = room.Id, RackId = rack.Id, RackUnit = 20 });
        await db.Devices.AddPortsAsync(sw.Id, new PortGenerationRequest { Prefix = "Gi1/0/", Count = 4 });
        var pp = await db.Devices.SaveAsync(new Device { Name = "PP-A", Kind = DeviceKind.PatchPanel, RoomId = room.Id, RackId = rack.Id, RackUnit = 22 });
        await db.Devices.AddPortsAsync(pp.Id, new PortGenerationRequest { Count = 2, FrontAndRearPairs = true });
        var jack = await db.Devices.SaveAsync(new Device { Name = "J-101", Kind = DeviceKind.WallJack, RoomId = room.Id, Notes = "by the door" });
        await db.Devices.AddPortsAsync(jack.Id, new PortGenerationRequest { Count = 1, FrontAndRearPairs = true });

        await db.Cables.ConnectAsync((await db.PortAsync(sw, "Gi1/0/1")).Id, (await db.PortAsync(pp, "1", PortFace.Front)).Id,
            new Cable { Label = "C-1", Color = "Blue", LengthMeters = 0.5m, CableType = CableType.Cat6a });
        await db.Cables.ConnectAsync((await db.PortAsync(pp, "1", PortFace.Rear)).Id, (await db.PortAsync(jack, "1", PortFace.Rear)).Id,
            new Cable { Label = "C-2", LengthMeters = 40 });

        await db.Faceplates.SaveAsync(new FaceplateTemplate
        {
            Name = "Cisco C9300", Model = "C9300", Columns = 4, Rows = 2,
            Placements = [new() { PortName = "Gi1/0/1", Col = 0, Row = 0 }, new() { PortName = "Gi1/0/2", Col = 0, Row = 1 }],
            Labels = [new() { Text = "LAN", Col = 2, Row = 0, Span = 2 }]
        }, sw.Id, asNew: true, applyToSameModel: false);

        await new PortMapViewService(db, db.User).SaveAsync(new PortMapView { Name = "Closet", DeviceIds = [pp.Id, sw.Id], ShowExternal = false });
    }

    /// <summary>A description of the data that ignores ids, so two instances can be compared.</summary>
    private static async Task<string> FingerprintAsync(TestDb db)
    {
        var f = await new TransferService(db, db.User).BuildExportAsync(null);
        var device = f.Devices.ToDictionary(d => d.Id, d => d.Name);
        var port = f.Ports.ToDictionary(p => p.Id, p => $"{device[p.DeviceId]}/{p.Name}/{p.Face}");
        var lines = new List<string>();
        lines.AddRange(f.Sites.Select(s => $"site {s.Name} {s.Address}"));
        lines.AddRange(f.Rooms.Select(r => $"room {r.Name} {r.Floor}"));
        lines.AddRange(f.Racks.Select(r => $"rack {r.Name} {r.HeightU}"));
        lines.AddRange(f.Devices.Select(d => $"device {d.Name} {d.Kind} {d.Model} U{d.RackUnit} {d.Notes} layout={f.FaceplateTemplates.FirstOrDefault(t => t.Id == d.FaceplateTemplateId)?.Name}"));
        lines.AddRange(f.Ports.Select(p => $"port {port[p.Id]} {p.MediaType} pair={(p.PairedPortId is int x ? port[x] : "")}"));
        lines.AddRange(f.Cables.Select(c => $"cable {c.Label} {c.CableType} {c.Color} {c.LengthMeters} {string.Join("<>", new[] { port[c.PortAId], port[c.PortBId] }.Order())}"));
        lines.AddRange(f.FaceplateTemplates.Select(t => $"layout {t.Name} {t.Columns}x{t.Rows} {string.Join(",", t.Placements.Select(p => $"{p.PortName}@{p.Col},{p.Row}"))} {string.Join(",", t.Labels.Select(l => l.Text))}"));
        lines.AddRange(f.PortMaps.Select(m => $"map {m.Name} {string.Join(",", m.DeviceIds.Select(id => device[id]))} ext={m.ShowExternal}"));
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    private static async Task<ImportPreview> RoundTripFileAsync(TestDb from, TestDb to)
    {
        var json = await new TransferService(from, from.User).ExportJsonAsync("tester");
        return await new TransferService(to, to.User).PreviewAsync(new MemoryStream(json));
    }

    [Fact]
    public async Task Export_then_import_into_an_empty_instance_reproduces_everything()
    {
        await using var source = new TestDb();
        await using var target = new TestDb();
        await BuildNetworkAsync(source);

        var preview = await RoundTripFileAsync(source, target);
        Assert.True(preview.CanImport, string.Join("; ", preview.Problems));
        Assert.Equal(10, preview.Counts["Ports"]); // 4 switch + 2×2 panel + 2 jack
        Assert.Equal(2, preview.Counts["Cables"]);

        await new TransferService(target, target.User).ImportAsync(preview.File, ImportMode.Replace);

        Assert.Equal(await FingerprintAsync(source), await FingerprintAsync(target));

        // The imported copy traces end to end just like the original.
        var sw = (await target.Devices.SearchAsync(new DeviceFilter { Text = "SW01" })).Single();
        var start = (await target.Devices.GetPortsAsync(sw.Id)).Single(p => p.Name == "Gi1/0/1");
        var trace = await target.Tracer.TraceAsync(start.Id);
        Assert.Equal(5, trace!.Hops.Count); // switch → panel front → panel rear → jack rear → jack front
    }

    [Fact]
    public async Task Replace_clears_what_was_there()
    {
        await using var source = new TestDb();
        await using var target = new TestDb();
        await BuildNetworkAsync(source);
        await BuildNetworkAsync(target);
        var room = (await target.Locations.GetRoomsAsync()).First();
        await target.Devices.SaveAsync(new Device { Name = "Only-in-target", RoomId = room.Id });

        var preview = await RoundTripFileAsync(source, target);
        await new TransferService(target, target.User).ImportAsync(preview.File, ImportMode.Replace);

        Assert.Equal(await FingerprintAsync(source), await FingerprintAsync(target));
    }

    [Fact]
    public async Task Merge_keeps_existing_data_and_renames_clashes()
    {
        await using var source = new TestDb();
        await using var target = new TestDb();
        await BuildNetworkAsync(source);
        await BuildNetworkAsync(target);

        var preview = await RoundTripFileAsync(source, target);
        Assert.Contains("Site 'HQ'", preview.MergeRenames);

        var result = await new TransferService(target, target.User).ImportAsync(preview.File, ImportMode.Merge);

        var after = await new TransferService(target, target.User).BuildExportAsync(null);
        Assert.Equal(["HQ", "HQ (imported)"], after.Sites.Select(s => s.Name).Order());
        Assert.Equal(6, after.Devices.Count);
        Assert.Contains(after.Cables, c => c.Label == "C-1 (imported)");
        Assert.Contains(after.PortMaps, m => m.Name == "Closet (imported)");
        Assert.Contains(result.Renamed, r => r.StartsWith("Faceplate layout"));
    }

    [Fact]
    public async Task Broken_files_are_rejected_before_anything_changes()
    {
        await using var db = new TestDb();
        var svc = new TransferService(db, db.User);

        await Assert.ThrowsAsync<LinktraceException>(() => svc.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes("not json"))));
        await Assert.ThrowsAsync<LinktraceException>(() => svc.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes("""{"format":"something-else"}"""))));

        var bad = new ExportFile
        {
            Sites = [new(1, "HQ", null, null)],
            Rooms = [new(1, 1, "IDF", null, null)],
            Devices = [new(1, "SW", DeviceKind.Switch, null, null, null, null, RoomId: 99, null, null, 1, 0, PortNumbering.Auto, null, null)],
            Ports = [new(1, 1, "1", 1, PortFace.Standard, MediaType.RJ45, null, null), new(2, 1, "2", 2, PortFace.Standard, MediaType.RJ45, null, null)],
            Cables = [new(1, "A", CableType.Cat6, null, null, null, 1, 2), new(2, "B", CableType.Cat6, null, null, null, 1, 2)]
        };
        var preview = await svc.PreviewAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(bad, TransferService.Json)));

        Assert.False(preview.CanImport);
        Assert.Contains(preview.Problems, p => p.Contains("missing room"));
        Assert.Contains(preview.Problems, p => p.Contains("more than one cable"));
        await Assert.ThrowsAsync<LinktraceException>(() => svc.ImportAsync(bad, ImportMode.Merge));
    }

    [Fact]
    public async Task Only_admins_can_import()
    {
        await using var db = new TestDb();
        db.User.IsAdmin = false;
        await Assert.ThrowsAsync<LinktraceException>(() => new TransferService(db, db.User).ImportAsync(new ExportFile(), ImportMode.Merge));
    }
}
