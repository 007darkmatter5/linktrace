using Linktrace.Data.Entities;

namespace Linktrace.Tests;

public class ManufacturerTests
{
    [Fact]
    public async Task Typing_a_known_manufacturer_or_model_in_another_case_reuses_the_existing_spelling()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        await db.Devices.SaveAsync(new Device { Name = "SW1", RoomId = room.Id, Manufacturer = "Ubiquiti", Model = "USW-Pro-48-PoE" });

        var sw2 = await db.Devices.SaveAsync(new Device { Name = "SW2", RoomId = room.Id, Manufacturer = "  ubiquiti ", Model = "usw-pro-48-poe" });

        Assert.Equal("Ubiquiti", sw2.Manufacturer);
        Assert.Equal("USW-Pro-48-PoE", sw2.Model);

        // A device can still change the spelling of its own value when it's the only one using it.
        var odd = await db.Devices.SaveAsync(new Device { Name = "FW", RoomId = room.Id, Manufacturer = "fortinet" });
        var loaded = (await db.Devices.GetAsync(odd.Id))!;
        loaded.Manufacturer = "Fortinet";
        Assert.Equal("Fortinet", (await db.Devices.SaveAsync(loaded)).Manufacturer);
    }

    [Fact]
    public async Task Pickers_list_manufacturers_with_counts_and_the_chosen_makers_models_first()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        await db.Devices.SaveAsync(new Device { Name = "A", RoomId = room.Id, Manufacturer = "Ubiquiti", Model = "USW-24" });
        await db.Devices.SaveAsync(new Device { Name = "B", RoomId = room.Id, Manufacturer = "Ubiquiti", Model = "UDM-Pro" });
        await db.Devices.SaveAsync(new Device { Name = "C", RoomId = room.Id, Manufacturer = "Cisco", Model = "C9300" });
        await db.Devices.SaveAsync(new Device { Name = "D", RoomId = room.Id });

        var makers = await db.Devices.ManufacturersAsync();
        Assert.Equal([("Cisco", 1), ("Ubiquiti", 2)], makers);

        var models = await db.Devices.ModelsAsync("ubiquiti");
        Assert.Equal(["UDM-Pro", "USW-24", "C9300"], models.Select(m => m.Model));
    }
}
