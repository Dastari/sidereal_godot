using System;
using Godot;

namespace Sidereal.Ui;

/// <summary>Shared source-style beveled item well, independent of inventory rules.</summary>
public static class ItemFrameStyle
{
    public static void Paint(Control target, Rect2 rect, string rarity = "common", bool selected = false,
        bool hovered = false, bool focused = false, bool disabled = false, bool empty = false, float opacity = 1)
    {
        if (rect.Size.X < 10 || rect.Size.Y < 10) return;
        var p = SiderealPalette.Current; var edge = p.Rarity(rarity);
        var active = !disabled && (selected || hovered || focused);
        var alpha = opacity * (disabled ? .46f : 1) * p.Opacity;
        var unit = Math.Clamp(Math.Min(rect.Size.X, rect.Size.Y) / 50, .8f, 1.35f);
        var cut = Math.Clamp(Math.Min(rect.Size.X, rect.Size.Y) * .11f, 4, 9);
        var outer = rect.Grow(-.5f);
        var points = Chamfer(outer, cut);
        var colors = points.SelectColors((new Color("142840") with { A = alpha }), (new Color("030c1a") with { A = alpha }), outer);
        target.DrawPolygon(points, colors);
        Loop(target, points, (empty ? new Color("274f76") : edge.Darkened(.65f)) with { A = alpha }, 1);
        var rim = Chamfer(outer.Grow(-1.2f * unit), Math.Max(2, cut - .6f * unit));
        if (!empty)
        {
            Loop(target, rim, edge with { A = alpha * .06f }, active ? 6 : 3);
            Loop(target, rim, edge with { A = alpha * .14f }, active ? 3 : 2);
        }
        Loop(target, rim, (empty ? new Color("35638e") : edge) with { A = alpha }, active ? 1.55f : 1.05f);
        var inset = 3 * unit;
        var well = Chamfer(outer.Grow(-inset), Math.Max(2, cut - inset * .58f));
        target.DrawColoredPolygon(well, (new Color("020c19") with { A = alpha }));
        Loop(target, well, edge.Darkened(.62f) with { A = alpha * (empty ? .42f : 1) }, 1);
        if (empty)
        {
            var d = Math.Min(rect.Size.X, rect.Size.Y) * .25f; var middle = rect.GetCenter();
            target.DrawLine(middle - new Vector2(d, d), middle + new Vector2(d, d), (new Color("33638e") with { A = alpha * .34f }), .8f, true);
            target.DrawLine(middle - new Vector2(d, -d), middle + new Vector2(d, -d), (new Color("33638e") with { A = alpha * .34f }), .8f, true);
        }
        else
        {
            Loop(target, Chamfer(outer.Grow(-(inset + 1.55f * unit)), Math.Max(2, cut - inset * .45f)), edge.Darkened(.48f) with { A = alpha }, .8f);
            var shoulder = Math.Min(12 * unit, Math.Min(rect.Size.X, rect.Size.Y) * .2f);
            target.DrawPolyline(new[] { rim[^1] + new Vector2(0, shoulder), rim[^1], rim[0], rim[0] + new Vector2(shoulder, 0) },
                (active ? Colors.White : edge.Lightened(.65f)) with { A = alpha }, active ? 1.7f : 1.35f, true);
            target.DrawPolyline(new[] { rim[3] - new Vector2(0, shoulder), rim[3], rim[4], rim[4] - new Vector2(shoulder, 0) },
                (active ? Colors.White : edge.Lightened(.65f)) with { A = alpha }, active ? 1.7f : 1.35f, true);
        }
        if (selected && !disabled)
            target.DrawLine(new Vector2(rect.GetCenter().X - Math.Min(8, rect.Size.X * .15f), rect.End.Y - 2.4f * unit),
                new Vector2(rect.GetCenter().X + Math.Min(8, rect.Size.X * .15f), rect.End.Y - 2.4f * unit), edge.Lightened(.65f), 1.5f * unit, true);
        if (focused && !disabled) Loop(target, Chamfer(rect.Grow(1), cut), Colors.White with { A = .9f }, 1);
    }
    private static Vector2[] Chamfer(Rect2 r, float cut)
    {
        var c = Math.Min(cut, Math.Min(r.Size.X, r.Size.Y) * .4f); var p = r.Position; var e = r.End;
        return new[] { new Vector2(p.X+c,p.Y), new Vector2(e.X-c,p.Y), new Vector2(e.X,p.Y+c), new Vector2(e.X,e.Y-c),
            new Vector2(e.X-c,e.Y),new Vector2(p.X+c,e.Y),new Vector2(p.X,e.Y-c),new Vector2(p.X,p.Y+c) };
    }
    private static void Loop(Control target, Vector2[] points, Color color, float width)
    { var line = new Vector2[points.Length+1]; points.CopyTo(line,0); line[^1]=points[0]; target.DrawPolyline(line,color,width,true); }
    private static Color[] SelectColors(this Vector2[] points, Color top, Color bottom, Rect2 r)
    { var colors=new Color[points.Length]; for(var i=0;i<points.Length;i++) colors[i]=top.Lerp(bottom,(points[i].Y-r.Position.Y)/Math.Max(1,r.Size.Y)); return colors; }
}
