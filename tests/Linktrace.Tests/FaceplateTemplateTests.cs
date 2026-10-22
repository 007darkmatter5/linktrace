using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class FaceplateTemplateTests
{
    private static FaceplateTemplate Grid(int cols, int rows, params (string Name, int Col, int Row)[] ports) => new()
    {
        Name = "Test", Columns = cols, Rows = rows,
        Placements = ports.Select(p => new FaceplatePlacement { PortName = p.Name, Col = p.Col, Row = p.Row }).ToList()
    };

    [Fact]
    public void Ports_are_drawn_exactly_in_their_grid_cells()
    {
        var device = new Device
        {
            Kind = DeviceKind.Firewall,
            Ports = [new Port { Id = 1, Name = "wan1" }, new Port { Id = 2, Name = "lan1" }, new Port { Id = 3, Name = "console" }],
            FaceplateTemplate = Grid(10, 2, ("wan1", 0, 0), ("lan1", 4, 1))
        };
        device.FaceplateTemplate.Labels.Add(new FaceplateLabel { Text = "WAN", Col = 0, Row = 1, Span = 2 });

        var plate = FaceplateLayout.Build(device, device.Ports);

        var wan = plate.Find(1)!;
        var lan = plate.Find(2)!;
        Assert.Equal(FaceplateLayout.Padding, wan.X);
        Assert.Equal(FaceplateLayout.Padding + 4 * FaceplateLayout.Pitch, lan.X);   // snapped to column 4
        Assert.Equal(FaceplateLayout.Padding + FaceplateLayout.Pitch, lan.Y);       // row 1
        Assert.Single(plate.Labels);

        // "console" isn't in the template: still drawn, below the grid, flagged as not placed.
        var console = plate.Find(3)!;
        Assert.True(console.Y > lan.Y + FaceplateLayout.Cell);
        Assert.Contains(plate.Blocks, b => b.Title.StartsWith("Not placed"));
    }

    [Fact]
    public void A_port_can_span_several_columns()
    {
        var device = new Device
        {
            Kind = DeviceKind.Other,
            Ports = [new Port { Id = 1, Name = "1" }, new Port { Id = 2, Name = "2" }, new Port { Id = 7, Name = "7", Face = PortFace.Rear }],
            FaceplateTemplate = new FaceplateTemplate
            {
                Name = "RPS", Columns = 6, Rows = 2,
                Placements =
                [
                    new() { PortName = "1", Col = 0, Row = 0, Span = 2 },
                    new() { PortName = "2", Col = 2, Row = 0 },              // older layouts: no span = 1 cell
                    new() { PortName = "7", Face = PortFace.Rear, Col = 0, Row = 1, Span = 3 }
                ]
            }
        };

        FaceplateService.Validate(device.FaceplateTemplate);
        var plate = FaceplateLayout.Build(device, device.Ports);

        var p1 = plate.Find(1)!;
        var p2 = plate.Find(2)!;
        var p7 = plate.Find(7)!;
        Assert.Equal(2 * FaceplateLayout.Pitch - FaceplateLayout.Gap, p1.Width);
        Assert.Equal(FaceplateLayout.Cell, p2.Width);
        Assert.Equal(3 * FaceplateLayout.Pitch - FaceplateLayout.Gap, p7.Width);
        Assert.Equal(p1.X + p1.Width / 2, p1.CenterX);      // cables attach in the middle
        Assert.True(p2.X >= p1.X + p1.Width);               // no overlap
    }

    [Fact]
    public async Task Port_spans_are_saved_with_the_layout()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var rps = await db.CreateDeviceAsync(room, "USP RPS", DeviceKind.Other, new() { Count = 2 });
        var layout = Grid(6, 1, ("1", 0, 0), ("2", 3, 0));
        layout.Name = "RPS";
        layout.Placements[0].Span = 3;
        await db.Faceplates.SaveAsync(layout, rps.Id, asNew: true, applyToSameModel: false);

        var loaded = (await db.Devices.GetAsync(rps.Id))!.FaceplateTemplate!;
        Assert.Equal(3, loaded.Placements.Single(p => p.PortName == "1").Width);
        Assert.Equal(1, loaded.Placements.Single(p => p.PortName == "2").Width);
    }

    [Fact]
    public void A_wide_port_cannot_overlap_its_neighbour_or_hang_off_the_grid()
    {
        var overlap = Grid(4, 1, ("1", 0, 0), ("2", 1, 0));
        overlap.Placements[0].Span = 2;
        Assert.Contains("share the cell", Assert.Throws<LinktraceException>(() => FaceplateService.Validate(overlap)).Message);

        var offEdge = Grid(4, 1, ("1", 3, 0));
        offEdge.Placements[0].Span = 2;
        Assert.Contains("outside the grid", Assert.Throws<LinktraceException>(() => FaceplateService.Validate(offEdge)).Message);
    }

    [Fact]
    public void Automatic_layout_converts_to_a_starting_grid()
    {
        var sw = new Device { Kind = DeviceKind.Switch };
        var ports = Enumerable.Range(1, 24).Select(n => new Port { Id = n, Name = $"Gi1/0/{n}", Position = n })
            .Concat(Enumerable.Range(1, 4).Select(n => new Port { Id = 100 + n, Name = $"Te1/1/{n}", Position = 100 + n })).ToList();

        var t = FaceplateLayout.AutoTemplate(sw, ports);

        t.Name = "Generated";
        FaceplateService.Validate(t);
        Assert.Equal(28, t.Placements.Count);
        Assert.Equal(3, t.Rows);                                   // label row + 2 port rows
        Assert.Equal(12 + 1 + 2, t.Columns);                       // 12 columns, gap, 2 uplink columns
        var p1 = t.Placements.Single(p => p.PortName == "Gi1/0/1");
        var p2 = t.Placements.Single(p => p.PortName == "Gi1/0/2");
        Assert.Equal((0, 1), (p1.Col, p1.Row));
        Assert.Equal((0, 2), (p2.Col, p2.Row));                    // odd over even
        Assert.Equal(["Gi1/0/", "Te1/1/"], t.Labels.Select(l => l.Text));
    }

    [Theory]
    [InlineData(10, 0, "outside the grid")]
    [InlineData(0, 0, "share the cell")]
    public void Grid_rules_are_enforced(int col, int row, string expected)
    {
        var t = Grid(4, 2, ("1", 0, 0), ("2", col, row));
        var ex = Assert.Throws<LinktraceException>(() => FaceplateService.Validate(t));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void A_label_cannot_overlap_a_port()
    {
        var t = Grid(4, 1, ("1", 2, 0));
        t.Labels.Add(new FaceplateLabel { Text = "WAN", Col = 0, Row = 0, Span = 3 });
        Assert.Throws<LinktraceException>(() => FaceplateService.Validate(t));
    }

    [Fact]
    public async Task Layouts_are_shared_by_model_and_picked_up_by_new_devices()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var fw1 = await db.Devices.SaveAsync(new Device { Name = "FW1", Kind = DeviceKind.Firewall, Model = "FG-100F", RoomId = room.Id });
        var fw2 = await db.Devices.SaveAsync(new Device { Name = "FW2", Kind = DeviceKind.Firewall, Model = "fg-100f", RoomId = room.Id });
        await db.Devices.AddPortsAsync(fw1.Id, new PortGenerationRequest { Prefix = "port", Count = 4 });

        var layout = Grid(8, 2, ("port1", 0, 0), ("port2", 0, 1));
        layout.Name = "FortiGate 100F";
        layout.Model = "FG-100F";
        var saved = await db.Faceplates.SaveAsync(layout, fw1.Id, asNew: false, applyToSameModel: true);

        Assert.Equal(saved.Id, (await db.Devices.GetAsync(fw1.Id))!.FaceplateTemplateId);
        Assert.Equal(saved.Id, (await db.Devices.GetAsync(fw2.Id))!.FaceplateTemplateId); // same model, different case

        var fw3 = await db.Devices.SaveAsync(new Device { Name = "FW3", Kind = DeviceKind.Firewall, Model = "FG-100F", RoomId = room.Id });
        var loaded = (await db.Devices.GetAsync(fw3.Id))!;
        Assert.Equal(saved.Id, loaded.FaceplateTemplateId);
        Assert.Equal(2, loaded.FaceplateTemplate!.Placements.Count); // JSON round-trip

        // Editing the shared layout updates it for everyone; a stale copy is rejected.
        var edit = loaded.FaceplateTemplate;
        edit.Placements[1].Col = 3;
        await db.Faceplates.SaveAsync(edit, fw3.Id, asNew: false, applyToSameModel: false);
        Assert.Equal(3, (await db.Devices.GetAsync(fw1.Id))!.FaceplateTemplate!.Placements.Single(p => p.PortName == "port2").Col);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.Faceplates.SaveAsync(edit, fw3.Id, asNew: false, applyToSameModel: false));

        // Back to automatic for one device only.
        await db.Faceplates.AssignAsync(fw2.Id, null);
        Assert.Null((await db.Devices.GetAsync(fw2.Id))!.FaceplateTemplateId);
        Assert.Equal(2, await db.Faceplates.UsageCountAsync(saved.Id));
    }
}
