#if TOOLS
using Godot;
using System;
using Sidereal.Native.Editor;

[Tool]
public partial class SiderealWorldDock : EditorDock
{
    private WorldObserver? observer;
    private ObserverProfiles? profiles;
    private OptionButton environments = null!;
    private Label boundary = null!, endpoint = null!, database = null!, authentication = null!;
    private Label status = null!, count = null!, message = null!, notice = null!;
    private Button connect = null!, disconnect = null!;
    private ObserverSnapshot? displayed;
    private int selected = -1;

    public void Configure(WorldObserver service, ObserverProfiles available)
    {
        observer = service; profiles = available;
        Name = "SiderealWorld"; Title = "Sidereal World";
        DefaultSlot = DockSlot.RightUl;
        AvailableLayouts = DockLayout.Vertical | DockLayout.Floating;
        CustomMinimumSize = new Vector2(260, 180);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 9); scroll.AddChild(body);
        Label Text(string text, bool wrap = true)
        {
            var label = new Label { Text = text, AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            body.AddChild(label); return label;
        }
        boundary = Text("READ ONLY");
        boundary.AddThemeColorOverride("font_color", new Color("e7be76"));
        Text("Environment");
        environments = new OptionButton { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddChild(environments);
        foreach (var profile in available.Items) environments.AddItem(profile.Name);
        environments.ItemSelected += _ => SelectEnvironment();
        endpoint = Text(""); database = Text(""); authentication = Text("");
        var actions = new HBoxContainer(); body.AddChild(actions);
        connect = new Button { Text = "Connect", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        disconnect = new Button { Text = "Disconnect", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        actions.AddChild(connect); actions.AddChild(disconnect);
        connect.Pressed += ConnectSelected;
        disconnect.Pressed += () => { observer?.Disconnect(); Refresh(); };
        body.AddChild(new HSeparator());
        status = Text("Disconnected"); count = Text("Cached own characters: 0");
        Text("Subscription: own_characters\nActor-filtered count only");
        message = Text("");
        body.AddChild(new HSeparator());
        Text("No character is created or controlled. World preview is planned.");
        Text("An applied subscription can contain zero rows. Provider views require an existing admitted game session for the same account. This observer does not admit a gameplay session.");
        notice = Text(available.Notice); notice.Visible = available.Notice.Length > 0;
        SelectEnvironment(); Refresh();
    }
    private void SelectEnvironment()
    {
        if (profiles == null || profiles.Items.Count == 0)
        {
            selected = -1;
            endpoint.Text = "No valid project profile available.";
            database.Text = ""; authentication.Text = "";
            return;
        }
        selected = environments.Selected;
        if (selected < 0 || selected >= profiles.Items.Count) { selected = 0; environments.Select(0); }
        var profile = profiles.Items[selected];
        boundary.Text = profile.IsProduction ? "LIVE / PRODUCTION — READ ONLY" : "ISOLATED TEST FIXTURE — READ ONLY";
        endpoint.Text = "Server: " + profile.Settings.GameOrigin;
        database.Text = "Database: " + profile.Settings.Database;
        authentication.Text = "Authentication: " + profile.Authentication;
        displayed = null;
    }
    private void ConnectSelected()
    {
        if (profiles == null || selected < 0 || selected >= profiles.Items.Count || observer == null) return;
        observer.Connect(profiles.Items[selected]); Refresh();
    }
    public void Refresh()
    {
        if (observer == null || status == null) return;
        var snapshot = observer.Snapshot;
        if (snapshot == displayed) return;
        displayed = snapshot;
        status.Text = "Status: " + snapshot.Status;
        count.Text = "Cached own characters: " + snapshot.RowCount;
        message.Text = snapshot.Message;
        connect.Disabled = snapshot.Busy || selected < 0;
        disconnect.Disabled = !snapshot.Busy;
        environments.Disabled = snapshot.Busy || selected < 0;
    }
}
#endif
