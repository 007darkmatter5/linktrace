using Linktrace.Components.Shared;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Tests;

public class PortMapSceneTests
{
    private static int _nextId = 1;

    private static Device Dev(string name, DeviceKind kind, int ports)
    {
        var d = new Device { Id = _nextId++, Name = name, Kind = kind };
        d.Ports = Enumerable.Range(1, ports).Select(n => new Port { Id = _nextId++, Name = $"{n}", Position = n, DeviceId = d.Id }).ToList();
        return d;
    }

    [Fact]
    public void Devices_stack_top_to_bottom_without_overlapping()
    {
        var a = Dev("SP WAN", DeviceKind.Router, 4);
        var b = Dev("Pri", DeviceKind.Firewall, 8);
        var c = Dev("Sec", DeviceKind.Firewall, 8);

        var scene = PortMapScene.Build([a, b, c], [], []);

        for (var i = 1; i < scene.Devices.Count; i++)
            Assert.True(scene.Devices[i].Y >= scene.Devices[i - 1].Y + scene.Devices[i - 1].Height + PortMapScene.MinInnerChannel - 0.01);
    }

    [Fact]
    public void Lines_never_cross_a_device_body()
    {
        var wan1 = Dev("SP WAN", DeviceKind.Router, 4);
        var wan2 = Dev("SL WAN", DeviceKind.Router, 4);
        var pri = Dev("Pri", DeviceKind.Firewall, 8);
        var sec = Dev("Sec", DeviceKind.Firewall, 8);
        // WAN 1 → both firewalls (skipping a device), plus an HA link between the firewalls.
        PortLink L(Device x, int px, Device y, int py) => new(x.Ports[px].Id, y.Ports[py].Id, new Cable(), 1);
        var links = new[] { L(wan1, 0, pri, 0), L(wan1, 1, sec, 0), L(wan2, 0, pri, 1), L(pri, 7, sec, 7) };
        var external = new ExternalLink(sec.Ports[3].Id, null, 1, 999, "CORE › 9");

        var scene = PortMapScene.Build([wan1, wan2, pri, sec], links, [external]);

        Assert.Equal(5, scene.Lines.Count);
        foreach (var line in scene.Lines)
        {
            foreach (var (x, y) in SamplePath(line.Path))
            {
                foreach (var d in scene.Devices)
                {
                    // Only the tiny stubs that enter a port may touch a device (at the port itself).
                    var insideBody = x > d.X + 1 && x < d.X + d.Width - 1 && y > d.Y + 1 && y < d.Y + d.Height - 1;
                    var atPort = d.Plate.Cells.Any(c => Math.Abs(d.X + c.CenterX - x) < 0.5);
                    Assert.False(insideBody && !atPort, $"Line {line.Path} crosses {d.Device.Name} at ({x},{y})");
                }
            }
        }
        // The cable to a device that isn't shown ends in a labelled stub on the right.
        var stub = scene.Lines.Single(l => l.External is not null);
        Assert.Contains("CORE › 9", stub.Label);
        Assert.True(stub.B.X > scene.Devices.Max(d => d.X + d.Width));
    }

    [Fact]
    public void Devices_can_sit_side_by_side_in_a_row_without_lines_crossing_them()
    {
        // Like the WAN edge: a circuit beside the router it feeds, firewalls below.
        var circuit = Dev("Spectrum Circuit", DeviceKind.Other, 1);
        var wan = Dev("WAN Spectrum", DeviceKind.Switch, 4);
        var pri = Dev("FW Primary", DeviceKind.Firewall, 8);
        var sec = Dev("FW Secondary", DeviceKind.Firewall, 8);
        PortLink L(Device x, int px, Device y, int py) => new(x.Ports[px].Id, y.Ports[py].Id, new Cable(), 1);
        var links = new[] { L(circuit, 0, wan, 2), L(wan, 0, pri, 0), L(wan, 1, sec, 0), L(pri, 7, sec, 7) };

        var scene = PortMapScene.BuildRows([[wan, circuit], [pri], [sec]], links, []);

        var w = scene.Devices.Single(d => d.Device == wan);
        var c = scene.Devices.Single(d => d.Device == circuit);
        Assert.Equal(w.Y, c.Y);                                        // same row
        Assert.True(c.X >= w.X + w.Width + PortMapScene.NameColumn);   // to the right, with room for its name
        Assert.Equal((0, 1), (c.Row, c.Column));
        Assert.True(scene.Devices.Single(d => d.Device == pri).Y > w.Y + w.Height);

        Assert.Equal(4, scene.Lines.Count);
        foreach (var line in scene.Lines)
            foreach (var (x, y) in SamplePath(line.Path))
                foreach (var d in scene.Devices)
                {
                    var insideBody = x > d.X + 1 && x < d.X + d.Width - 1 && y > d.Y + 1 && y < d.Y + d.Height - 1;
                    var atPort = d.Plate.Cells.Any(cell => Math.Abs(d.X + cell.CenterX - x) < 0.5);
                    Assert.False(insideBody && !atPort, $"Line {line.Path} crosses {d.Device.Name} at ({x},{y})");
                }
    }

    [Fact]
    public void A_wide_device_alone_on_its_row_does_not_push_side_by_side_devices_apart()
    {
        // The screenshot case: two small routers side by side, a 48-port switch on its own row below.
        var a = Dev("EFG Primary", DeviceKind.Router, 6);
        var b = Dev("EFG Secondary", DeviceKind.Router, 6);
        var big = Dev("USW Pro 48", DeviceKind.Switch, 52);

        var scene = PortMapScene.BuildRows([[a, b], [big]], [], []);

        var pa = scene.Devices.Single(d => d.Device == a);
        var pb = scene.Devices.Single(d => d.Device == b);
        var pbig = scene.Devices.Single(d => d.Device == big);
        Assert.Equal(pa.X + pa.Width + PortMapScene.ColumnGap + PortMapScene.NameColumn, pb.X);  // b sits right after a
        Assert.True(pbig.Width > pb.X + pb.Width - pbig.X);                                       // the switch is wider than both…
        Assert.Equal(pa.X, pbig.X);                                                               // …and simply extends to the right
    }

    [Fact]
    public void An_explicit_span_fills_its_columns_and_pushes_only_its_own_row()
    {
        var a = Dev("A", DeviceKind.Router, 4);
        var b = Dev("B", DeviceKind.Router, 4);
        var c = Dev("C", DeviceKind.Router, 4);
        var small = Dev("Small", DeviceKind.Other, 2);
        var big = Dev("Big", DeviceKind.Switch, 52);
        var d = Dev("D", DeviceKind.Router, 4);

        var scene = PortMapScene.BuildGrid(
            [[new(a), new(b), new(c)], [new(small, Span: 2), new(d)], [new(big, Span: 2), new(Dev("E", DeviceKind.Router, 4))]], [], []);
        PlacedDevice P(Device x) => scene.Devices.Single(p => p.Device == x);

        // "Small" spans columns 0-1, so D lands in column 2, lined up with C; Small stretches to fill both columns.
        Assert.Equal(P(c).X, P(d).X);
        Assert.Equal(P(b).X + P(b).Width, P(small).X + P(small).Width, 3);
        Assert.Equal(2, P(d).Column);

        // "Big" is wider than its two columns: it extends, and only the device after it on its row moves.
        var e = scene.Devices.Last();
        Assert.True(e.X >= P(big).X + P(big).Width + PortMapScene.ColumnGap);
        Assert.Equal(P(c).X, P(d).X); // other rows unaffected

        // Nothing overlaps within a row.
        foreach (var row in scene.Devices.GroupBy(p => p.Row))
            foreach (var (left, right) in row.OrderBy(p => p.X).Zip(row.OrderBy(p => p.X).Skip(1)))
                Assert.True(right.X >= left.X + left.Width, $"{left.Device.Name} overlaps {right.Device.Name}");
    }

    [Fact]
    public void A_cable_between_neighbours_crosses_directly_instead_of_going_round_the_side()
    {
        // The screenshot case: a router with front ports over rear ports, and beside it a one-port circuit.
        // Its front port 3 (top row) is cabled to the circuit's only port.
        var wan = new Device { Id = 9001, Name = "WAN Spectrum", Kind = DeviceKind.Switch };
        wan.Ports =
        [
            new Port { Id = 9101, Name = "3", Face = PortFace.Front, Position = 1, DeviceId = wan.Id },
            new Port { Id = 9102, Name = "4", Face = PortFace.Rear, Position = 2, DeviceId = wan.Id }
        ];
        var circuit = Dev("Spectrum Circuit", DeviceKind.Other, 1);
        var below = Dev("Below", DeviceKind.Router, 4);
        var link = new PortLink(wan.Ports[0].Id, circuit.Ports[0].Id, new Cable(), 1);

        var scene = PortMapScene.BuildRows([[wan, circuit], [below]], [link], []);
        var line = scene.Lines.Single();

        // Up from port 3, across the gap above both devices, down into the circuit: 4 corners, no detour to the right.
        Assert.Equal(4, line.Points.Count);
        var rightmostDevice = scene.Devices.Max(d => d.X + d.Width);
        Assert.True(line.Points.Max(p => p.X) <= rightmostDevice, "the cable should not go round the right-hand side");
        Assert.True(line.Points.All(p => p.Y <= scene.Devices.Single(d => d.Device == wan).Y + FaceplateLayout.Padding + FaceplateLayout.LabelHeight + 1));
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0 }, "1|2|3")]
    [InlineData(new[] { 0, 1, 0 }, "1,2|3")]
    [InlineData(new[] { 5, 1, 1 }, "1,2,3")]      // the first device always starts a row
    [InlineData(new int[0], "1|2|3")]             // older saved maps without columns
    public void Columns_group_devices_into_rows(int[] columns, string expected)
    {
        var rows = PortMapLayout.ToRows([1, 2, 3], columns);
        Assert.Equal(expected, string.Join("|", rows.Select(r => string.Join(",", r))));
    }

    /// <summary>Points along the straight parts of a path ("M x y L x y Q …").</summary>
    private static IEnumerable<(double X, double Y)> SamplePath(string path)
    {
        var nums = path.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var pts = Enumerable.Range(0, nums.Count / 2).Select(i => (X: nums[2 * i], Y: nums[2 * i + 1])).ToList();
        for (var i = 1; i < pts.Count; i++)
            for (var t = 0.0; t <= 1.0; t += 0.05)
                yield return (pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t);
    }
}
