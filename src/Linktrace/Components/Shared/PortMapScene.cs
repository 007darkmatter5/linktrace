using System.Globalization;
using System.Text;
using Linktrace.Data.Entities;
using Linktrace.Services;

namespace Linktrace.Components.Shared;

/// <summary>A device's position in the drawing. <paramref name="Row"/>/<paramref name="Column"/> are its grid slot;
/// <paramref name="Span"/> is how many columns it covers (0 = the rest of its row).</summary>
public record PlacedDevice(Device Device, Faceplate Plate, double X, double Y, double Width, int Row = 0, int Column = 0, int Span = 1)
{
    public double Height => Plate.Height;
}

/// <summary>A device in a port map row and how many columns it covers (0 = automatic).</summary>
public record MapCell(Device Device, int Span = 0);

/// <summary>A drawn connection: its SVG path, color, end points, and the data behind it.</summary>
/// <param name="Points">The corners of the line before rounding (used for PDF export).</param>
public record DrawnLink(string Path, string Color, bool Dashed, (double X, double Y) A, (double X, double Y) B,
    PortLink? Link, ExternalLink? External, string? Label, (double X, double Y) LabelAt,
    IReadOnlyList<(double X, double Y)> Points);

/// <summary>
/// Lays out devices in rows top to bottom (like a rack), optionally with several devices side by side
/// in a row, and routes cables between their ports so lines never cross a device:
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
    public const double ColumnGap = 24;
    public const double MinBodyWidth = 240;
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

    /// <summary>One device per row, top to bottom.</summary>
    public static PortMapScene Build(IReadOnlyList<Device> devices, IEnumerable<PortLink> links, IEnumerable<ExternalLink> externals)
        => BuildRows(devices.Select(d => (IReadOnlyList<Device>)[d]).ToList(), links, externals);

    /// <summary>Rows top to bottom; each row holds one or more devices left to right (all with automatic spans).</summary>
    public static PortMapScene BuildRows(IReadOnlyList<IReadOnlyList<Device>> rows, IEnumerable<PortLink> links, IEnumerable<ExternalLink> externals)
        => BuildGrid(rows.Select(r => (IReadOnlyList<MapCell>)r.Select(d => new MapCell(d)).ToList()).ToList(), links, externals);

    /// <summary>
    /// Lays devices out like a table. Rows go top to bottom and devices left to right; devices in the same
    /// column line up. A device covers <see cref="MapCell.Span"/> columns; with an automatic span (0) the last
    /// device in a row fills the rest of the row and any other device covers one column. Only devices that
    /// cover exactly one column decide how wide that column is, so a wide device spanning several columns
    /// (or alone on its row) never pushes the other columns apart: if it's wider than its columns, it just
    /// extends to the right.
    /// </summary>
    public static PortMapScene BuildGrid(IReadOnlyList<IReadOnlyList<MapCell>> rows, IEnumerable<PortLink> links, IEnumerable<ExternalLink> externals)
    {
        var scene = new PortMapScene();
        rows = rows.Where(r => r.Count > 0).ToList();

        // Flatten to a device list, remembering each device's row, starting column and span.
        var devices = new List<Device>();
        var deviceRow = new List<int>();
        var deviceCol = new List<int>();
        var deviceSpan = new List<int>();        // columns covered (0 = the rest of the row)
        var deviceExplicit = new List<bool>();   // span chosen by the user rather than automatic
        for (var r = 0; r < rows.Count; r++)
        {
            var col = 0;
            for (var c = 0; c < rows[r].Count; c++)
            {
                var cell = rows[r][c];
                var last = c == rows[r].Count - 1;
                var span = cell.Span > 0 ? cell.Span : last ? 0 : 1;
                devices.Add(cell.Device);
                deviceRow.Add(r);
                deviceCol.Add(col);
                deviceSpan.Add(span);
                deviceExplicit.Add(cell.Span > 0);
                col += Math.Max(span, 1);
            }
        }
        var plates = devices.Select(d => FaceplateLayout.Build(d, d.Ports)).ToList();
        double Natural(int i) => Math.Max(MinBodyWidth, plates[i].Width);

        // Columns: as many as any row uses. Only single-column devices set a column's width.
        var columnCount = Math.Max(1, Enumerable.Range(0, devices.Count).Select(i => deviceCol[i] + Math.Max(deviceSpan[i], 1)).DefaultIfEmpty(1).Max());
        var colWidth = Enumerable.Range(0, columnCount)
            .Select(c => Enumerable.Range(0, devices.Count).Where(i => deviceCol[i] == c && deviceSpan[i] == 1).Select(Natural).DefaultIfEmpty(MinBodyWidth).Max())
            .ToArray();
        // One device per row (a classic rack view): every box the same width, as wide as the widest.
        if (rows.All(r => r.Count == 1) && devices.Count > 0) colWidth[0] = Enumerable.Range(0, devices.Count).Select(Natural).Max();

        var colX = new double[columnCount];
        for (var c = 0; c < columnCount; c++)
            colX[c] = c == 0 ? NameColumn : colX[c - 1] + colWidth[c - 1] + ColumnGap + NameColumn;
        double ColumnsRight(int from, int count) => colX[Math.Min(from + count, columnCount) - 1] + colWidth[Math.Min(from + count, columnCount) - 1];

        // Horizontal placement of each device. A device that's wider than its columns pushes only the
        // devices after it in the same row.
        var deviceX = new double[devices.Count];
        var deviceW = new double[devices.Count];
        double prevRight = double.MinValue;
        for (var i = 0; i < devices.Count; i++)
        {
            if (i > 0 && deviceRow[i] != deviceRow[i - 1]) prevRight = double.MinValue;
            var x = Math.Max(colX[deviceCol[i]], prevRight == double.MinValue ? double.MinValue : prevRight + ColumnGap + NameColumn);
            var width = Natural(i);
            if (rows.All(r => r.Count == 1))
                width = colWidth[0];
            else if (deviceExplicit[i] && deviceSpan[i] > 1)
                width = Math.Max(width, ColumnsRight(deviceCol[i], deviceSpan[i]) - x);  // fill the columns it spans
            deviceX[i] = x;
            deviceW[i] = width;
            prevRight = x + width;
        }
        var gutterX = Math.Max(colX[^1] + colWidth[^1], deviceX.Select((x, i) => x + deviceW[i]).DefaultIfEmpty(0).Max()) + GutterPad;

        // Which device each port is on.
        var portDevice = new Dictionary<int, int>();
        for (var i = 0; i < plates.Count; i++)
            foreach (var c in plates[i].Cells) portDevice[c.Port.Id] = i;

        // ---- 1. Decide which channel each cable end uses. Channel k is the gap above row k (k = row count: below the last). ----
        // A port on the top row of a multi-row faceplate must leave upward, one on the bottom row downward;
        // any other port (e.g. a single row of ports) can go either way.
        int? ForcedChannel(int portId)
        {
            var dev = portDevice[portId];
            var row = deviceRow[dev];
            var plate = plates[dev];
            var cell = plate.Find(portId)!;
            var minY = plate.Cells.Min(c => c.Y);
            var maxY = plate.Cells.Max(c => c.Y);
            if (minY != maxY && cell.Y == minY) return row;          // top row of ports: leave upward
            if (minY != maxY && cell.Y == maxY) return row + 1;      // bottom row of ports: leave downward
            return null;
        }

        // A port that can go either way joins the gap the other end uses when that gap touches its own row,
        // so the cable crosses straight over instead of going round the right-hand side. Otherwise it
        // leaves toward the other end.
        int FlexibleChannel(int portId, int otherRow, int? otherChannel)
        {
            var row = deviceRow[portDevice[portId]];
            if (otherChannel is int oc && (oc == row || oc == row + 1)) return oc;
            return otherRow < row ? row : row + 1;
        }

        int ExitChannel(int portId, int towardRow) => ForcedChannel(portId) ?? FlexibleChannel(portId, towardRow, null);

        var routes = new List<Route>();
        var colorIndex = 0;
        foreach (var link in links)
        {
            if (!portDevice.TryGetValue(link.PortAId, out var da) || !portDevice.TryGetValue(link.PortBId, out var db)) continue;
            var color = CableColor(link.Cable?.Color) ?? Palette[colorIndex++ % Palette.Length];
            var forcedA = ForcedChannel(link.PortAId);
            var forcedB = ForcedChannel(link.PortBId);
            var channelA = forcedA ?? FlexibleChannel(link.PortAId, deviceRow[db], forcedB);
            var channelB = forcedB ?? FlexibleChannel(link.PortBId, deviceRow[da], channelA);
            routes.Add(new Route
            {
                Link = link, Color = color, Dashed = !link.IsDirect,
                PortA = link.PortAId, PortB = link.PortBId,
                ChannelA = channelA, ChannelB = channelB
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
        var channelCount = rows.Count + 1;
        var segments = Enumerable.Range(0, channelCount).Select(_ => new List<Segment>()).ToList();
        double PortX(int portId)
        {
            var dev = portDevice[portId];
            return deviceX[dev] + plates[dev].Find(portId)!.CenterX;
        }

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
            if (c < rows.Count)
            {
                // A row is as tall as its tallest device; devices sit at the top of the row.
                var rowHeight = 0.0;
                for (var i = 0; i < devices.Count; i++)
                {
                    if (deviceRow[i] != c) continue;
                    scene.Devices.Add(new PlacedDevice(devices[i], plates[i], deviceX[i], y, deviceW[i], c, deviceCol[i], deviceSpan[i]));
                    rowHeight = Math.Max(rowHeight, plates[i].Height);
                }
                y += rowHeight;
            }
        }
        double TrackY(int channel, int track) =>
            channelTop[channel] + (channelHeight[channel] - (trackCounts[channel] - 1) * TrackGap) / 2 + track * TrackGap;

        (double X, double Y) PortEdge(int portId, int channel)
        {
            var d = scene.Devices[portDevice[portId]];
            var cell = d.Plate.Find(portId)!;
            var up = channel == d.Row;
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
                List<(double X, double Y)> stub = [a, (a.X, ya), (stubX, ya)];
                scene.Lines.Add(new DrawnLink(Rounded(stub), r.Color, r.Dashed, a, (stubX, ya),
                    null, ext, label, (stubX + 6, ya + 4), stub));
                continue;
            }

            var b = PortEdge(r.PortB, r.ChannelB);
            List<(double X, double Y)> pts;
            if (r.ChannelA == r.ChannelB)
                pts = [a, (a.X, ya), (b.X, ya), b];
            else
            {
                var sb = segments[r.ChannelB].First(s => s.Route == r && !s.IsA);
                var yb = TrackY(r.ChannelB, sb.Track);
                var lx = LaneX(r.Lane);
                pts = [a, (a.X, ya), (lx, ya), (lx, yb), (b.X, yb), b];
            }
            scene.Lines.Add(new DrawnLink(Rounded(pts), r.Color, r.Dashed, a, b, r.Link, null, null, default, pts));
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
