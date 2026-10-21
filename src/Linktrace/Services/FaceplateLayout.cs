using System.Text.RegularExpressions;
using Linktrace.Data.Entities;

namespace Linktrace.Services;

/// <summary>A port's position in the faceplate drawing (top-left corner, in SVG units).</summary>
public record PortCell(Port Port, double X, double Y, string Label)
{
    public double CenterX => X + FaceplateLayout.Cell / 2;
    public double Top => Y;
    public double Bottom => Y + FaceplateLayout.Cell;
}

/// <summary>A small heading above a block of ports, e.g. "Gi1/0/" or "Rear".</summary>
public record PortBlock(string Title, double X, double Y, double Width, double Height);

/// <summary>A label from a hand-built grid, drawn centered in the cells it spans.</summary>
public record GridLabel(string Text, double X, double Y, double Width);

public class Faceplate
{
    public List<PortCell> Cells { get; } = [];
    public List<PortBlock> Blocks { get; } = [];
    public List<GridLabel> Labels { get; } = [];
    public double Width { get; set; }
    public double Height { get; set; }

    public PortCell? Find(int portId) => Cells.FirstOrDefault(c => c.Port.Id == portId);
}

/// <summary>
/// Lays out a device's ports for drawing. If the device has a hand-built grid template, ports go
/// exactly where it says (any ports it doesn't mention are shown underneath). Otherwise ports are
/// grouped by name prefix and side ("Gi1/0/1-48" and "Te1/1/1-4" become two blocks) and laid out
/// like typical hardware; rear ports of patch panels go in a second strip.
/// </summary>
public static partial class FaceplateLayout
{
    public const double Cell = 28;
    public const double Gap = 5;
    public const double Pitch = Cell + Gap;
    public const double BlockGap = 22;
    public const double LabelHeight = 16;
    public const double StripGap = 14;
    public const double Padding = 12;

    private static readonly DeviceKind[] SwitchLike = [DeviceKind.Switch, DeviceKind.Router, DeviceKind.Firewall];

    public static Faceplate Build(Device device, IEnumerable<Port> ports) =>
        device.FaceplateTemplate is { } template ? BuildFromTemplate(device, template, ports) : BuildAuto(device, ports);

    // ---------- Hand-built grid ----------

    private static Faceplate BuildFromTemplate(Device device, FaceplateTemplate t, IEnumerable<Port> ports)
    {
        var plate = new Faceplate();
        var all = ports.ToList();
        var byKey = all.GroupBy(p => Key(p.Face, p.Name)).ToDictionary(g => g.Key, g => g.First());
        var placed = new HashSet<int>();

        foreach (var pl in t.Placements)
        {
            if (pl.Col >= t.Columns || pl.Row >= t.Rows) continue;
            if (!byKey.TryGetValue(Key(pl.Face, pl.PortName), out var port) || !placed.Add(port.Id)) continue;
            plate.Cells.Add(new PortCell(port, Padding + pl.Col * Pitch, Padding + pl.Row * Pitch, ShortLabel(port.Name)));
        }
        foreach (var l in t.Labels.Where(l => l.Col < t.Columns && l.Row < t.Rows))
        {
            var span = Math.Clamp(l.Span, 1, t.Columns - l.Col);
            plate.Labels.Add(new GridLabel(l.Text, Padding + l.Col * Pitch, Padding + l.Row * Pitch, span * Pitch - Gap));
        }

        plate.Width = 2 * Padding + t.Columns * Pitch - Gap;
        plate.Height = 2 * Padding + t.Rows * Pitch - Gap;

        // Ports the template doesn't place (renamed, or added later) are still drawn, underneath.
        var rest = all.Where(p => !placed.Contains(p.Id)).ToList();
        if (rest.Count > 0)
        {
            var extra = BuildAuto(device, rest);
            var dy = plate.Height - Padding + StripGap;
            foreach (var c in extra.Cells) plate.Cells.Add(c with { Y = c.Y + dy });
            foreach (var b in extra.Blocks) plate.Blocks.Add(b with { Title = $"Not placed · {b.Title}", Y = b.Y + dy });
            plate.Width = Math.Max(plate.Width, extra.Width);
            plate.Height = dy + extra.Height;
        }
        return plate;
    }

    public static string Key(PortFace face, string name) => $"{face}|{name.Trim().ToLowerInvariant()}";

    // ---------- Automatic ----------

    /// <summary>A block of ports that share a prefix and side, with its grid shape.</summary>
    private sealed record Group(string Title, List<Port> Ports, int Rows, int Cols, bool OddEven)
    {
        public (int Row, int Col) Position(int i) => OddEven ? (i % Rows, i / Rows) : (i / Cols, i % Cols);
    }

    /// <summary>Strips (front/standard, then rear), each a list of groups in port order.</summary>
    private static List<List<Group>> Groups(Device device, List<Port> all)
    {
        var strips = new[]
        {
            all.Where(p => p.Face != PortFace.Rear).ToList(),
            all.Where(p => p.Face == PortFace.Rear).ToList()
        }.Where(s => s.Count > 0);

        var oddEven = Numbering(device) == PortNumbering.OddTopEvenBottom;
        return strips.Select(strip => strip
            .GroupBy(p => (p.Face, Prefix: Split(p.Name).Prefix))
            .OrderBy(g => g.Min(p => p.Position))
            .Select(g =>
            {
                var sorted = g.OrderBy(p => Split(p.Name).Number ?? int.MaxValue).ThenBy(p => p.Position).ToList();
                var rows = Rows(device, sorted.Count);
                var cols = (int)Math.Ceiling(sorted.Count / (double)rows);
                return new Group(Title(g.Key.Face, g.Key.Prefix), sorted, rows, cols, oddEven);
            }).ToList()).ToList();
    }

    private static Faceplate BuildAuto(Device device, IEnumerable<Port> ports)
    {
        var plate = new Faceplate();
        var strips = Groups(device, ports.ToList());

        var y = Padding;
        foreach (var strip in strips)
        {
            var x = Padding;
            var stripHeight = 0.0;
            foreach (var g in strip)
            {
                for (var i = 0; i < g.Ports.Count; i++)
                {
                    var (row, col) = g.Position(i);
                    plate.Cells.Add(new PortCell(g.Ports[i], x + col * Pitch, y + LabelHeight + row * Pitch, ShortLabel(g.Ports[i].Name)));
                }
                var width = g.Cols * Pitch - Gap;
                var height = LabelHeight + g.Rows * Pitch - Gap;
                plate.Blocks.Add(new PortBlock(g.Title, x, y, width, height));
                x += width + BlockGap;
                stripHeight = Math.Max(stripHeight, height);
            }
            plate.Width = Math.Max(plate.Width, x - BlockGap + Padding);
            y += stripHeight + StripGap;
        }

        plate.Height = strips.Count == 0 ? 0 : y - StripGap + Padding;
        return plate;
    }

    /// <summary>
    /// Turns the automatic layout into a grid template: a label row above each strip,
    /// one empty column between blocks. A good starting point for hand editing.
    /// </summary>
    public static FaceplateTemplate AutoTemplate(Device device, IEnumerable<Port> ports)
    {
        var t = new FaceplateTemplate();
        var strips = Groups(device, ports.ToList());
        var row = 0;
        var maxCol = 1;
        foreach (var strip in strips)
        {
            var col = 0;
            var stripRows = 0;
            foreach (var g in strip)
            {
                t.Labels.Add(new FaceplateLabel { Text = g.Title, Col = col, Row = row, Span = Math.Max(1, g.Cols) });
                for (var i = 0; i < g.Ports.Count; i++)
                {
                    var (r, c) = g.Position(i);
                    t.Placements.Add(new FaceplatePlacement { PortName = g.Ports[i].Name, Face = g.Ports[i].Face, Col = col + c, Row = row + 1 + r });
                }
                col += g.Cols + 1;
                stripRows = Math.Max(stripRows, g.Rows);
            }
            maxCol = Math.Max(maxCol, col - 1);
            row += 1 + stripRows;
        }
        t.Columns = Math.Clamp(maxCol, 1, FaceplateGrid.MaxColumns);
        t.Rows = Math.Clamp(Math.Max(row, 1), 1, FaceplateGrid.MaxRows);
        // Drop anything that wouldn't fit in the maximum grid.
        t.Placements.RemoveAll(p => p.Col >= t.Columns || p.Row >= t.Rows);
        t.Labels.RemoveAll(l => l.Col >= t.Columns || l.Row >= t.Rows);
        return t;
    }

    public static PortNumbering Numbering(Device d) => d.FaceplateNumbering != PortNumbering.Auto
        ? d.FaceplateNumbering
        : SwitchLike.Contains(d.Kind) ? PortNumbering.OddTopEvenBottom : PortNumbering.RowByRow;

    /// <summary>Rows for a block of <paramref name="count"/> ports.</summary>
    public static int Rows(Device d, int count)
    {
        if (count <= 1) return 1;
        if (d.FaceplateRows > 0) return Math.Min(d.FaceplateRows, count);
        // Switches: two rows like real hardware (24 ports → 2×12, 48 → 2×24, 4 SFP → 2×2).
        if (SwitchLike.Contains(d.Kind) && count >= 4) return 2;
        // Panels and others: rows of up to 24.
        return (int)Math.Ceiling(count / 24.0);
    }

    /// <summary>"Gi1/0/12" → ("Gi1/0/", 12); "eth0" → ("eth", 0); "uplink" → ("uplink", null).</summary>
    public static (string Prefix, int? Number) Split(string name)
    {
        var m = TrailingNumber().Match(name);
        return m.Success && int.TryParse(m.Groups[2].Value, out var n) ? (m.Groups[1].Value, n) : (name, null);
    }

    public static string ShortLabel(string name)
    {
        var (_, number) = Split(name);
        if (number is int n) return n.ToString();
        return name.Length <= 4 ? name : name[..4];
    }

    public static string Title(PortFace face, string prefix)
    {
        var side = face switch { PortFace.Front => "Front", PortFace.Rear => "Rear", _ => "" };
        var p = prefix.Trim();
        return (side, p) switch
        {
            ("", "") => "Ports",
            ("", _) => p,
            (_, "") => side,
            _ => $"{side} · {p}"
        };
    }

    [GeneratedRegex(@"^(.*?)(\d+)$")]
    private static partial Regex TrailingNumber();
}
