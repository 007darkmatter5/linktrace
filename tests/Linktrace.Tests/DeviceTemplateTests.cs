using System.Text;
using System.Text.Json;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class DeviceTemplateTests
{
    /// <summary>A PDU-like device: network ports with descriptions, USB-C, outlets, a front/rear pair and a hand-built layout with a wide port.</summary>
    private static async Task<Device> BuildPduAsync(TestDb db, Room room)
    {
        var pdu = await db.Devices.SaveAsync(new Device
        {
            Name = "PDU-01", Kind = DeviceKind.Other, Manufacturer = "Ubiquiti", Model = "USP PDU Pro", HeightU = 2,
            SerialNumber = "SN123", ManagementAddress = "10.0.0.5", RoomId = room.Id
        });
        await db.Devices.AddPortsAsync(pdu.Id, new PortGenerationRequest { Prefix = "port", Count = 2 });
        await db.Devices.AddPortsAsync(pdu.Id, new PortGenerationRequest { Prefix = "usb", Count = 2, MediaType = MediaType.UsbC });
        await db.Devices.AddPortsAsync(pdu.Id, new PortGenerationRequest { Prefix = "outlet", Count = 4, MediaType = MediaType.PowerNema515 });
        await db.Devices.AddPortsAsync(pdu.Id, new PortGenerationRequest { Prefix = "pass", Count = 1, FrontAndRearPairs = true });
        var port1 = await db.PortAsync(pdu, "port1");
        port1.Description = "Uplink";
        await db.Devices.SavePortAsync(port1);

        await db.Faceplates.SaveAsync(new FaceplateTemplate
        {
            Name = "USP PDU Pro", Model = "USP PDU Pro", Columns = 8, Rows = 2,
            Placements = [new() { PortName = "port1", Col = 0, Row = 0, Span = 2 }, new() { PortName = "outlet1", Col = 0, Row = 1 }],
            Labels = [new() { Text = "AC", Col = 2, Row = 0, Span = 2 }]
        }, pdu.Id, asNew: true, applyToSameModel: false);
        return pdu;
    }

    private static async Task<(TestDb Db, Room Room, Device Pdu)> SetupAsync()
    {
        var db = new TestDb();
        var room = await db.CreateRoomAsync();
        return (db, room, await BuildPduAsync(db, room));
    }

    [Fact]
    public async Task A_device_saved_as_a_template_keeps_its_ports_pairs_and_layout_but_nothing_specific_to_it()
    {
        var (db, _, pdu) = await SetupAsync();
        await using var __ = db;

        var t = await db.Templates.SaveFromDeviceAsync(pdu.Id, "Ubiquiti USP PDU Pro", includeDescriptions: false, replaceExisting: false);

        var loaded = (await db.Templates.GetAsync(t.Id))!;
        Assert.Equal(("Ubiquiti", "USP PDU Pro", 2, DeviceKind.Other), (loaded.Manufacturer, loaded.Model, loaded.HeightU, loaded.Kind));
        Assert.Equal(10, loaded.Ports.Count);
        Assert.Equal(MediaType.PowerNema515, loaded.Ports.Single(p => p.Name == "outlet3").MediaType);
        var front = loaded.Ports.Single(p => p.Name == "pass1" && p.Face == PortFace.Front);
        Assert.Equal(("pass1", PortFace.Rear), (front.PairName, front.PairFace));
        Assert.All(loaded.Ports, p => Assert.Null(p.Description));      // descriptions left out unless asked
        Assert.Equal("USP PDU Pro", loaded.FaceplateTemplate!.Name);

        var info = (await db.Templates.GetAllAsync()).Single();
        Assert.Equal("4 × Outlet (NEMA 5-15), 3 × RJ45, 2 × USB-C, 1 × Punch-down", info.PortSummary);
    }

    [Fact]
    public async Task A_new_device_from_a_template_gets_its_ports_pairs_and_layout()
    {
        var (db, room, pdu) = await SetupAsync();
        await using var __ = db;
        var t = await db.Templates.SaveFromDeviceAsync(pdu.Id, "PDU", includeDescriptions: true, replaceExisting: false);

        var created = await db.Templates.CreateDeviceAsync(new Device
        {
            Name = "PDU-02", RoomId = room.Id, Kind = t.Kind, Manufacturer = t.Manufacturer, Model = t.Model, HeightU = t.HeightU
        }, t.Id);

        var d = (await db.Devices.GetAsync(created.Id))!;
        Assert.Equal(10, d.Ports.Count);
        Assert.Equal("Uplink", d.Ports.Single(p => p.Name == "port1").Description);
        Assert.Null(d.SerialNumber);
        var front = d.Ports.Single(p => p.Name == "pass1" && p.Face == PortFace.Front);
        var rear = d.Ports.Single(p => p.Name == "pass1" && p.Face == PortFace.Rear);
        Assert.Equal(rear.Id, front.PairedPortId);
        Assert.Equal(front.Id, rear.PairedPortId);
        Assert.Equal(pdu.FaceplateTemplateId ?? (await db.Devices.GetAsync(pdu.Id))!.FaceplateTemplateId, d.FaceplateTemplateId);
        Assert.All(d.Ports, p => Assert.Null(p.CableId));
    }

    [Fact]
    public async Task Template_names_are_unique_unless_you_choose_to_replace()
    {
        var (db, _, pdu) = await SetupAsync();
        await using var __ = db;
        await db.Templates.SaveFromDeviceAsync(pdu.Id, "PDU", false, false);

        await Assert.ThrowsAsync<LinktraceException>(() => db.Templates.SaveFromDeviceAsync(pdu.Id, "pdu", false, false));
        await db.Templates.SaveFromDeviceAsync(pdu.Id, "pdu", true, replaceExisting: true);
        Assert.Single(await db.Templates.GetAllAsync());
    }

    [Fact]
    public async Task A_template_file_carries_everything_to_another_install()
    {
        var (db, _, pdu) = await SetupAsync();
        await using var __ = db;
        var t = await db.Templates.SaveFromDeviceAsync(pdu.Id, "Ubiquiti USP PDU Pro", false, false);
        var json = await db.Templates.ExportJsonAsync([t.Id], "tester");
        Assert.Contains("\"format\": \"linktrace-template\"", Encoding.UTF8.GetString(json));
        Assert.Contains("\"PowerNema515\"", Encoding.UTF8.GetString(json));   // enums by name: readable and stable

        await using var other = new TestDb();
        var preview = await other.Templates.PreviewAsync(new MemoryStream(json));
        Assert.True(preview.CanImport, string.Join("; ", preview.Problems));
        await other.Templates.ImportAsync(preview.File);

        var imported = (await other.Templates.GetAllAsync()).Single();
        Assert.Equal(("Ubiquiti USP PDU Pro", 10, "USP PDU Pro"), (imported.Name, imported.PortCount, imported.LayoutName));

        // The imported template makes a working device there, wide port and all.
        var room = await other.CreateRoomAsync();
        var d = await other.Templates.CreateDeviceAsync(new Device { Name = "PDU-X", RoomId = room.Id }, imported.Id);
        var loaded = (await other.Devices.GetAsync(d.Id))!;
        Assert.Equal(2, loaded.FaceplateTemplate!.Placements.Single(p => p.PortName == "port1").Width);

        // Importing the same file again doesn't clash: the copy gets "(imported)".
        await other.Templates.ImportAsync((await other.Templates.PreviewAsync(new MemoryStream(json))).File);
        Assert.Equal(["Ubiquiti USP PDU Pro", "Ubiquiti USP PDU Pro (imported)"], (await other.Templates.GetAllAsync()).Select(x => x.Name));
    }

    [Fact]
    public async Task Broken_template_files_are_rejected()
    {
        await using var db = new TestDb();
        var bad = new TemplateFile
        {
            Templates =
            [
                new TemplateDto("Bad", DeviceKind.Switch, null, null, 1, 0, PortNumbering.Auto, null,
                    [new("1", PortFace.Standard, MediaType.RJ45, 1, null), new("1", PortFace.Standard, MediaType.RJ45, 2, null, "9", PortFace.Rear)],
                    null)
            ]
        };
        var preview = await db.Templates.PreviewAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(bad, TransferService.Json)));
        Assert.False(preview.CanImport);
        Assert.Contains(preview.Problems, p => p.Contains("two Standard ports named '1'"));
        Assert.Contains(preview.Problems, p => p.Contains("isn't in the template"));

        await Assert.ThrowsAsync<LinktraceException>(() => db.Templates.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes("""{"format":"linktrace"}"""))));
    }

    [Fact]
    public async Task Templates_are_included_in_the_whole_install_export()
    {
        var (db, _, pdu) = await SetupAsync();
        await using var __ = db;
        await db.Templates.SaveFromDeviceAsync(pdu.Id, "PDU template", false, false);

        await using var other = new TestDb();
        var json = await new TransferService(db, db.User).ExportJsonAsync(null);
        var preview = await new TransferService(other, other.User).PreviewAsync(new MemoryStream(json));
        Assert.True(preview.CanImport, string.Join("; ", preview.Problems));
        Assert.Equal(1, preview.Counts["Device templates"]);
        await new TransferService(other, other.User).ImportAsync(preview.File, ImportMode.Replace);

        var t = (await other.Templates.GetAllAsync()).Single();
        Assert.Equal(("PDU template", "USP PDU Pro"), (t.Name, t.LayoutName));
    }
}
