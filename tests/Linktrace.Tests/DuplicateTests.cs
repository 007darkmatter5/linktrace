using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class DuplicateTests
{
    [Theory]
    [InlineData("SW01", 3, "SW01,SW02,SW03")]
    [InlineData("J-2.14-09", 3, "J-2.14-09,J-2.14-10,J-2.14-11")]
    [InlineData("PP-A", 3, "PP-A,PP-A-2,PP-A-3")]
    [InlineData("eth9", 2, "eth9,eth10")]
    public void Copy_names_count_up(string first, int count, string expected)
        => Assert.Equal(expected.Split(','), DuplicateRequest.NamesFor(first, count));

    [Theory]
    [InlineData("SW01", "SW02")]
    [InlineData("FW-PRI", "FW-PRI copy")]
    public void Suggested_name_is_the_next_one(string source, string expected)
        => Assert.Equal(expected, DuplicateRequest.Suggest(source));

    [Fact]
    public async Task Duplicate_copies_ports_and_pairings_but_not_cables()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var pp = await db.CreateDeviceAsync(room, "PP-01", DeviceKind.PatchPanel, new() { Count = 4, FrontAndRearPairs = true });
        var sw = await db.CreateDeviceAsync(room, "SW01", DeviceKind.Switch, new() { Prefix = "Gi", Count = 2 });
        await db.Cables.ConnectAsync((await db.PortAsync(pp, "1", PortFace.Front)).Id, (await db.PortAsync(sw, "Gi1")).Id, new Cable());

        var copies = await db.Devices.DuplicateAsync(pp.Id, new DuplicateRequest { Name = "PP-02", Copies = 2, RoomId = room.Id });

        Assert.Equal(["PP-02", "PP-03"], copies.Select(c => c.Name));
        foreach (var c in copies)
        {
            var ports = await db.Devices.GetPortsAsync(c.Id);
            Assert.Equal(8, ports.Count);
            Assert.All(ports, p => Assert.Null(p.CableId));
            // Each front is paired with its own new rear, not the original's.
            var front1 = ports.Single(p => p.Name == "1" && p.Face == PortFace.Front);
            var rear1 = ports.Single(p => p.Name == "1" && p.Face == PortFace.Rear);
            Assert.Equal(rear1.Id, front1.PairedPortId);
            Assert.Equal(front1.Id, rear1.PairedPortId);
        }

        // The original still has its cable.
        Assert.NotNull((await db.PortAsync(pp, "1", PortFace.Front)).CableId);
    }

    [Fact]
    public async Task A_single_copy_can_go_in_a_free_rack_position()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var rack = await db.Locations.SaveRackAsync(new Rack { RoomId = room.Id, Name = "R1", HeightU = 42 });
        var sw = await db.Devices.SaveAsync(new Device { Name = "SW01", Kind = DeviceKind.Switch, RoomId = room.Id, RackId = rack.Id, RackUnit = 40 });

        await Assert.ThrowsAsync<LinktraceException>(() =>
            db.Devices.DuplicateAsync(sw.Id, new DuplicateRequest { Name = "SW02", RoomId = room.Id, RackId = rack.Id, RackUnit = 40 }));

        var copy = (await db.Devices.DuplicateAsync(sw.Id, new DuplicateRequest { Name = "SW02", RoomId = room.Id, RackId = rack.Id, RackUnit = 39 })).Single();
        Assert.Equal(39, copy.RackUnit);
    }
}
