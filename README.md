# Sidereal Godot

Native Godot frontend for the existing authoritative Sidereal SpacetimeDB world.
This evaluation client implements Dastari browser sign-in, persistent character entry, replicated
deck display and server-validated walking/pilot input. Its shared UI includes configurable colors,
transparency and scale, draggable/resizable windows, Tetris inventory with rotation, equipment slots,
five item hotbar slots and tooltips. Login/crew selection renders the furnished Wayfarer in a 3D dock.
Known stock ships use their published authored surfaces and furnishings, matched to the replicated
construction. The clipped HUD adapts to window size and 75–150% UI scale; open Menu for other panels.
The paper doll uses a silhouette pending the separately assigned replacement character/armor models.
Full gameplay/rendering parity and asset streaming remain planned.

Until the initial PR is merged, clone the implementation branch explicitly:

```powershell
git clone --branch feat/native-spacetimedb-client https://github.com/Dastari/sidereal_godot.git
cd sidereal_godot
```

On Windows, install **Godot 4.7.2 .NET**, **.NET SDK 8.0.425**, and Git. Clone the repository, then
import the root `project.godot`. Connect Tailscale to access the configured server, build the C#
project, and press Play. The standard Godot editor cannot compile this C# client.
The default game endpoint is `https://sidereal.tail7a58a6.ts.net:8447`; authentication uses
`https://auth.dastari.net/realms/dastari`. The editor opens your local files; the running game connects
to the server. [Windows downloads](https://sidereal.tail7a58a6.ts.net:8446) include the runtime.

AI changes and local editor work share Git branches. Save and commit editor changes before pulling.
`powershell -ExecutionPolicy Bypass -File scripts/update-windows.ps1 -Build` pulls without resetting
local work and builds the client. Reload changed scenes in Godot when prompted.
Use `Ui/ThemeGallery.tscn` to edit/review shared components; the Interface window changes your local
theme and layout. The editable default theme is `Ui/default_palette.tres`.

The Windows agent can run focused tests with `dotnet run --project Tests/Tests.csproj`.
For a real Dastari/Tailscale integration test against the separate fixture:

```powershell
Invoke-WebRequest https://sidereal.tail7a58a6.ts.net:8446/client-settings.network-test.json -OutFile client-settings.network-test.json
dotnet run --project Tests/Tests.csproj -- --network-probe client-settings.network-test.json
```

Complete the browser sign-in opened by the probe. This verifies admission, subscriptions, token refresh,
inventory moves, equipment and hotbar reducers on the isolated server; it never seeds the live world.
Physical Windows/GPU acceptance is still pending. Detailed steps and evidence requirements live in the
[Windows/Tailscale runbook](https://wiki.sidereal.dastari.net/Operations/Godot%20Windows%20Development%20and%20Tailscale%20Testing).

On Linux, `python3 scripts/dev.py godot-setup` installs private pinned tools;
`python3 scripts/dev.py godot-export` builds Windows/Linux packages and a source bundle.
The existing backend is a separate project and is never installed or republished by these commands.

Design, setup, auth, validation, limitations, and the asset-streaming proposal:
[Sidereal wiki](https://wiki.sidereal.dastari.net/Architecture/Godot%20Frontend%20Evaluation).
Shared UI contracts: [Godot UI framework](https://wiki.sidereal.dastari.net/Architecture/Godot%20UI%20Framework).
Model tasks and scale/socket rules: [Godot frontend asset tasks](https://wiki.sidereal.dastari.net/Art/Godot%20Frontend%20Asset%20Tasks).
