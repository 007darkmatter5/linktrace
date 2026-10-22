using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class BulkPortTests
{
    [Fact]
    public async Task Selected_ports_move_to_the_front_and_change_together()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var sw = await db.CreateDeviceAsync(room, "SW", DeviceKind.Switch, new() { Count = 6 });
        var ports = await db.Devices.GetPortsAsync(sw.Id);
        var picked = ports.Where(p => int.Parse(p.Name) <= 4).Select(p => p.Id).ToList();

        var n = await db.Devices.UpdatePortsAsync(sw.Id, picked,
            new BulkPortChange { Face = PortFace.Front, MediaType = MediaType.Keystone, SetDescription = true, Description = "Desk drops" });

        Assert.Equal(4, n);
        var after = await db.Devices.GetPortsAsync(sw.Id);
        Assert.All(after.Where(p => picked.Contains(p.Id)), p =>
        {
            Assert.Equal(PortFace.Front, p.Face);
            Assert.Equal(MediaType.Keystone, p.MediaType);
            Assert.Equal("Desk drops", p.Description);
        });
        Assert.All(after.Where(p => !picked.Contains(p.Id)), p => Assert.Equal(PortFace.Standard, p.Face)); // others untouched

        // "Don't change" leaves things alone; an empty description clears it.
        await db.Devices.UpdatePortsAsync(sw.Id, picked, new BulkPortChange { SetDescription = true, Description = "" });
        after = await db.Devices.GetPortsAsync(sw.Id);
        Assert.All(after.Where(p => picked.Contains(p.Id)), p => { Assert.Null(p.Description); Assert.Equal(PortFace.Front, p.Face); });
    }

    [Fact]
    public async Task Moving_ports_onto_a_side_that_already_has_those_names_is_refused_and_nothing_changes()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var pp = await db.CreateDeviceAsync(room, "PP", DeviceKind.PatchPanel, new() { Count = 2, FrontAndRearPairs = true });
        var rears = (await db.Devices.GetPortsAsync(pp.Id)).Where(p => p.Face == PortFace.Rear).Select(p => p.Id).ToList();

        var ex = await Assert.ThrowsAsync<LinktraceException>(() =>
            db.Devices.UpdatePortsAsync(pp.Id, rears, new BulkPortChange { Face = PortFace.Front, MediaType = MediaType.LC }));
        Assert.Contains("1, 2", ex.Message);
        Assert.All((await db.Devices.GetPortsAsync(pp.Id)).Where(p => rears.Contains(p.Id)), p =>
        {
            Assert.Equal(PortFace.Rear, p.Face);
            Assert.Equal(MediaType.PunchDown, p.MediaType); // the connector change didn't happen either
        });

        // Two selected ports with the same name can't both become standard ports.
        var both1 = (await db.Devices.GetPortsAsync(pp.Id)).Where(p => p.Name == "1").Select(p => p.Id).ToList();
        await Assert.ThrowsAsync<LinktraceException>(() =>
            db.Devices.UpdatePortsAsync(pp.Id, both1, new BulkPortChange { Face = PortFace.Standard }));
    }

    [Fact]
    public async Task A_layout_used_only_by_this_device_follows_the_ports_to_their_new_side()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var d = await db.CreateDeviceAsync(room, "Jack panel", DeviceKind.Other, new() { Count = 2 });
        await db.Faceplates.SaveAsync(new FaceplateTemplate
        {
            Name = "Jacks", Columns = 2, Rows = 1,
            Placements = [new() { PortName = "1", Col = 0, Row = 0 }, new() { PortName = "2", Col = 1, Row = 0 }]
        }, d.Id, asNew: true, applyToSameModel: false);

        var ids = (await db.Devices.GetPortsAsync(d.Id)).Select(p => p.Id).ToList();
        await db.Devices.UpdatePortsAsync(d.Id, ids, new BulkPortChange { Face = PortFace.Front });

        var device = (await db.Devices.GetAsync(d.Id))!;
        Assert.All(device.FaceplateTemplate!.Placements, pl => Assert.Equal(PortFace.Front, pl.Face));
        var plate = FaceplateLayout.Build(device, device.Ports);
        Assert.DoesNotContain(plate.Blocks, b => b.Title.StartsWith("Not placed")); // still on the grid
    }
}
