using System.Globalization;
using Linktrace.Components.Shared;
using Linktrace.Data.Entities;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Linktrace.Services;

public enum PaperSize { Letter, Tabloid, A4, A3 }

/// <summary>What to put in a port map PDF: the same things the Port map page shows.</summary>
public record PortMapPdfRequest(
    string Title,
    IReadOnlyList<int> DeviceIds,
    IReadOnlyList<int> Columns,
    bool FollowPassThrough,
    bool ShowExternal,
    PaperSize Paper,
    string? GeneratedBy,
    IReadOnlyList<int>? Spans = null);

/// <summary>
/// Renders a port map to a printable PDF: page 1 is the drawing (landscape, scaled to fit the paper),
/// the following pages list every connection. Uses the same layout as the screen, in print-friendly colors.
/// </summary>
public class PortMapPdfService(DeviceService devices, PortMapService portMaps)
{
    static PortMapPdfService() => EmbeddedFonts.Register();

    private const double Margin = 36;      // half an inch
    private const string Font = EmbeddedFonts.Family;

    // Print palette (light theme).
    private static readonly XColor Text = XColor.FromArgb(0x21, 0x21, 0x21);
    private static readonly XColor Muted = XColor.FromArgb(0x75, 0x75, 0x75);
    private static readonly XColor BodyFill = XColor.FromArgb(0xf5, 0xf5, 0xf5);
    private static readonly XColor BodyLine = XColor.FromArgb(0xd0, 0xd0, 0xd0);
    private static readonly XColor PortLine = XColor.FromArgb(0xb0, 0xb0, 0xb0);
    private static readonly XColor Connected = XColor.FromArgb(0x00, 0xa8, 0x4f);
    private static readonly XColor ConnectedLine = XColor.FromArgb(0x00, 0x7a, 0x3a);
    private static readonly XColor Rule = XColor.FromArgb(0xe0, 0xe0, 0xe0);

    public static string FileName(string title)
    {
        var safe = FileNames.Safe(title);
        return $"{(safe.Length == 0 ? "port-map" : safe)}.pdf";
    }

    public async Task<byte[]> RenderAsync(PortMapPdfRequest req)
    {
        // Load the devices, keeping each one's column and span; devices deleted since are skipped.
        var slots = new List<(Device Device, int Column, int Span)>();
        for (var i = 0; i < req.DeviceIds.Count; i++)
            if (await devices.GetAsync(req.DeviceIds[i]) is { } d)
                slots.Add((d, i < req.Columns.Count ? req.Columns[i] : 0, req.Spans is { } sp && i < sp.Count ? sp[i] : 0));
        if (slots.Count == 0) throw new LinktraceException("There are no devices on this map to print.");

        var map = await portMaps.GetMapAsync(slots.Select(s => s.Device.Id).ToList(), req.FollowPassThrough);
        var cells = slots.Select(s => new MapCell(s.Device, Math.Clamp(s.Span, 0, PortMapLayout.MaxSpan))).ToList();
        var rows = PortMapLayout.ToRows(cells, slots.Select(s => s.Column).ToList());
        var scene = PortMapScene.BuildGrid(rows.Select(r => (IReadOnlyList<MapCell>)r).ToList(), map.Links,
            req.ShowExternal ? map.Externals : []);

        var doc = new PdfDocument();
        doc.Info.Title = req.Title;
        doc.Info.Creator = "Linktrace";
        if (req.GeneratedBy is not null) doc.Info.Author = req.GeneratedBy;

        var (pageW, pageH) = PageSize(req.Paper);
        var cableCount = map.Links.Count(l => l.IsDirect);
        var subtitle = string.Join(" · ", new[]
        {
            $"Port map printed {DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}",
            $"{slots.Count} device(s)",
            $"{cableCount} cable(s) between them",
            req.ShowExternal ? $"{map.Externals.Count} to other devices" : null,
            req.GeneratedBy is null ? null : $"by {req.GeneratedBy}"
        }.Where(s => s is not null));

        DrawMapPage(doc, scene, req.Title, subtitle, pageW, pageH);
        DrawConnectionPages(doc, scene, req.Title, pageW, pageH);
        DrawFooters(doc);

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Landscape page sizes in points.</summary>
    private static (double W, double H) PageSize(PaperSize paper) => paper switch
    {
        PaperSize.Tabloid => (1224, 792),
        PaperSize.A4 => (842, 595),
        PaperSize.A3 => (1191, 842),
        _ => (792, 612)
    };

    private static PdfPage NewPage(PdfDocument doc, double w, double h)
    {
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(w);
        page.Height = XUnit.FromPoint(h);
        return page;
    }

    // ---------------- Page 1: the drawing ----------------

    private static void DrawMapPage(PdfDocument doc, PortMapScene scene, string title, string subtitle, double pageW, double pageH)
    {
        var page = NewPage(doc, pageW, pageH);
        using var gfx = XGraphics.FromPdfPage(page);

        gfx.DrawString(title, new XFont(Font, 18, XFontStyleEx.Bold), new XSolidBrush(Text), Margin, Margin + 14);
        gfx.DrawString(subtitle, new XFont(Font, 8.5), new XSolidBrush(Muted), Margin, Margin + 30);
        DrawLegend(gfx, Margin, Margin + 46);

        // Fit the drawing in the remaining area (never enlarge past 100%).
        var top = Margin + 60;
        var availW = pageW - 2 * Margin;
        var availH = pageH - top - Margin - 14;
        var scale = Math.Min(1.0, Math.Min(availW / scene.Width, availH / scene.Height));

        var state = gfx.Save();
        gfx.TranslateTransform(Margin, top);
        gfx.ScaleTransform(scale);
        DrawScene(gfx, scene);
        gfx.Restore(state);

        if (scale < 0.999)
            gfx.DrawString($"Scaled to {scale:P0} to fit. Choose a larger paper size for more detail.",
                new XFont(Font, 7.5), new XSolidBrush(Muted), Margin, pageH - Margin + 2);
    }

    private static void DrawLegend(XGraphics gfx, double x, double y)
    {
        var font = new XFont(Font, 8);
        var muted = new XSolidBrush(Muted);
        void Swatch(XColor fill, XColor line, string label)
        {
            gfx.DrawRoundedRectangle(new XPen(line, 0.8), new XSolidBrush(fill), x, y - 7, 9, 9, 2, 2);
            gfx.DrawString(label, font, muted, x + 13, y + 1);
            x += 13 + gfx.MeasureString(label, font).Width + 14;
        }
        Swatch(Connected, ConnectedLine, "connected");
        Swatch(XColors.White, PortLine, "free");

        void LineSample(bool dashed, string label)
        {
            var pen = new XPen(Muted, 1.4);
            if (dashed) { pen.DashStyle = XDashStyle.Custom; pen.DashPattern = [3, 2]; }
            gfx.DrawLine(pen, x, y - 2.5, x + 18, y - 2.5);
            gfx.DrawString(label, font, muted, x + 22, y + 1);
            x += 22 + gfx.MeasureString(label, font).Width + 14;
        }
        LineSample(false, "cable");
        LineSample(true, "through patch panels");
    }

    /// <summary>Draws the scene in its own coordinates (1 unit = 1 point at 100%).</summary>
    private static void DrawScene(XGraphics gfx, PortMapScene scene)
    {
        var titleFont = new XFont(Font, 10, XFontStyleEx.Bold);
        var kindFont = new XFont(Font, 7.5);
        var blockFont = new XFont(Font, 6.5);
        var labelFont = new XFont(Font, 7.5, XFontStyleEx.Bold);
        var portFontOn = new XFont(Font, 7.5, XFontStyleEx.Bold);
        var portFont = new XFont(Font, 7.5);
        var text = new XSolidBrush(Text);
        var muted = new XSolidBrush(Muted);
        var cell = FaceplateLayout.Cell;

        foreach (var placed in scene.Devices)
        {
            var d = placed.Device;
            var nameX = placed.X - PortMapScene.NameColumn;
            gfx.DrawString(Truncate(d.Name, 22), titleFont, text, nameX, placed.Y + placed.Height / 2 - 2);
            gfx.DrawString(Display.Kind(d.Kind) + (d.RackUnit is int u ? $" · U{u}" : ""), kindFont, muted, nameX, placed.Y + placed.Height / 2 + 11);

            gfx.DrawRoundedRectangle(new XPen(BodyLine, 1), new XSolidBrush(BodyFill), placed.X, placed.Y, placed.Width, placed.Height, 8, 8);

            foreach (var block in placed.Plate.Blocks)
                gfx.DrawString(block.Title, blockFont, muted, placed.X + block.X, placed.Y + block.Y + 10);

            foreach (var label in placed.Plate.Labels)
            {
                var ruleY = placed.Y + label.Y + cell - 3;
                gfx.DrawLine(new XPen(PortLine, 0.6), placed.X + label.X, ruleY, placed.X + label.X + label.Width, ruleY);
                gfx.DrawString(Fit(gfx, label.Text, labelFont, label.Width - 2), labelFont, muted, new XRect(placed.X + label.X, placed.Y + label.Y, label.Width, cell - 3), XStringFormats.Center);
            }

            foreach (var c in placed.Plate.Cells)
            {
                var on = c.Port.CableId != null;
                var rect = new XRect(placed.X + c.X, placed.Y + c.Y, c.Width, cell);
                gfx.DrawRoundedRectangle(new XPen(on ? ConnectedLine : PortLine, 1), new XSolidBrush(on ? Connected : XColors.White), rect, new XSize(7, 7));
                if (PortGlyphs.For(c.Port.MediaType) is { } glyph)
                    DrawGlyph(gfx, glyph, rect.X + (c.Width - cell) / 2, rect.Y, on ? XColors.White : Muted);
                else
                    gfx.DrawString(c.Label, on ? portFontOn : portFont, on ? XBrushes.White : muted, rect, XStringFormats.Center);
            }
        }

        var stubFont = new XFont(Font, 7.5);
        foreach (var line in scene.Lines)
        {
            var color = ParseColor(line.Color);
            var pen = new XPen(color, 2) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
            if (line.Dashed) { pen.DashStyle = XDashStyle.Custom; pen.DashPattern = [3, 2]; }
            gfx.DrawPath(pen, RoundedPath(line.Points));

            var brush = new XSolidBrush(color);
            gfx.DrawEllipse(brush, line.A.X - 3, line.A.Y - 3, 6, 6);
            if (line.External is null)
                gfx.DrawEllipse(brush, line.B.X - 3, line.B.Y - 3, 6, 6);
            else
            {
                gfx.DrawPolygon(brush, [new XPoint(line.B.X, line.B.Y - 4), new XPoint(line.B.X + 6, line.B.Y), new XPoint(line.B.X, line.B.Y + 4)], XFillMode.Winding);
                // The arrowhead is drawn above; Roboto has no "→" glyph, so leave it out of the text.
                gfx.DrawString((line.Label ?? "").TrimStart('→', ' '), stubFont, muted, line.LabelAt.X + 4, line.LabelAt.Y);
            }
        }
    }

    /// <summary>Draws a USB/power connector shape inside a port square at (x, y).</summary>
    private static void DrawGlyph(XGraphics gfx, IReadOnlyList<GlyphShape> glyph, double x, double y, XColor color)
    {
        var pen = new XPen(color, 1.4);
        var brush = new XSolidBrush(color);
        foreach (var s in glyph)
        {
            if (s.Kind == GlyphShapeKind.Circle)
            {
                var r = new XRect(x + s.X - s.R, y + s.Y - s.R, 2 * s.R, 2 * s.R);
                if (s.Filled) gfx.DrawEllipse(brush, r); else gfx.DrawEllipse(pen, r);
            }
            else
            {
                var r = new XRect(x + s.X, y + s.Y, s.W, s.H);
                var corner = new XSize(2 * s.R, 2 * s.R);
                if (s.Filled) gfx.DrawRoundedRectangle(brush, r, corner); else gfx.DrawRoundedRectangle(pen, r, corner);
            }
        }
    }

    /// <summary>Same rounded corners as on screen; quadratic corners become cubic Béziers.</summary>
    private static XGraphicsPath RoundedPath(IReadOnlyList<(double X, double Y)> raw)
    {
        var p = new List<XPoint> { new(raw[0].X, raw[0].Y) };
        foreach (var q in raw.Skip(1))
            if (Math.Abs(q.X - p[^1].X) > 0.01 || Math.Abs(q.Y - p[^1].Y) > 0.01) p.Add(new XPoint(q.X, q.Y));

        var path = new XGraphicsPath();
        var current = p[0];
        for (var i = 1; i < p.Count; i++)
        {
            if (i == p.Count - 1) { path.AddLine(current, p[i]); break; }
            var (prev, cur, next) = (p[i - 1], p[i], p[i + 1]);
            var r = Math.Min(PortMapScene.CornerRadius, Math.Min(Dist(prev, cur), Dist(cur, next)) / 2);
            var inPt = Toward(cur, prev, r);
            var outPt = Toward(cur, next, r);
            path.AddLine(current, inPt);
            path.AddBezier(inPt, Lerp(inPt, cur, 2.0 / 3), Lerp(outPt, cur, 2.0 / 3), outPt);
            current = outPt;
        }
        return path;

        static double Dist(XPoint a, XPoint b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
        static XPoint Toward(XPoint from, XPoint to, double d)
        {
            var len = Dist(from, to);
            return len == 0 ? from : new XPoint(from.X + (to.X - from.X) / len * d, from.Y + (to.Y - from.Y) / len * d);
        }
        static XPoint Lerp(XPoint a, XPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    // ---------------- Following pages: the connection list ----------------

    private static void DrawConnectionPages(PdfDocument doc, PortMapScene scene, string title, double pageW, double pageH)
    {
        if (scene.Lines.Count == 0) return;

        var head = new XFont(Font, 8.5, XFontStyleEx.Bold);
        var body = new XFont(Font, 8.5);
        var text = new XSolidBrush(Text);
        var muted = new XSolidBrush(Muted);
        const double rowH = 15;
        var contentW = pageW - 2 * Margin;
        double[] colX = [Margin, Margin + 18, Margin + 18 + contentW * 0.30, Margin + 18 + contentW * 0.62];

        string PortText(int portId) => scene.FindPort(portId) is { } p && scene.DeviceOfPort(portId) is { } d
            ? Display.Port(d.Device.Name, p.Name, p.Face) : "?";
        string Details(DrawnLink l)
        {
            var cable = l.Link?.Cable ?? l.External?.Cable;
            var count = l.Link?.CableCount ?? l.External!.CableCount;
            if (cable is null) return count > 1 ? $"Through patch panels · {count} cables" : "Cable";
            return string.Join(" · ", new[] { cable.Label ?? "Unlabelled cable", Display.CableKind(cable.CableType), cable.Color,
                cable.LengthMeters is { } m ? $"{m:0.##} m" : null }.Where(s => !string.IsNullOrEmpty(s)));
        }

        var rows = scene.Lines.Select(l => (
            l.Color,
            From: PortText(l.Link?.PortAId ?? l.External!.PortId),
            To: l.Link is { } link ? PortText(link.PortBId) : l.External!.FarText,
            Cable: Details(l))).ToList();

        var index = 0;
        while (index < rows.Count)
        {
            var page = NewPage(doc, pageW, pageH);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{title} · connections", new XFont(Font, 13, XFontStyleEx.Bold), text, Margin, Margin + 10);

            var y = Margin + 32;
            gfx.DrawString("From", head, muted, colX[1], y);
            gfx.DrawString("To", head, muted, colX[2], y);
            gfx.DrawString("Cable", head, muted, colX[3], y);
            y += 6;
            gfx.DrawLine(new XPen(Rule, 0.8), Margin, y, pageW - Margin, y);
            y += rowH - 2;

            while (index < rows.Count && y < pageH - Margin - 10)
            {
                var r = rows[index++];
                gfx.DrawRoundedRectangle(new XSolidBrush(ParseColor(r.Color)), colX[0], y - 8, 9, 9, 2, 2);
                gfx.DrawString(Fit(gfx, r.From, body, colX[2] - colX[1] - 8), body, text, colX[1], y);
                gfx.DrawString(Fit(gfx, r.To, body, colX[3] - colX[2] - 8), body, text, colX[2], y);
                gfx.DrawString(Fit(gfx, r.Cable, body, pageW - Margin - colX[3]), body, text, colX[3], y);
                gfx.DrawLine(new XPen(Rule, 0.4), Margin, y + 5, pageW - Margin, y + 5);
                y += rowH;
            }
        }
    }

    private static void DrawFooters(PdfDocument doc)
    {
        var font = new XFont(Font, 7.5);
        var muted = new XSolidBrush(Muted);
        for (var i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var label = $"Linktrace · page {i + 1} of {doc.PageCount}";
            var w = gfx.MeasureString(label, font).Width;
            gfx.DrawString(label, font, muted, page.Width.Point - Margin - w, page.Height.Point - Margin + 14);
        }
    }

    // ---------------- Helpers ----------------

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Shortens text with an ellipsis until it fits the width.</summary>
    private static string Fit(XGraphics gfx, string s, XFont font, double width)
    {
        if (gfx.MeasureString(s, font).Width <= width) return s;
        while (s.Length > 1 && gfx.MeasureString(s + "…", font).Width > width) s = s[..^1];
        return s + "…";
    }

    /// <summary>"#1e88e5" → color; CSS theme variables fall back to print-friendly equivalents.</summary>
    private static XColor ParseColor(string css)
    {
        if (css.StartsWith('#') && css.Length == 7 &&
            int.TryParse(css[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return XColor.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
        return css.Contains("primary") ? XColor.FromArgb(0x15, 0x65, 0xc0)
             : css.Contains("secondary") && !css.Contains("text") ? XColor.FromArgb(0xef, 0x6c, 0x00)
             : Muted;
    }
}

/// <summary>Serves the bundled Roboto fonts to PDFsharp, so PDFs render the same on any OS or container.</summary>
public sealed class EmbeddedFonts : IFontResolver
{
    public const string Family = "Roboto";
    private static readonly object Gate = new();

    public static void Register()
    {
        lock (Gate)
        {
            if (GlobalFontSettings.FontResolver is not EmbeddedFonts)
                GlobalFontSettings.FontResolver = new EmbeddedFonts();
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "Roboto-Bold" : "Roboto-Regular");

    public byte[]? GetFont(string faceName)
    {
        var assembly = typeof(EmbeddedFonts).Assembly;
        var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($"{faceName}.ttf", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return null;
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
