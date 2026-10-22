using Linktrace.Data.Entities;

namespace Linktrace.Services;

public enum GlyphShapeKind { Rect, Circle }

/// <summary>
/// One piece of a connector drawing, in the coordinates of a port square (0–28 on each side).
/// Rect: X/Y are the top-left corner, W/H the size, R the corner radius. Circle: X/Y the centre, R the radius.
/// </summary>
public record GlyphShape(GlyphShapeKind Kind, double X, double Y, double W, double H, double R, bool Filled);

/// <summary>
/// Simple drawings for connectors that aren't network ports (USB, power), shown inside the port square
/// instead of the port number. Used by both the on-screen faceplate and the PDF so they match.
/// </summary>
public static class PortGlyphs
{
    private static GlyphShape Rect(double x, double y, double w, double h, double r, bool filled) => new(GlyphShapeKind.Rect, x, y, w, h, r, filled);
    private static GlyphShape Circle(double cx, double cy, double r, bool filled) => new(GlyphShapeKind.Circle, cx, cy, 0, 0, r, filled);

    // IEC C13/C14 body: a rectangle with the top corners cut, approximated by a rounded rectangle.
    private static readonly GlyphShape[] C13 =
    [
        Rect(6.5, 8.5, 15, 11, 2.5, false),
        Rect(9, 12.5, 2, 3.5, 0.4, true), Rect(13, 10.6, 2, 3.5, 0.4, true), Rect(17, 12.5, 2, 3.5, 0.4, true)
    ];
    private static readonly GlyphShape[] C19 =
    [
        Rect(6, 8.5, 16, 11, 1.5, false),
        Rect(12, 10.6, 4, 1.8, 0.4, true), Rect(8.5, 15.2, 4, 1.8, 0.4, true), Rect(15.5, 15.2, 4, 1.8, 0.4, true)
    ];

    public static IReadOnlyList<GlyphShape>? For(MediaType media) => media switch
    {
        MediaType.UsbC => [Rect(6, 10.5, 16, 7, 3.5, false)],
        MediaType.UsbA => [Rect(7, 10, 14, 8, 1, false), Rect(9, 12, 10, 2.5, 0.5, true)],
        // Two flat blades (neutral a little longer) and a round ground hole.
        MediaType.PowerNema515 => [Rect(9.5, 8, 2.2, 6.5, 0.6, true), Rect(16.3, 7.5, 2.2, 7.5, 0.6, true), Circle(14, 19.5, 2.3, true)],
        MediaType.PowerC13 => C13,
        MediaType.PowerC19 => C19,
        // Inlets: the same face with a heavier outline (a double border).
        MediaType.PowerC14 => [.. C13, Rect(5, 7, 18, 14, 3, false)],
        MediaType.PowerC20 => [.. C19, Rect(4.5, 7, 19, 14, 2.5, false)],
        MediaType.PowerDc => [Circle(14, 14, 5.5, false), Circle(14, 14, 1.6, true)],
        _ => null
    };
}
