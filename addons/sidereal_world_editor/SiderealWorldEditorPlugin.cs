#if TOOLS
using Godot;
using Sidereal.Native.Editor;

[Tool]
public partial class SiderealWorldEditorPlugin : EditorPlugin, ISerializationListener
{
    private WorldObserver? observer;
    private SiderealWorldDock? dock;

    public override void _EnterTree() => Initialize();
    private void Initialize()
    {
        if (observer != null || !Engine.IsEditorHint()) return;
        observer = new WorldObserver();
        dock = new SiderealWorldDock();
        dock.Configure(observer, ObserverProfiles.Load(ProjectSettings.GlobalizePath("res://")));
        AddDock(dock);
        SetProcess(true);
    }
    public override void _Process(double delta)
    {
        // Sole pump owner. No runtime ClientCore, gameplay Main or concurrent SDK tick.
        observer?.Tick();
        if (observer != null)
            while (observer.TryTakeBrowserUrl(out var url)) OS.ShellOpen(url);
        dock?.Refresh();
    }
    private void Shutdown()
    {
        SetProcess(false);
        observer?.Dispose(); observer = null;
        if (dock != null && GodotObject.IsInstanceValid(dock))
        {
            RemoveDock(dock);
            dock.QueueFree();
        }
        dock = null;
    }
    public override void _ExitTree() => Shutdown();
    public void OnBeforeSerialize() => Shutdown();
    public void OnAfterDeserialize()
    {
        if (IsInsideTree()) Initialize(); // Reload always starts disconnected, with no credentials/cache.
    }
}
#endif
