using Godot;
using System;

namespace Sidereal.Ui;

/// <summary>Resolution-independent clipped frame shared by native controls and painted widgets.</summary>
public partial class SciFiFrameStyle : StyleBox
{
    public Color Fill { get; set; }
    public Color Border { get; set; }
    public Color Accent { get; set; }
    public float Corner { get; set; } = 10;
    public float BorderWidth { get; set; } = 1;
    public bool Rails { get; set; } = true;

    public SciFiFrameStyle() { }
    public SciFiFrameStyle(Color fill, Color border, Color accent, int padding, float corner = 10, bool rails = true)
    {
        Fill = fill; Border = border; Accent = accent; Corner = corner; Rails = rails;
        ContentMarginLeft = ContentMarginRight = padding;
        ContentMarginTop = ContentMarginBottom = padding;
    }

    public override void _Draw(Rid toCanvasItem, Rect2 rect)
        => Paint(toCanvasItem, rect, Fill, Border, Accent, Corner, Rails, BorderWidth);

    public static void Paint(Rid canvas, Rect2 rect, Color fill, Color border, Color accent,
        float corner = 10, bool rails = true, float borderWidth = 1)
    {
        if (rect.Size.X <= 2 || rect.Size.Y <= 2) return;
        var points = Outline(rect.Grow(-1), corner);
        if (fill.A > 0)
        {
            var colors = new Color[points.Length];
            // A subtle top-to-bottom shade gives the interface a readable surface above game art.
            for (var i = 0; i < points.Length; i++)
            {
                var blend = (points[i].Y - rect.Position.Y) / Math.Max(1, rect.Size.Y);
                colors[i] = fill.Lerp(fill.Darkened(.28f) with { A = fill.A }, blend);
            }
            RenderingServer.CanvasItemAddPolygon(canvas, points, colors);
        }
        var loop = new Vector2[points.Length + 1]; points.CopyTo(loop, 0); loop[^1] = points[0];
        RenderingServer.CanvasItemAddPolyline(canvas, loop, new[] { border }, borderWidth, true);
        if (!rails) return;
        var length = Math.Min(58, rect.Size.X * .22f);
        var left = new[] { points[7] + new Vector2(0, Math.Min(17, rect.Size.Y * .2f)), points[7], points[0], points[0] + new Vector2(length, 0) };
        var right = new[] { points[3] - new Vector2(0, Math.Min(17, rect.Size.Y * .2f)), points[3], points[4], points[4] - new Vector2(length, 0) };
        foreach (var rail in new[] { left, right })
        {
            RenderingServer.CanvasItemAddPolyline(canvas, rail, new[] { accent with { A = accent.A * .09f } }, 8, true);
            RenderingServer.CanvasItemAddPolyline(canvas, rail, new[] { accent with { A = accent.A * .22f } }, 4, true);
            RenderingServer.CanvasItemAddPolyline(canvas, rail, new[] { accent }, 1.8f, true);
        }
        // The second quiet line reads as a manufactured frame, rather than a glowing rectangle.
        RenderingServer.CanvasItemAddLine(canvas, points[0] + new Vector2(length + 8, 4), points[1] + new Vector2(-5, 4), border with { A = border.A * .5f }, 1, true);
    }

    private static Vector2[] Outline(Rect2 rect, float corner)
    {
        var c = Mathf.Clamp(corner, 0, Math.Min(rect.Size.X, rect.Size.Y) * .24f);
        var p = rect.Position; var e = rect.End;
        return new[] { new Vector2(p.X + c, p.Y), new Vector2(e.X - c, p.Y),
            new Vector2(e.X, p.Y + c), new Vector2(e.X, e.Y - c),
            new Vector2(e.X - c, e.Y), new Vector2(p.X + c, e.Y),
            new Vector2(p.X, e.Y - c), new Vector2(p.X, p.Y + c) };
    }
}
