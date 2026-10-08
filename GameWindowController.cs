using System;
using Godot;
using Sidereal.Native;

/// <summary>Desktop presentation only. The responsive viewport remains in physical pixels.</summary>
public sealed class GameWindowController : IDisposable
{
    private readonly Window window;
    private readonly Action cancelLocalInput;
    private Vector2I windowedSize, windowedPosition;
    private Vector2I frameOffset, frameExtra;
    private Window.ModeEnum returnMode = Window.ModeEnum.Windowed;
    private bool preferenceFullscreen;

    public bool Fullscreen => window.Mode is Window.ModeEnum.Fullscreen or Window.ModeEnum.ExclusiveFullscreen;
    public bool Supported => DisplayServer.GetName() != "headless" && !window.IsEmbedded();

    public GameWindowController(Window window, Action cancelLocalInput)
    {
        this.window = window;
        this.cancelLocalInput = cancelLocalInput;
        windowedSize = window.Size;
        windowedPosition = window.Position;
        if (Supported)
        {
            window.Unresizable = false;
            window.MinSize = new Vector2I(640, 480);
        }
        RememberWindowed();
        window.SizeChanged += OnResize;
        NativePreferences.Current.Changed += ApplyPreferences;
        preferenceFullscreen = NativePreferences.Current.Snapshot.Fullscreen;
        ApplyFullscreen(preferenceFullscreen);
    }

    public void Toggle()
    {
        if (Supported)
            NativePreferences.Current.Set(NativePreferences.Current.Snapshot with { Fullscreen = !Fullscreen });
    }

    public void Observe()
    {
        if (!Supported) return;
        RememberWindowed();
        // Keep the settings toggle truthful if the window manager changes the mode.
        if (Fullscreen != preferenceFullscreen)
            NativePreferences.Current.Set(NativePreferences.Current.Snapshot with { Fullscreen = Fullscreen });
    }

    private void ApplyPreferences(NativePreferencesSnapshot next)
    {
        if (next.Fullscreen == preferenceFullscreen) return;
        preferenceFullscreen = next.Fullscreen;
        ApplyFullscreen(next.Fullscreen);
    }

    private void ApplyFullscreen(bool enabled)
    {
        if (!Supported || Fullscreen == enabled) return;
        cancelLocalInput();
        if (enabled)
        {
            RememberWindowed();
            window.Mode = Window.ModeEnum.Fullscreen;
        }
        else
        {
            window.Mode = Window.ModeEnum.Windowed;
            var area = DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
            if (area.Size.X > 0 && area.Size.Y > 0)
            {
                var available = new Vector2I(Math.Max(1, area.Size.X - frameExtra.X), Math.Max(1, area.Size.Y - frameExtra.Y));
                var size = new Vector2I(
                    Math.Clamp(windowedSize.X, Math.Min(640, available.X), available.X),
                    Math.Clamp(windowedSize.Y, Math.Min(480, available.Y), available.Y));
                window.Size = size;
                window.Position = new Vector2I(
                    Math.Clamp(windowedPosition.X, area.Position.X - frameOffset.X, area.End.X - frameOffset.X - size.X - frameExtra.X),
                    Math.Clamp(windowedPosition.Y, area.Position.Y - frameOffset.Y, area.End.Y - frameOffset.Y - size.Y - frameExtra.Y));
            }
            else { window.Size = windowedSize; window.MoveToCenter(); }
            if (returnMode == Window.ModeEnum.Maximized) window.Mode = Window.ModeEnum.Maximized;
        }
    }

    private void RememberWindowed()
    {
        if (window.Mode == Window.ModeEnum.Windowed)
        {
            windowedSize = window.Size;
            windowedPosition = window.Position;
            frameOffset = window.GetPositionWithDecorations() - window.Position;
            var extra = window.GetSizeWithDecorations() - window.Size;
            frameExtra = new Vector2I(Math.Max(0, extra.X), Math.Max(0, extra.Y));
            returnMode = Window.ModeEnum.Windowed;
        }
        else if (window.Mode == Window.ModeEnum.Maximized) returnMode = Window.ModeEnum.Maximized;
    }

    private void OnResize() => cancelLocalInput();

    public void Dispose()
    {
        window.SizeChanged -= OnResize;
        NativePreferences.Current.Changed -= ApplyPreferences;
    }
}
