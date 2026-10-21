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
