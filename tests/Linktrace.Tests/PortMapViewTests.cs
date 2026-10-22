using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class PortMapViewTests
{
    [Fact]
    public async Task Saved_maps_keep_device_order_and_options()
    {
        await using var db = new TestDb();
        var svc = new PortMapViewService(db, db.User);
        var room = await db.CreateRoomAsync();
        var a = await db.CreateDeviceAsync(room, "A", DeviceKind.Router);
        var b = await db.CreateDeviceAsync(room, "B", DeviceKind.Firewall);
        var c = await db.CreateDeviceAsync(room, "C", DeviceKind.Switch);

        var saved = await svc.SaveAsync(new PortMapView { Name = "WAN edge", DeviceIds = [c.Id, a.Id, b.Id], ShowExternal = false });
        var loaded = await svc.GetAsync(saved.Id);

        Assert.Equal([c.Id, a.Id, b.Id], loaded!.DeviceIds);
        Assert.False(loaded.ShowExternal);
        Assert.True(loaded.FollowPassThrough);

        // Reorder and save again.
        loaded.DeviceIds = [a.Id, b.Id, c.Id];
        var updated = await svc.SaveAsync(loaded);
        Assert.Equal([a.Id, b.Id, c.Id], (await svc.GetAsync(saved.Id))!.DeviceIds);

        // Someone saving an older copy gets a conflict instead of overwriting.
        loaded.DeviceIds = [b.Id];
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => svc.SaveAsync(loaded));

        Assert.Single(await svc.GetAllAsync());
        await svc.DeleteAsync(updated.Id);
        Assert.Empty(await svc.GetAllAsync());
    }

    [Fact]
    public async Task Side_by_side_layout_is_saved_and_survives_export_and_import()
    {
        await using var db = new TestDb();
        var svc = new PortMapViewService(db, db.User);
        var room = await db.CreateRoomAsync();
        var wan = await db.CreateDeviceAsync(room, "WAN", DeviceKind.Router);
        var circuit = await db.CreateDeviceAsync(room, "Circuit", DeviceKind.Other);
        var fw = await db.CreateDeviceAsync(room, "FW", DeviceKind.Firewall);

        // Circuit beside WAN; a stray column on the first device and a duplicate are cleaned up.
        var saved = await svc.SaveAsync(new PortMapView
        {
            Name = "Edge", DeviceIds = [wan.Id, circuit.Id, fw.Id, fw.Id], Columns = [3, 1, 0, 1], Spans = [0, 0, 2, 0]
        });
        var loaded = (await svc.GetAsync(saved.Id))!;
        Assert.Equal([wan.Id, circuit.Id, fw.Id], loaded.DeviceIds);
        Assert.Equal([0, 1, 0], loaded.Columns);
        Assert.Equal([0, 0, 2], loaded.Spans);

        await using var other = new TestDb();
        var json = await new TransferService(db, db.User).ExportJsonAsync(null);
        var preview = await new TransferService(other, other.User).PreviewAsync(new MemoryStream(json));
        await new TransferService(other, other.User).ImportAsync(preview.File, ImportMode.Replace);
        var imported = (await new PortMapViewService(other, other.User).GetAllAsync()).Single();
        var importedView = (await new PortMapViewService(other, other.User).GetAsync(imported.Id))!;
        Assert.Equal([0, 1, 0], importedView.Columns);
        Assert.Equal([0, 0, 2], importedView.Spans);
    }

    [Fact]
    public async Task Names_must_be_unique_and_maps_need_devices()
    {
        await using var db = new TestDb();
        var svc = new PortMapViewService(db, db.User);
        var room = await db.CreateRoomAsync();
        var a = await db.CreateDeviceAsync(room, "A", DeviceKind.Router);

        await svc.SaveAsync(new PortMapView { Name = "Edge", DeviceIds = [a.Id] });
        await Assert.ThrowsAsync<LinktraceException>(() => svc.SaveAsync(new PortMapView { Name = "Edge", DeviceIds = [a.Id] }));
        await Assert.ThrowsAsync<LinktraceException>(() => svc.SaveAsync(new PortMapView { Name = "Empty" }));
    }

    [Fact]
    public async Task Sort_by_location_puts_the_top_of_the_rack_first()
    {
        await using var db = new TestDb();
        var svc = new PortMapViewService(db, db.User);
        var room = await db.CreateRoomAsync();
        var rack = await db.Locations.SaveRackAsync(new Rack { RoomId = room.Id, Name = "R1", HeightU = 42 });
        var low = await db.Devices.SaveAsync(new Device { Name = "Low", RoomId = room.Id, RackId = rack.Id, RackUnit = 2 });
        var high = await db.Devices.SaveAsync(new Device { Name = "High", RoomId = room.Id, RackId = rack.Id, RackUnit = 30 });
        var loose = await db.Devices.SaveAsync(new Device { Name = "Desk", RoomId = room.Id });

        var sorted = await svc.SortByLocationAsync([loose.Id, low.Id, high.Id]);

        Assert.Equal([high.Id, low.Id, loose.Id], sorted);
    }
}
