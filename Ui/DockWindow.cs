using Godot;
using System;

namespace Sidereal.Ui;

/// <summary>Window chrome shared by inventory, equipment and settings. Layout is local presentation only.</summary>
public partial class DockWindow : Control
{
    public VBoxContainer Content { get; } = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
    public bool InteractionActive => dragging || edges != 0;
    public event Action? Closed;
    public event Action? LayoutPreferenceChanged;
    public bool HasUserLayout { get; private set; }
    public bool TemporaryLayout { get; private set; }
    public Vector2 PreferredPosition => preferredLoaded ? preferredPosition : InitialPosition;
    public Vector2 PreferredSize => preferredLoaded ? preferredSize : InitialSize;
    [Export] public string LayoutKey { get; set; } = "panel";
    [Export] public string Caption { get; set; } = "PANEL";
    [Export] public Vector2 InitialPosition { get; set; } = new(40, 120);
    [Export] public Vector2 InitialSize { get; set; } = new(420, 320);
    private bool dragging;
    private int edges;
    private Vector2 mouseStart, positionStart, sizeStart;
    private HBoxContainer title = null!;
    private MarginContainer body = null!;
    private ScrollContainer? sidebar;
    private float sidebarWidth;
    private Vector2 preferredPosition, preferredSize;
    private bool preferredLoaded;
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
        var close = UiKit.Button("×", Close);
        close.TooltipText = "Close panel"; close.CustomMinimumSize = new Vector2(32, 30); title.AddChild(close);
        title.GuiInput += TitleInput; AddChild(title);
        body = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) body.AddThemeConstantOverride("margin_" + edge, 14);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto };
        scroll.AddChild(Content); body.AddChild(scroll); AddChild(body);
        Resized += LayoutContents;
        VisibilityChanged+=()=>{if(!IsVisibleInTree())CancelInteraction();};
        SiderealPalette.Current.Changed += PaletteChanged;
        LoadLayout(); LayoutContents();
        GetParent<Control>().Resized += ParentResized;
    }

    private void PaletteChanged() => QueueRedraw();
    private void ParentResized()
    {
        Clamp(); LayoutContents();
        // SiderealUi assigns its new window bounds after resizing this parent.
        CallDeferred(nameof(FitPreferredLayout));
    }
    private void FitPreferredLayout()
    {
        if (!IsInsideTree() || IsQueuedForDeletion()) return;
        if (!InteractionActive && !TemporaryLayout)
        {
            Position = PreferredPosition; Size = PreferredSize;
        }
        Clamp(); LayoutContents();
    }
    private void LayoutContents()
    {
        if (title == null) return;
        title.Position = new Vector2(14, 10); title.Size = new Vector2(Math.Max(0, Size.X - 28), 34);
        body.Position = new Vector2(sidebarWidth, 49); body.Size = new Vector2(Math.Max(1, Size.X - sidebarWidth), Math.Max(0, Size.Y - 57));
        if (sidebar != null) { sidebar.Position = new Vector2(14, 58); sidebar.Size = new Vector2(Math.Max(1, sidebarWidth - 14), Math.Max(1, Size.Y - 73)); }
        QueueRedraw();
    }
    public void SetSidebar(Control content, float width)
    {
        sidebarWidth = width;
        sidebar = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        sidebar.AddChild(content); AddChild(sidebar); LayoutContents();
    }

    public override void _Draw()
    {
        var p = SiderealPalette.Current; if (p == null) return;
        var w = Size.X; var h = Size.Y;
        SciFiFrameStyle.Paint(GetCanvasItem(), new Rect2(Vector2.Zero, Size), new Color(p.Surface, p.Opacity), p.Accent.Darkened(.45f), p.Accent, 12);
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
        // A deliberate drag/resize adopts the displayed geometry as the user's choice.
        TemporaryLayout = false; HasUserLayout = true;
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
        { dragging = false; edges = 0; Clamp(); SaveLayout(adoptDisplayedGeometry: true); GetViewport().SetInputAsHandled(); }
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
        var parent = GetParent<Control>();
        var bounds = parent is SiderealUi ui ? ui.WindowBounds : new Rect2(Vector2.Zero, parent.Size);
        var area = bounds.Size;
        if (area.X < 1 || area.Y < 1) return;
        Size = new Vector2(Mathf.Clamp(Size.X, Math.Min(minimum.X, area.X), area.X), Mathf.Clamp(Size.Y, Math.Min(minimum.Y, area.Y), area.Y));
        Position = new Vector2(Mathf.Clamp(Position.X, bounds.Position.X, Math.Max(bounds.Position.X, bounds.End.X - Size.X)),
            Mathf.Clamp(Position.Y, bounds.Position.Y, Math.Max(bounds.Position.Y, bounds.End.Y - Size.Y)));
    }

    public void PresentTemporaryLayout(Vector2 position, Vector2 size)
    {
        if (HasUserLayout || InteractionActive || !position.IsFinite() || !size.IsFinite()) return;
        TemporaryLayout = true; Position = position; Size = size; Clamp();
    }
    public void RestorePreferredLayout()
    {
        if (!TemporaryLayout || InteractionActive) return;
        TemporaryLayout = false; Position = PreferredPosition; Size = PreferredSize; Clamp();
    }
    public void ResetLayout()
    {
        TemporaryLayout = false; HasUserLayout = false; preferredLoaded = true;
        preferredPosition = InitialPosition; preferredSize = InitialSize;
        Position = InitialPosition; Size = InitialSize; Clamp(); SaveLayout();
    }
    public void CancelInteraction(){if(!InteractionActive)return;dragging=false;edges=0;Clamp();SaveLayout(adoptDisplayedGeometry: true);}
    public void Close(){CancelInteraction();Hide();SaveLayout();Closed?.Invoke();}
    private void LoadLayout()
    {
        var file = new ConfigFile();
        if (file.Load("user://ui-layout.cfg") == Error.Ok && file.HasSection(LayoutKey))
        {
            var pos = file.GetValue(LayoutKey, "position", InitialPosition).AsVector2(); var size = file.GetValue(LayoutKey, "size", InitialSize).AsVector2();
            if (pos.IsFinite() && size.IsFinite())
            {
                Position = pos; Size = size;
                // Older saved profiles have no marker; preserve them as explicit layouts.
                var marker = file.GetValue(LayoutKey, "user_layout", true);
                HasUserLayout = marker.VariantType != Variant.Type.Bool || marker.AsBool();
            }
        }
        preferredPosition = Position; preferredSize = Size; preferredLoaded = true; Clamp();
    }
    private void SaveLayout(bool adoptDisplayedGeometry = false)
    {
        // Viewport clamping is temporary; only a deliberate drag/resize adopts it.
        if (HasUserLayout && adoptDisplayedGeometry) { preferredPosition = Position; preferredSize = Size; }
        var file = new ConfigFile(); file.Load("user://ui-layout.cfg");
        file.SetValue(LayoutKey, "position", PreferredPosition); file.SetValue(LayoutKey, "size", PreferredSize);
        file.SetValue(LayoutKey, "user_layout", HasUserLayout); file.Save("user://ui-layout.cfg");
        LayoutPreferenceChanged?.Invoke();
    }
    public override void _ExitTree()
    {
        SiderealPalette.Current.Changed -= PaletteChanged;
        if (GetParent() is Control parent) parent.Resized -= ParentResized;
    }
}
