using System.Globalization;
using System.Text;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Components.Shared;

public record PlacedDevice(Device Device, Faceplate Plate, double X, double Y, double Width)
{
    public double Height => Plate.Height;
}

/// <summary>A drawn connection: its SVG path, color, end points, and the data behind it.</summary>
public record DrawnLink(string Path, string Color, bool Dashed, (double X, double Y) A, (double X, double Y) B,
    PortLink? Link, ExternalLink? External, string? Label, (double X, double Y) LabelAt);

/// <summary>
/// Lays out devices stacked top to bottom (like a rack) and routes cables between their ports
/// so lines never cross a device:
/// <list type="bullet">
/// <item>each cable leaves its port from the nearest edge into the horizontal channel above or below the device;</item>
/// <item>cables between adjacent devices run straight across the channel between them;</item>
/// <item>longer runs go around the right-hand side, each in its own vertical lane;</item>
/// <item>cables to devices not in the view end in a labelled stub on the right.</item>
/// </list>
/// </summary>
public class PortMapScene
{
    public const double NameColumn = 170;
    public const double TrackGap = 7;
    public const double ChannelPad = 10;
    public const double MinInnerChannel = 30;
    public const double MinEdgeChannel = 10;
    public const double LaneGap = 9;
    public const double GutterPad = 18;
    public const double CornerRadius = 6;

    // Distinct, readable colors for cables without a recorded color.
    private static readonly string[] Palette =
        ["#1e88e5", "#e53935", "#43a047", "#8e24aa", "#fb8c00", "#00897b", "#d81b60", "#6d4c41", "#3949ab", "#c0ca33"];

    public List<PlacedDevice> Devices { get; } = [];
    public List<DrawnLink> Lines { get; } = [];
    public double Width { get; private set; }
    public double Height { get; private set; }

    public PlacedDevice? DeviceOfPort(int portId) => Devices.FirstOrDefault(d => d.Plate.Find(portId) is not null);
    public Port? FindPort(int portId) => DeviceOfPort(portId)?.Plate.Find(portId)?.Port;

    public static PortMapScene Single(Device device) => Build([device], [], []);

    public static PortMapScene Build(IReadOnlyList<Device> devices, IEnumerable<PortLink> links, IEnumerable<ExternalLink> externals)
    {
        var scene = new PortMapScene();
        var plates = devices.Select(d => FaceplateLayout.Build(d, d.Ports)).ToList();
        var bodyWidth = Math.Max(240, plates.Select(p => p.Width).DefaultIfEmpty(0).Max());
        var bodyX = NameColumn;
        var gutterX = bodyX + bodyWidth + GutterPad;

        // Which device each port is on.
        var portDevice = new Dictionary<int, int>();
        for (var i = 0; i < plates.Count; i++)
            foreach (var c in plates[i].Cells) portDevice[c.Port.Id] = i;

        // ---- 1. Decide which channel each cable end uses. Channel k is the gap above device k (k = count: below the last). ----
        int ExitChannel(int portId, int towardDevice)
        {
            var dev = portDevice[portId];
            var plate = plates[dev];
            var cell = plate.Find(portId)!;
            var minY = plate.Cells.Min(c => c.Y);
            var maxY = plate.Cells.Max(c => c.Y);
            if (minY != maxY && cell.Y == minY) return dev;          // top row: leave upward
            if (minY != maxY && cell.Y == maxY) return dev + 1;      // bottom row: leave downward
            return towardDevice < dev ? dev : dev + 1;               // single row: leave toward the other end
        }

        var routes = new List<Route>();
        var colorIndex = 0;
        foreach (var link in links)
        {
            if (!portDevice.TryGetValue(link.PortAId, out var da) || !portDevice.TryGetValue(link.PortBId, out var db)) continue;
            var color = CableColor(link.Cable?.Color) ?? Palette[colorIndex++ % Palette.Length];
            routes.Add(new Route
            {
                Link = link, Color = color, Dashed = !link.IsDirect,
                PortA = link.PortAId, PortB = link.PortBId,
                ChannelA = ExitChannel(link.PortAId, db), ChannelB = ExitChannel(link.PortBId, da)
            });
        }
        foreach (var ext in externals)
        {
            if (!portDevice.ContainsKey(ext.PortId)) continue;
            routes.Add(new Route
            {
                External = ext, Color = CableColor(ext.Cable?.Color) ?? "var(--mud-palette-text-secondary)",
                Dashed = ext.CableCount > 1, PortA = ext.PortId,
                ChannelA = ExitChannel(ext.PortId, int.MaxValue)
            });
        }

        // ---- 2. Vertical lanes on the right for cables that change channel; shorter spans sit closer to the devices. ----
        var laneEnds = new List<int>(); // per lane: the lowest channel already used
        foreach (var r in routes.Where(r => r.External is null && r.ChannelA != r.ChannelB)
                     .OrderBy(r => Math.Abs(r.ChannelA - r.ChannelB)).ThenBy(r => Math.Min(r.ChannelA, r.ChannelB)))
        {
            var (lo, hi) = (Math.Min(r.ChannelA, r.ChannelB), Math.Max(r.ChannelA, r.ChannelB));
            r.Lane = Enumerable.Range(0, laneEnds.Count).FirstOrDefault(l => r.CanShareLane(routes, l, lo, hi), -1);
            if (r.Lane < 0) { r.Lane = laneEnds.Count; laneEnds.Add(hi); }
        }
        var laneCount = routes.Where(r => r.Lane >= 0).Select(r => r.Lane + 1).DefaultIfEmpty(0).Max();
        double LaneX(int lane) => gutterX + lane * LaneGap;
        var stubX = gutterX + laneCount * LaneGap + (laneCount > 0 ? 8 : 0);

        // ---- 3. Horizontal tracks inside each channel. ----
        var channelCount = devices.Count + 1;
        var segments = Enumerable.Range(0, channelCount).Select(_ => new List<Segment>()).ToList();
        double PortX(int portId) => bodyX + plates[portDevice[portId]].Find(portId)!.CenterX;

        foreach (var r in routes)
        {
            if (r.External is not null)
                segments[r.ChannelA].Add(new Segment(r, true, PortX(r.PortA), stubX));
            else if (r.ChannelA == r.ChannelB)
                segments[r.ChannelA].Add(new Segment(r, true, Math.Min(PortX(r.PortA), PortX(r.PortB)), Math.Max(PortX(r.PortA), PortX(r.PortB))));
            else
            {
                segments[r.ChannelA].Add(new Segment(r, true, PortX(r.PortA), LaneX(r.Lane)));
                segments[r.ChannelB].Add(new Segment(r, false, PortX(r.PortB), LaneX(r.Lane)));
            }
        }

        var trackCounts = new int[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            // Greedy interval packing: segments that don't overlap share a track.
            var trackEnds = new List<double>();
            foreach (var s in segments[c].OrderBy(s => s.From).ThenByDescending(s => s.To))
            {
                var t = trackEnds.FindIndex(end => end + 10 < s.From);
                if (t < 0) { t = trackEnds.Count; trackEnds.Add(s.To); } else trackEnds[t] = s.To;
                s.Track = t;
            }
            trackCounts[c] = trackEnds.Count;
        }

        // ---- 4. Vertical positions. ----
        var channelTop = new double[channelCount];
        var channelHeight = new double[channelCount];
        var y = 0.0;
        for (var c = 0; c < channelCount; c++)
        {
            var isEdge = c == 0 || c == channelCount - 1;
            channelHeight[c] = Math.Max(isEdge ? MinEdgeChannel : MinInnerChannel, trackCounts[c] * TrackGap + 2 * ChannelPad);
            channelTop[c] = y;
            y += channelHeight[c];
            if (c < devices.Count)
            {
                scene.Devices.Add(new PlacedDevice(devices[c], plates[c], bodyX, y, bodyWidth));
                y += plates[c].Height;
            }
        }
        double TrackY(int channel, int track) =>
            channelTop[channel] + (channelHeight[channel] - (trackCounts[channel] - 1) * TrackGap) / 2 + track * TrackGap;

        (double X, double Y) PortEdge(int portId, int channel)
        {
            var d = scene.Devices[portDevice[portId]];
            var cell = d.Plate.Find(portId)!;
            var up = channel == portDevice[portId];
            return (d.X + cell.CenterX, d.Y + (up ? cell.Top : cell.Bottom));
        }

        // ---- 5. Paths. ----
        var maxLabel = 0.0;
        foreach (var r in routes)
        {
            var a = PortEdge(r.PortA, r.ChannelA);
            var sa = segments[r.ChannelA].First(s => s.Route == r && s.IsA);
            var ya = TrackY(r.ChannelA, sa.Track);

            if (r.External is { } ext)
            {
                var label = $"→ {ext.FarText}" + (ext.CableCount > 1 ? $" ({ext.CableCount} cables)" : "");
                maxLabel = Math.Max(maxLabel, label.Length * 6.4);
                scene.Lines.Add(new DrawnLink(Rounded([a, (a.X, ya), (stubX, ya)]), r.Color, r.Dashed, a, (stubX, ya),
                    null, ext, label, (stubX + 6, ya + 4)));
                continue;
            }

            var b = PortEdge(r.PortB, r.ChannelB);
            List<(double, double)> pts;
            if (r.ChannelA == r.ChannelB)
                pts = [a, (a.X, ya), (b.X, ya), b];
            else
            {
                var sb = segments[r.ChannelB].First(s => s.Route == r && !s.IsA);
                var yb = TrackY(r.ChannelB, sb.Track);
                var lx = LaneX(r.Lane);
                pts = [a, (a.X, ya), (lx, ya), (lx, yb), (b.X, yb), b];
            }
            scene.Lines.Add(new DrawnLink(Rounded(pts), r.Color, r.Dashed, a, b, r.Link, null, null, default));
        }

        scene.Width = Math.Max(stubX + (maxLabel > 0 ? maxLabel + 12 : 0), gutterX) + 4;
        scene.Height = y;
        return scene;
    }

    private sealed class Route
    {
        public PortLink? Link;
        public ExternalLink? External;
        public string Color = "";
        public bool Dashed;
        public int PortA, PortB;
        public int ChannelA, ChannelB;
        public int Lane = -1;

        /// <summary>A lane can be reused if no route already in it spans an overlapping range of channels.</summary>
        public bool CanShareLane(List<Route> all, int lane, int lo, int hi) => all
            .Where(o => o != this && o.Lane == lane)
            .All(o => Math.Max(o.ChannelA, o.ChannelB) < lo || Math.Min(o.ChannelA, o.ChannelB) > hi);
    }

    private sealed record Segment(Route Route, bool IsA, double From, double To)
    {
        public int Track { get; set; }
    }

    /// <summary>Polyline with rounded corners.</summary>
    private static string Rounded(List<(double X, double Y)> pts)
    {
        // Drop zero-length steps so corners are computed correctly.
        var p = new List<(double X, double Y)> { pts[0] };
        foreach (var q in pts.Skip(1))
            if (Math.Abs(q.X - p[^1].X) > 0.01 || Math.Abs(q.Y - p[^1].Y) > 0.01) p.Add(q);

        var sb = new StringBuilder($"M {F(p[0].X)} {F(p[0].Y)}");
        for (var i = 1; i < p.Count; i++)
        {
            if (i == p.Count - 1) { sb.Append($" L {F(p[i].X)} {F(p[i].Y)}"); break; }
            var (prev, cur, next) = (p[i - 1], p[i], p[i + 1]);
            var r = Math.Min(CornerRadius, Math.Min(Dist(prev, cur), Dist(cur, next)) / 2);
            var inPt = Toward(cur, prev, r);
            var outPt = Toward(cur, next, r);
            sb.Append($" L {F(inPt.X)} {F(inPt.Y)} Q {F(cur.X)} {F(cur.Y)} {F(outPt.X)} {F(outPt.Y)}");
        }
        return sb.ToString();

        static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        static (double X, double Y) Toward((double X, double Y) from, (double X, double Y) to, double d)
        {
            var len = Dist(from, to);
            return len == 0 ? from : (from.X + (to.X - from.X) / len * d, from.Y + (to.Y - from.Y) / len * d);
        }
    }

    public static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>A CSS color for a recorded cable color name, or null if unknown.</summary>
    public static string? CableColor(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "blue" => "#1e88e5",
        "yellow" => "#f9a825",
        "green" => "#43a047",
        "red" => "#e53935",
        "orange" => "#fb8c00",
        "purple" => "#8e24aa",
        "gray" or "grey" => "#757575",
        "white" => "#9e9e9e",
        "black" => "#212121",
        "pink" => "#ec407a",
        "aqua" => "#00acc1",
        _ => null
    };
}
