using System.Text;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class ConnectorTypeTests
{
    [Theory]
    [InlineData(MediaType.RJ45, MediaType.SFPPlus, true)]        // via a module
    [InlineData(MediaType.LC, MediaType.SFP, true)]
    [InlineData(MediaType.UsbC, MediaType.UsbA, true)]
    [InlineData(MediaType.PowerNema515, MediaType.PowerC14, true)] // outlet to device inlet
    [InlineData(MediaType.PowerC13, MediaType.RJ45, false)]
    [InlineData(MediaType.UsbC, MediaType.LC, false)]
    [InlineData(MediaType.Other, MediaType.PowerC13, true)]        // unknown: don't nag
    public void Unlike_connectors_are_flagged(MediaType a, MediaType b, bool ok)
    {
        Assert.Equal(ok, Display.CanConnect(a, b));
        Assert.Equal(ok, Display.CanConnect(b, a));
    }

    [Theory]
    [InlineData(MediaType.UsbC, CableType.Usb)]
    [InlineData(MediaType.PowerC13, CableType.Power)]
    [InlineData(MediaType.PowerNema515, CableType.Power)]
    [InlineData(MediaType.RJ45, CableType.Cat6)]
    [InlineData(MediaType.LC, CableType.FiberMultiModeOM4)]
    public void Connecting_suggests_a_matching_cable(MediaType media, CableType expected)
        => Assert.Equal(expected, Display.SuggestedCable(media));

    [Fact]
    public void Every_usb_and_power_connector_has_a_drawing_and_network_ports_keep_their_numbers()
    {
        foreach (var m in Enum.GetValues<MediaType>())
        {
            var family = Display.Family(m);
            var glyph = PortGlyphs.For(m);
            if (family is MediaFamily.Usb or MediaFamily.Power)
            {
                Assert.NotNull(glyph);
                Assert.All(glyph, s => Assert.True(s.X >= 0 && s.Y >= 0 && s.X + s.W <= FaceplateLayout.Cell && s.Y + s.H <= FaceplateLayout.Cell,
                    $"{m}: shape outside the port square"));
            }
            else Assert.Null(glyph);
        }
    }

    [Fact]
    public async Task A_power_strip_with_usb_and_outlets_saves_cables_and_prints()
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var pdu = await db.CreateDeviceAsync(room, "PDU", DeviceKind.Other, new() { Prefix = "port", Count = 8 });
        await db.Devices.AddPortsAsync(pdu.Id, new PortGenerationRequest { Prefix = "usb", Count = 2, MediaType = MediaType.UsbC });
        await db.Devices.AddPortsAsync(pdu.Id, new PortGenerationRequest { Prefix = "outlet", Count = 4, MediaType = MediaType.PowerNema515 });
        var nas = await db.CreateDeviceAsync(room, "NAS", DeviceKind.Server, new() { Prefix = "psu", Count = 1, MediaType = MediaType.PowerC14 });
        await db.Devices.AddPortsAsync(nas.Id, new PortGenerationRequest { Prefix = "usb", Count = 1, MediaType = MediaType.UsbA });

        await db.Cables.ConnectAsync((await db.PortAsync(pdu, "outlet1")).Id, (await db.PortAsync(nas, "psu1")).Id, new Cable { CableType = CableType.Power, Color = "Black" });
        await db.Cables.ConnectAsync((await db.PortAsync(pdu, "usb1")).Id, (await db.PortAsync(nas, "usb1")).Id, new Cable { CableType = CableType.Usb });

        var ports = await db.Devices.GetPortsAsync(pdu.Id);
        Assert.Equal(MediaType.PowerNema515, ports.Single(p => p.Name == "outlet1").MediaType); // round-trips through the database

        var pdf = await new PortMapPdfService(db.Devices, new PortMapService(db))
            .RenderAsync(new PortMapPdfRequest("Power", [pdu.Id, nas.Id], [], true, true, PaperSize.Letter, null));
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        if (Environment.GetEnvironmentVariable("LINKTRACE_PDF_OUT") is { Length: > 0 } outPath)
            await File.WriteAllBytesAsync(outPath, pdf);
    }
}
