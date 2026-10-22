using System.Text;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class PortMapPdfTests
{
    private static int PageCount(byte[] pdf) =>
        System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count;

    [Theory]
    [InlineData(PaperSize.Letter)]
    [InlineData(PaperSize.A3)]
    public async Task A_map_renders_to_a_pdf_with_its_connection_list(PaperSize paper)
    {
        await using var db = new TestDb();
        var room = await db.CreateRoomAsync();
        var sw = await db.CreateDeviceAsync(room, "SW01", DeviceKind.Switch, new() { Prefix = "Gi1/0/", Count = 48 });
        var pp = await db.CreateDeviceAsync(room, "PP-A", DeviceKind.PatchPanel, new() { Count = 48, FrontAndRearPairs = true });
        // Enough cables to need more than one page for the connection list.
        for (var n = 1; n <= 40; n++)
            await db.Cables.ConnectAsync((await db.PortAsync(sw, $"Gi1/0/{n}")).Id, (await db.PortAsync(pp, $"{n}", PortFace.Front)).Id,
                new Cable { Label = $"C-{n:D3}", Color = n % 2 == 0 ? "Blue" : "Yellow" });

        var svc = new PortMapPdfService(db.Devices, new PortMapService(db));
        var pdf = await svc.RenderAsync(new PortMapPdfRequest("Closet · ä/ü", [pp.Id, sw.Id], [0, 1], true, true, paper, "tester"));

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        // Map page + connection list; 40 rows need two list pages on Letter but fit on one A3 page.
        Assert.Equal(paper == PaperSize.Letter ? 3 : 2, PageCount(pdf));
        Assert.Contains("Roboto", Encoding.Latin1.GetString(pdf)); // the bundled font is embedded
    }

    [Fact]
    public async Task An_empty_map_is_refused()
    {
        await using var db = new TestDb();
        var svc = new PortMapPdfService(db.Devices, new PortMapService(db));
        await Assert.ThrowsAsync<LinktraceException>(() =>
            svc.RenderAsync(new PortMapPdfRequest("Nothing", [999], [], true, true, PaperSize.Letter, null)));
    }

    [Theory]
    [InlineData("HuntWeis Comm Rack", "HuntWeis Comm Rack.pdf")]
    [InlineData("a/b:c", "a-b-c.pdf")]
    [InlineData("   ", "port-map.pdf")]
    public void File_names_are_safe(string title, string expected)
        => Assert.Equal(expected, PortMapPdfService.FileName(title).Replace('\\', '-'));
}
