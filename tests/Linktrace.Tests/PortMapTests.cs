using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class PortMapTests
{
    private static List<Port> Ports(string prefix, int count, PortFace face = PortFace.Standard, int startPos = 0, int idBase = 0)
        => Enumerable.Range(1, count).Select(n => new Port { Id = idBase + n, Name = $"{prefix}{n}", Face = face, Position = startPos + n }).ToList();

    [Fact]
    public void A_48_port_switch_is_two_rows_of_24_with_odd_ports_on_top()
    {
        var sw = new Device { Kind = DeviceKind.Switch };
        var plate = FaceplateLayout.Build(sw, Ports("Gi1/0/", 48));

        var rows = plate.Cells.Select(c => c.Y).Distinct().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(24, plate.Cells.Count(c => c.Y == rows.Min()));

        var p1 = plate.Cells.Single(c => c.Port.Name == "Gi1/0/1");
        var p2 = plate.Cells.Single(c => c.Port.Name == "Gi1/0/2");
        var p3 = plate.Cells.Single(c => c.Port.Name == "Gi1/0/3");
        Assert.Equal(p1.X, p2.X);      // 2 sits under 1
        Assert.True(p2.Y > p1.Y);
        Assert.True(p3.X > p1.X);      // 3 starts the next column
        Assert.Equal(p1.Y, p3.Y);
    }

    [Fact]
    public void Uplinks_get_their_own_block_after_the_access_ports()
    {
        var sw = new Device { Kind = DeviceKind.Switch };
        var ports = Ports("Gi1/0/", 24).Concat(Ports("Te1/1/", 4, startPos: 100, idBase: 100)).ToList();

        var plate = FaceplateLayout.Build(sw, ports);

        Assert.Equal(["Gi1/0/", "Te1/1/"], plate.Blocks.Select(b => b.Title));
        var lastGi = plate.Cells.Where(c => c.Port.Name.StartsWith("Gi")).Max(c => c.X);
        Assert.True(plate.Cells.Where(c => c.Port.Name.StartsWith("Te")).All(c => c.X > lastGi));
    }

    [Fact]
    public void A_24_port_patch_panel_is_one_row_with_rear_ports_in_a_second_strip()
    {
        var pp = new Device { Kind = DeviceKind.PatchPanel };
        var ports = Ports("", 24, PortFace.Front).Concat(Ports("", 24, PortFace.Rear, 100, 100)).ToList();

        var plate = FaceplateLayout.Build(pp, ports);

        Assert.Single(plate.Cells.Where(c => c.Port.Face == PortFace.Front).Select(c => c.Y).Distinct());
        Assert.True(plate.Cells.Where(c => c.Port.Face == PortFace.Rear).Min(c => c.Y)
                    > plate.Cells.Where(c => c.Port.Face == PortFace.Front).Max(c => c.Y));
        Assert.Equal(["Front", "Rear"], plate.Blocks.Select(b => b.Title));
    }

    [Fact]
    public void Manual_rows_and_numbering_override_the_defaults()
    {
        var sw = new Device { Kind = DeviceKind.Switch, FaceplateRows = 4, FaceplateNumbering = PortNumbering.RowByRow };
        var plate = FaceplateLayout.Build(sw, Ports("", 48));

        Assert.Equal(4, plate.Cells.Select(c => c.Y).Distinct().Count());
        var p1 = plate.Cells.Single(c => c.Port.Name == "1");
        var p2 = plate.Cells.Single(c => c.Port.Name == "2");
        Assert.Equal(p1.Y, p2.Y); // row by row: 2 is beside 1
    }

    [Fact]
    public async Task Links_include_direct_cables_and_paths_through_patch_panels()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var core = await db.CreateDeviceAsync(room, "CORE", DeviceKind.Switch, new() { Prefix = "Te", Count = 2 });
        var access = await db.CreateDeviceAsync(room, "ACCESS", DeviceKind.Switch, new() { Prefix = "Te", Count = 2 });
        var fp1 = await db.CreateDeviceAsync(room, "FP-1", DeviceKind.PatchPanel, new() { Count = 2, FrontAndRearPairs = true });
        var fp2 = await db.CreateDeviceAsync(room, "FP-2", DeviceKind.PatchPanel, new() { Count = 2, FrontAndRearPairs = true });

        // Te1 ↔ Te1 directly; Te2 → FP-1 → trunk → FP-2 → Te2
        await db.Cables.ConnectAsync((await db.PortAsync(core, "Te1")).Id, (await db.PortAsync(access, "Te1")).Id, new Cable { Label = "D1" });
        await db.Cables.ConnectAsync((await db.PortAsync(core, "Te2")).Id, (await db.PortAsync(fp1, "1", PortFace.Front)).Id, new Cable());
        await db.Cables.ConnectAsync((await db.PortAsync(fp1, "1", PortFace.Rear)).Id, (await db.PortAsync(fp2, "1", PortFace.Rear)).Id, new Cable());
        await db.Cables.ConnectAsync((await db.PortAsync(fp2, "1", PortFace.Front)).Id, (await db.PortAsync(access, "Te2")).Id, new Cable());

        var svc = new PortMapService(db);
        var directOnly = await svc.GetMapAsync([core.Id, access.Id], throughPassThrough: false);
        var all = await svc.GetMapAsync([core.Id, access.Id], throughPassThrough: true);

        // Direct only: one cable between them; core Te2 and access Te2 lead to the panels (not shown).
        var d = Assert.Single(directOnly.Links);
        Assert.Equal("D1", d.Cable!.Label);
        Assert.Equal(["FP-1 › 1 (front)", "FP-2 › 1 (front)"], directOnly.Externals.Select(e => e.FarText).Order());

        // Following panels: the Te2 path becomes a second line, found once (not once from each end).
        Assert.Equal(2, all.Links.Count);
        Assert.Empty(all.Externals);
        var via = all.Links.Single(l => !l.IsDirect);
        var ends = new[] { via.PortAId, via.PortBId }.Order();
        Assert.Equal(new[] { (await db.PortAsync(core, "Te2")).Id, (await db.PortAsync(access, "Te2")).Id }.Order(), ends);
        Assert.Equal(3, via.CableCount);

        // With a panel in the view, it's drawn as direct cables instead.
        var withPanel = await svc.GetMapAsync([core.Id, fp1.Id, access.Id], throughPassThrough: true);
        Assert.Equal(2, withPanel.Links.Count(l => l.IsDirect && l.Cable!.Label != "D1") + withPanel.Links.Count(l => !l.IsDirect));

        var neighbors = await svc.GetNeighborsAsync(core.Id);
        Assert.Equal(["ACCESS", "FP-1"], neighbors.Select(n => n.Name).Order());
    }

    [Fact]
    public async Task Rack_devices_are_listed_top_of_rack_first()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var rack = await db.Locations.SaveRackAsync(new Rack { RoomId = room.Id, Name = "R1", HeightU = 42 });
        await db.Devices.SaveAsync(new Device { Name = "Bottom", RoomId = room.Id, RackId = rack.Id, RackUnit = 1 });
        await db.Devices.SaveAsync(new Device { Name = "Top", RoomId = room.Id, RackId = rack.Id, RackUnit = 40 });
        await db.Devices.SaveAsync(new Device { Name = "Unplaced", RoomId = room.Id, RackId = rack.Id });

        var ids = await new PortMapService(db).GetRackDeviceIdsAsync(rack.Id);
        var names = new List<string>();
        foreach (var id in ids) names.Add((await db.Devices.GetAsync(id))!.Name);

        Assert.Equal(["Top", "Bottom", "Unplaced"], names);
    }
}
