using Linktrace.Components.Shared;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class ServiceTests
{
    /// <summary>
    /// SW01 Gi1/0/1 ── cable ── PP-A 1 (front) ⇣ PP-A 1 (rear) ── cable ── J-101 1 (rear) ⇣ J-101 1 (front) ── cable ── PC eth0
    /// </summary>
    private static async Task<(Port Switch, Port PanelFront, Port PanelRear, Port JackRear, Port JackFront, Port Pc)> BuildCircuitAsync(TestDb db)
    {
        var room = await db.CreateRoomAsync();
        var sw = await db.CreateDeviceAsync(room, "SW01", DeviceKind.Switch, new() { Prefix = "Gi1/0/", Count = 4 });
        var pp = await db.CreateDeviceAsync(room, "PP-A", DeviceKind.PatchPanel, new() { Count = 4, FrontAndRearPairs = true });
        var jack = await db.CreateDeviceAsync(room, "J-101", DeviceKind.WallJack, new() { Count = 1, FrontAndRearPairs = true });
        var pc = await db.CreateDeviceAsync(room, "PC-23", DeviceKind.Workstation, new() { Prefix = "eth", Start = 0, Count = 1 });

        var swPort = await db.PortAsync(sw, "Gi1/0/1");
        var ppFront = await db.PortAsync(pp, "1", PortFace.Front);
        var ppRear = await db.PortAsync(pp, "1", PortFace.Rear);
        var jackRear = await db.PortAsync(jack, "1", PortFace.Rear);
        var jackFront = await db.PortAsync(jack, "1", PortFace.Front);
        var pcPort = await db.PortAsync(pc, "eth0");

        await db.Cables.ConnectAsync(swPort.Id, ppFront.Id, new Cable { Label = "C-00001", LengthMeters = 2 });
        await db.Cables.ConnectAsync(ppRear.Id, jackRear.Id, new Cable { Label = "C-00002", LengthMeters = 35 });
        await db.Cables.ConnectAsync(jackFront.Id, pcPort.Id, new Cable { Label = "C-00003", LengthMeters = 3 });
        return (swPort, ppFront, ppRear, jackRear, jackFront, pcPort);
    }

    [Fact]
    public async Task Trace_from_middle_follows_cables_and_patch_panel_pass_through_both_ways()
    {
        await using var db = new TestDb();
        var c = await BuildCircuitAsync(db);

        var trace = await db.Tracer.TraceAsync(c.PanelRear.Id);

        Assert.NotNull(trace);
        Assert.Equal(
            new[] { c.Switch.Id, c.PanelFront.Id, c.PanelRear.Id, c.JackRear.Id, c.JackFront.Id, c.Pc.Id },
            trace.Hops.Select(h => h.Port.Id));
        Assert.Equal(
            new LinkKind?[] { LinkKind.Cable, LinkKind.PassThrough, LinkKind.Cable, LinkKind.PassThrough, LinkKind.Cable, null },
            trace.Hops.Select(h => h.LinkToNext?.Kind));
        Assert.Equal(3, trace.CableCount);
        Assert.Equal(40m, trace.TotalLengthMeters);
        Assert.False(trace.LoopDetected);
    }

    [Fact]
    public async Task Trace_from_either_end_gives_the_same_path()
    {
        await using var db = new TestDb();
        var c = await BuildCircuitAsync(db);

        var fromSwitch = await db.Tracer.TraceAsync(c.Switch.Id);
        var fromPc = await db.Tracer.TraceAsync(c.Pc.Id);

        Assert.Equal(fromSwitch!.Hops.Select(h => h.Port.Id), fromPc!.Hops.Select(h => h.Port.Id).Reverse());
    }

    [Fact]
    public async Task Trace_of_unconnected_port_is_just_that_port()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var sw = await db.CreateDeviceAsync(room, "SW01", DeviceKind.Switch, new() { Prefix = "Gi1/0/", Count = 1 });

        var trace = await db.Tracer.TraceAsync((await db.PortAsync(sw, "Gi1/0/1")).Id);

        Assert.Single(trace!.Hops);
        Assert.Null(trace.First.LinkToNext);
    }

    [Fact]
    public async Task Trace_stops_on_a_loop()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var pp = await db.CreateDeviceAsync(room, "PP-A", DeviceKind.PatchPanel, new() { Count = 1, FrontAndRearPairs = true });
        // Plug the panel's own front into its own rear: a loop.
        var front = await db.PortAsync(pp, "1", PortFace.Front);
        var rear = await db.PortAsync(pp, "1", PortFace.Rear);
        await db.Cables.ConnectAsync(front.Id, rear.Id, new Cable());

        var trace = await db.Tracer.TraceAsync(front.Id);

        Assert.True(trace!.LoopDetected);
        Assert.Equal(2, trace.Hops.Count);
    }

    [Fact]
    public async Task A_port_can_only_hold_one_cable()
    {
        await using var db = new TestDb();
        var c = await BuildCircuitAsync(db);
        var other = await db.PortAsync((await db.Devices.GetAsync(c.Switch.DeviceId))!, "Gi1/0/2");

        var ex = await Assert.ThrowsAsync<LinktraceException>(() => db.Cables.ConnectAsync(other.Id, c.PanelFront.Id, new Cable()));
        Assert.Contains("already connected", ex.Message);
    }

    [Fact]
    public async Task Cable_labels_must_be_unique()
    {
        await using var db = new TestDb();
        await BuildCircuitAsync(db);
        var sw = (await db.Devices.SearchAsync(new DeviceFilter { Text = "SW01" })).Single();
        var p2 = (await db.Devices.GetPortsAsync(sw.Id)).Single(p => p.Name == "Gi1/0/2");
        var p3 = (await db.Devices.GetPortsAsync(sw.Id)).Single(p => p.Name == "Gi1/0/3");

        await Assert.ThrowsAsync<LinktraceException>(() => db.Cables.ConnectAsync(p2.Id, p3.Id, new Cable { Label = "c-00001" }));
        Assert.Equal("C-00004", await db.Cables.SuggestLabelAsync());
    }

    [Fact]
    public async Task Saving_a_stale_copy_is_rejected()
    {
        await using var db = new TestDb();
        var site = await db.Locations.SaveSiteAsync(new Site { Name = "HQ" });

        var userA = site.EditCopy();
        var userB = site.EditCopy();
        userA.Name = "HQ North";
        await db.Locations.SaveSiteAsync(userA);

        userB.Name = "HQ South";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.Locations.SaveSiteAsync(userB));
    }

    [Fact]
    public async Task Viewers_cannot_make_changes()
    {
        await using var db = new TestDb();
        db.User.CanEdit = false;

        await Assert.ThrowsAsync<LinktraceException>(() => db.Locations.SaveSiteAsync(new Site { Name = "HQ" }));
    }

    [Fact]
    public async Task Deleting_a_device_disconnects_its_cables_and_logs_it()
    {
        await using var db = new TestDb();
        var c = await BuildCircuitAsync(db);

        await db.Devices.DeleteAsync(c.PanelFront.DeviceId);

        var swPort = (await db.Devices.GetPortsAsync(c.Switch.DeviceId)).Single(p => p.Id == c.Switch.Id);
        Assert.Null(swPort.CableId);
        Assert.Single(await db.Cables.SearchAsync(null)); // only the jack ↔ PC cable is left

        await using var ctx = db.CreateDbContext();
        Assert.Equal(2, ctx.AuditEntries.Count(a => a.Action == "Disconnected"));
    }

    [Fact]
    public async Task Pairing_a_port_updates_both_sides_and_releases_old_partner()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var pp = await db.CreateDeviceAsync(room, "PP-A", DeviceKind.PatchPanel, new() { Count = 2, FrontAndRearPairs = true });
        var front1 = await db.PortAsync(pp, "1", PortFace.Front);
        var rear1 = await db.PortAsync(pp, "1", PortFace.Rear);
        var rear2 = await db.PortAsync(pp, "2", PortFace.Rear);
        Assert.Equal(rear1.Id, front1.PairedPortId);

        var edit = front1.EditCopy();
        edit.PairedPortId = rear2.Id;
        await db.Devices.SavePortAsync(edit);

        var ports = await db.Devices.GetPortsAsync(pp.Id);
        Assert.Equal(rear2.Id, ports.Single(p => p.Id == front1.Id).PairedPortId);
        Assert.Equal(front1.Id, ports.Single(p => p.Id == rear2.Id).PairedPortId);
        Assert.Null(ports.Single(p => p.Id == rear1.Id).PairedPortId);
        // Front 2 lost its rear partner too.
        Assert.Null(ports.Single(p => p.Name == "2" && p.Face == PortFace.Front).PairedPortId);
    }

    [Fact]
    public async Task Overlapping_rack_positions_are_rejected()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var rack = await db.Locations.SaveRackAsync(new Rack { RoomId = room.Id, Name = "R1", HeightU = 42 });
        await db.Devices.SaveAsync(new Device { Name = "Server", Kind = DeviceKind.Server, RoomId = room.Id, RackId = rack.Id, RackUnit = 10, HeightU = 2 });

        var ex = await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.SaveAsync(
            new Device { Name = "SW01", RoomId = room.Id, RackId = rack.Id, RackUnit = 11, HeightU = 1 }));
        Assert.Contains("overlaps", ex.Message);

        await db.Devices.SaveAsync(new Device { Name = "SW01", RoomId = room.Id, RackId = rack.Id, RackUnit = 12, HeightU = 1 });
    }

    [Fact]
    public async Task Duplicate_port_names_are_rejected()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var sw = await db.CreateDeviceAsync(room, "SW01", DeviceKind.Switch, new() { Prefix = "Gi1/0/", Count = 4 });

        await Assert.ThrowsAsync<LinktraceException>(() =>
            db.Devices.AddPortsAsync(sw.Id, new PortGenerationRequest { Prefix = "Gi1/0/", Start = 4, Count = 2 }));
    }
}
