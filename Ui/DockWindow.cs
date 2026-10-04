using Godot;
using System;

namespace Sidereal.Ui;

/// <summary>Window chrome shared by inventory, equipment and settings. Layout is local presentation only.</summary>
public partial class DockWindow : Control
{
    public VBoxContainer Content { get; } = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    public bool InteractionActive => dragging || edges != 0;
    public event Action? Closed;
    [Export] public string LayoutKey { get; set; } = "panel";
    [Export] public string Caption { get; set; } = "PANEL";
    [Export] public Vector2 InitialPosition { get; set; } = new(40, 120);
    [Export] public Vector2 InitialSize { get; set; } = new(420, 320);
    private bool dragging;
    private int edges;
    private Vector2 mouseStart, positionStart, sizeStart;
    private HBoxContainer title = null!;
    private MarginContainer body = null!;
    private readonly Vector2 minimum = new(280, 180);

    public DockWindow() { MouseFilter = MouseFilterEnum.Stop; }
    public DockWindow(string key, string caption, Vector2 position, Vector2 size)
        : this()
    {
        LayoutKey = key; Caption = caption; InitialPosition = position; InitialSize = size; Name = key;
    }

    public override void _Ready()
    {
        Position = InitialPosition; Size = InitialSize;
        title = new HBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.Drag, TooltipText = "Drag to move. Drag an edge or corner to resize." };
        title.AddChild(UiKit.Heading(Caption, 23)); title.AddChild(UiKit.Spacer());
        var close = UiKit.Button("×", () => { Hide(); SaveLayout(); Closed?.Invoke(); });
        close.TooltipText = "Close panel"; close.CustomMinimumSize = new Vector2(32, 30); title.AddChild(close);
        title.GuiInput += TitleInput; AddChild(title);
        body = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) body.AddThemeConstantOverride("margin_" + edge, 14);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        scroll.AddChild(Content); body.AddChild(scroll); AddChild(body);
        Resized += LayoutContents;
        SiderealPalette.Current.Changed += PaletteChanged;
        LoadLayout(); LayoutContents();
        GetParent<Control>().Resized += ParentResized;
    }

    private void PaletteChanged() => QueueRedraw();
    private void ParentResized() { Clamp(); LayoutContents(); }
    private void LayoutContents()
    {
        if (title == null) return;
        title.Position = new Vector2(14, 10); title.Size = new Vector2(Math.Max(0, Size.X - 28), 34);
        body.Position = new Vector2(0, 49); body.Size = new Vector2(Size.X, Math.Max(0, Size.Y - 57)); QueueRedraw();
    }

    public override void _Draw()
    {
        var p = SiderealPalette.Current; if (p == null) return;
        var w = Size.X; var h = Size.Y; const float c = 11;
        var points = new[] { new Vector2(c, 0), new Vector2(w - c, 0), new Vector2(w, c), new Vector2(w, h - c), new Vector2(w - c, h), new Vector2(c, h), new Vector2(0, h - c), new Vector2(0, c) };
        DrawColoredPolygon(points, new Color(p.Surface, p.Opacity));
        var line = new Vector2[9]; points.CopyTo(line, 0); line[8] = points[0];
        DrawPolyline(line, p.Accent.Darkened(0.22f), 1.3f, true);
        DrawLine(new Vector2(14, 48), new Vector2(w - 14, 48), p.Accent.Darkened(0.55f), 1);
        DrawLine(new Vector2(w - 17, h - 8), new Vector2(w - 8, h - 17), p.Muted, 1.4f, true);
        DrawLine(new Vector2(w - 23, h - 8), new Vector2(w - 8, h - 23), p.Muted.Darkened(0.3f), 1, true);
    }

    private void TitleInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        { BeginDrag(0); title.AcceptEvent(); }
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseMotion motion && !InteractionActive)
        {
            var side = EdgeAt(motion.Position);
            MouseDefaultCursorShape = side switch { 5 or 10 => CursorShape.Fdiagsize, 6 or 9 => CursorShape.Bdiagsize, 1 or 2 => CursorShape.Hsize, 4 or 8 => CursorShape.Vsize, _ => CursorShape.Arrow };
        }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
        {
            BringToFront();
            var side = EdgeAt(mouse.Position);
            if (side != 0) { BeginDrag(side); AcceptEvent(); }
        }
    }

    private int EdgeAt(Vector2 p) => p.X > Size.X - 26 && p.Y > Size.Y - 26 ? 10 :
        (p.X < 8 ? 1 : p.X > Size.X - 8 ? 2 : 0) | (p.Y < 8 ? 4 : p.Y > Size.Y - 8 ? 8 : 0);
    private void BeginDrag(int side)
    {
        BringToFront(); dragging = side == 0; edges = side;
        mouseStart = GetParent<Control>().GetLocalMousePosition(); positionStart = Position; sizeStart = Size;
    }

    public void BringToFront()
    {
        MoveToFront();
        // Explicit canvas ordering keeps nested custom-drawn item grids below the active frame.
        var index = 10;
        foreach (var child in GetParent().GetChildren()) if (child is DockWindow window) window.ZIndex = index++;
    }

    public override void _Input(InputEvent input)
    {
        if (!InteractionActive) return;
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        { dragging = false; edges = 0; Clamp(); SaveLayout(); GetViewport().SetInputAsHandled(); }
        else if (input is InputEventMouseMotion)
        {
            var delta = GetParent<Control>().GetLocalMousePosition() - mouseStart;
            if (dragging) Position = positionStart + delta;
            else
            {
                var pos = positionStart; var size = sizeStart;
                if ((edges & 1) != 0) { var change = Math.Min(delta.X, sizeStart.X - minimum.X); pos.X += change; size.X -= change; }
                if ((edges & 2) != 0) size.X += delta.X;
                if ((edges & 4) != 0) { var change = Math.Min(delta.Y, sizeStart.Y - minimum.Y); pos.Y += change; size.Y -= change; }
                if ((edges & 8) != 0) size.Y += delta.Y;
                Position = pos; Size = size;
            }
            Clamp(); GetViewport().SetInputAsHandled();
        }
    }

    public void Clamp()
    {
        var area = GetParent<Control>().Size;
        if (area.X < 1 || area.Y < 1) return;
        Size = new Vector2(Mathf.Clamp(Size.X, Math.Min(minimum.X, area.X), area.X), Mathf.Clamp(Size.Y, Math.Min(minimum.Y, area.Y), area.Y));
        Position = new Vector2(Mathf.Clamp(Position.X, 0, Math.Max(0, area.X - Size.X)), Mathf.Clamp(Position.Y, 0, Math.Max(0, area.Y - Size.Y)));
    }

    public void ResetLayout() { Position = InitialPosition; Size = InitialSize; Clamp(); SaveLayout(); }
    private void LoadLayout()
    {
        var file = new ConfigFile(); if (file.Load("user://ui-layout.cfg") != Error.Ok) { Clamp(); return; }
        var pos = file.GetValue(LayoutKey, "position", InitialPosition).AsVector2(); var size = file.GetValue(LayoutKey, "size", InitialSize).AsVector2();
        if (pos.IsFinite() && size.IsFinite()) { Position = pos; Size = size; } Clamp();
    }
    private void SaveLayout()
    {
        var file = new ConfigFile(); file.Load("user://ui-layout.cfg");
        file.SetValue(LayoutKey, "position", Position); file.SetValue(LayoutKey, "size", Size); file.Save("user://ui-layout.cfg");
    }
    public override void _ExitTree()
    {
        SiderealPalette.Current.Changed -= PaletteChanged;
        if (GetParent() is Control parent) parent.Resized -= ParentResized;
    }
}
