using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class RackPlacementTests
{
    [Theory]
    [InlineData(42, new int[0], 1, 42)]               // empty rack: top slot
    [InlineData(42, new[] { 42, 1 }, 1, 41)]          // top taken: next one down
    [InlineData(42, new[] { 41, 2 }, 2, 39)]          // 2U device under a 2U device at U41-42
    [InlineData(4, new[] { 1, 4 }, 1, null)]          // full rack
    [InlineData(4, new[] { 3, 1 }, 2, 1)]             // 2U only fits at U1-2
    public void Suggests_the_highest_free_position(int rackHeight, int[] occupied, int height, int? expected)
    {
        var taken = occupied.Chunk(2).Select(c => (c[0], c[1]));
        Assert.Equal(expected, RackSlots.SuggestUnit(rackHeight, taken, height));
    }

    private static async Task<(TestDb Db, Rack Rack, Room ServerRoom, Room Office)> SetupAsync()
    {
        var db = new TestDb();
        var site = await db.Locations.SaveSiteAsync(new Site { Name = "HQ" });
        var serverRoom = await db.Locations.SaveRoomAsync(new Room { SiteId = site.Id, Name = "Server room" });
        var office = await db.Locations.SaveRoomAsync(new Room { SiteId = site.Id, Name = "Office" });
        var rack = await db.Locations.SaveRackAsync(new Rack { RoomId = serverRoom.Id, Name = "R1", HeightU = 10 });
        return (db, rack, serverRoom, office);
    }

    [Fact]
    public async Task Existing_devices_from_anywhere_on_the_site_can_be_put_in_a_rack()
    {
        var (db, rack, serverRoom, office) = await SetupAsync();
        await using var _ = db;
        var sw = await db.Devices.SaveAsync(new Device { Name = "SW01", Kind = DeviceKind.Switch, RoomId = serverRoom.Id });
        var fw = await db.Devices.SaveAsync(new Device { Name = "FW01", Kind = DeviceKind.Firewall, RoomId = office.Id, HeightU = 2 });
        var otherSite = await db.Locations.SaveSiteAsync(new Site { Name = "Branch" });
        var branchRoom = await db.Locations.SaveRoomAsync(new Room { SiteId = otherSite.Id, Name = "Closet" });
        var far = await db.Devices.SaveAsync(new Device { Name = "Far away", RoomId = branchRoom.Id });

        var candidates = await db.Devices.RackCandidatesAsync(rack.Id);
        Assert.Equal(["SW01", "FW01"], candidates.Select(d => d.Name)); // same room first; other sites excluded

        await db.Devices.PlaceInRackAsync(rack.Id, [new(sw.Id, 10), new(fw.Id, null)]);

        var loadedFw = (await db.Devices.GetAsync(fw.Id))!;
        Assert.Equal(rack.Id, loadedFw.RackId);
        Assert.Equal(serverRoom.Id, loadedFw.RoomId);   // moved into the rack's room
        Assert.Null(loadedFw.RackUnit);
        Assert.Equal(10, (await db.Devices.GetAsync(sw.Id))!.RackUnit);
        // FW01 is in the rack but has no position yet, so it's still offered (to give it one); SW01 isn't.
        Assert.Equal(["FW01"], (await db.Devices.RackCandidatesAsync(rack.Id)).Select(d => d.Name));

        await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.PlaceInRackAsync(rack.Id, [new(far.Id, null)]));
    }

    [Fact]
    public async Task Overlapping_or_oversized_positions_are_rejected_and_nothing_changes()
    {
        var (db, rack, serverRoom, _) = await SetupAsync();
        await using var __ = db;
        var a = await db.Devices.SaveAsync(new Device { Name = "A", RoomId = serverRoom.Id, RackId = rack.Id, RackUnit = 5, HeightU = 2 });
        var b = await db.Devices.SaveAsync(new Device { Name = "B", RoomId = serverRoom.Id });
        var c = await db.Devices.SaveAsync(new Device { Name = "C", RoomId = serverRoom.Id, HeightU = 3 });

        var overlap = await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.PlaceInRackAsync(rack.Id, [new(b.Id, 1), new(c.Id, 4)]));
        Assert.Contains("overlaps 'A'", overlap.Message);
        Assert.Null((await db.Devices.GetAsync(b.Id))!.RackId); // all or nothing

        await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.PlaceInRackAsync(rack.Id, [new(c.Id, 9)])); // U9-11 > 10U
        await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.PlaceInRackAsync(rack.Id, [new(b.Id, 1), new(c.Id, 1)])); // with each other

        await db.Devices.PlaceInRackAsync(rack.Id, [new(b.Id, 1), new(c.Id, 2)]);
        Assert.Equal(2, (await db.Devices.GetAsync(c.Id))!.RackUnit);
    }

    [Fact]
    public async Task A_new_height_given_when_placing_is_saved_and_used_for_the_checks()
    {
        var (db, rack, serverRoom, _) = await SetupAsync();
        await using var __ = db;
        var top = await db.Devices.SaveAsync(new Device { Name = "Top", RoomId = serverRoom.Id, RackId = rack.Id, RackUnit = 10 });
        var server = await db.Devices.SaveAsync(new Device { Name = "Server", RoomId = serverRoom.Id }); // saved as 1U

        // As 2U at U9 it would reach U10, where "Top" is.
        await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.PlaceInRackAsync(rack.Id, [new(server.Id, 9, 2)]));
        Assert.Equal(1, (await db.Devices.GetAsync(server.Id))!.HeightU); // unchanged after the rejected attempt

        await db.Devices.PlaceInRackAsync(rack.Id, [new(server.Id, 8, 2)]);
        var loaded = (await db.Devices.GetAsync(server.Id))!;
        Assert.Equal((8, 2), (loaded.RackUnit!.Value, loaded.HeightU));
    }

    [Fact]
    public async Task Height_can_be_set_on_a_device_that_is_not_in_a_rack()
    {
        var (db, _, serverRoom, _) = await SetupAsync();
        await using var __ = db;
        var d = await db.Devices.SaveAsync(new Device { Name = "NAS", RoomId = serverRoom.Id, HeightU = 2 });
        Assert.Equal(2, (await db.Devices.GetAsync(d.Id))!.HeightU);
    }

    [Fact]
    public async Task A_device_in_the_rack_without_a_position_can_be_given_one_and_moved_later()
    {
        var (db, rack, serverRoom, _) = await SetupAsync();
        await using var __ = db;
        var sw = await db.Devices.SaveAsync(new Device { Name = "SW", RoomId = serverRoom.Id, RackId = rack.Id, RackUnit = 10 });
        var pdu = await db.Devices.SaveAsync(new Device { Name = "USP PDU Pro", RoomId = serverRoom.Id, RackId = rack.Id }); // no position

        Assert.Equal("USP PDU Pro", (await db.Devices.RackCandidatesAsync(rack.Id)).First().Name); // offered first

        await db.Devices.PlaceInRackAsync(rack.Id, [new(pdu.Id, 1)]);
        Assert.Equal(1, (await db.Devices.GetAsync(pdu.Id))!.RackUnit);

        // Moving it: its old slot doesn't count against it, other devices still do.
        await db.Devices.PlaceInRackAsync(rack.Id, [new(pdu.Id, 2)]);
        Assert.Equal(2, (await db.Devices.GetAsync(pdu.Id))!.RackUnit);
        await Assert.ThrowsAsync<LinktraceException>(() => db.Devices.PlaceInRackAsync(rack.Id, [new(pdu.Id, 10)]));

        // And it can go back to having no position.
        await db.Devices.PlaceInRackAsync(rack.Id, [new(pdu.Id, null)]);
        var loaded = (await db.Devices.GetAsync(pdu.Id))!;
        Assert.Equal((rack.Id, (int?)null), (loaded.RackId!.Value, loaded.RackUnit));
    }

    [Fact]
    public async Task Taking_a_device_out_of_a_rack_keeps_it_in_the_room()
    {
        var (db, rack, serverRoom, _) = await SetupAsync();
        await using var _ = db;
        var sw = await db.Devices.SaveAsync(new Device { Name = "SW01", RoomId = serverRoom.Id, RackId = rack.Id, RackUnit = 3 });

        await db.Devices.RemoveFromRackAsync(sw.Id, sw.Version);

        var loaded = (await db.Devices.GetAsync(sw.Id))!;
        Assert.Null(loaded.RackId);
        Assert.Null(loaded.RackUnit);
        Assert.Equal(serverRoom.Id, loaded.RoomId);

        // Someone else puts it back in the rack; taking it out again from an old copy of the page is rejected.
        await db.Devices.PlaceInRackAsync(rack.Id, [new(sw.Id, 3)]);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.Devices.RemoveFromRackAsync(sw.Id, sw.Version));
    }
}
