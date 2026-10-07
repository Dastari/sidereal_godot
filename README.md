# Sidereal Godot

Native Godot frontend for the existing authoritative Sidereal SpacetimeDB world.
The 0.4.1 evaluation implements Dastari browser sign-in, persistent character entry, replicated
decks, walking, piloting, cruise, contextual interaction, combat input and EVA controls.
The responsive blue-glass UI includes Tetris inventory, equipment, tooltips, ground pickup,
scoped storage, draggable/resizable windows and saved theme, display and graphics preferences.
Its eight action frames contain the browser's five item actions and three unavailable slots;
two quick frames inspect items. The six-tab system menu exposes supported local settings and
server-confirmed vessel, appearance and account actions.

Login and crew selection render game assets in a 3D dock. Known stock ships match their exact
replicated construction. The world and paper doll use the released study-v2 crew rig, armor,
faces and animation clips. Shared-space presentation includes actor-disclosed ships, the
reviewed planet/moon appearances, a procedural star, source starfield and dust, and accepted
engine exhaust. Accepted combat actions drive the browser's pinned muzzle, tracer and impact
effects; weapon cards and reload eligibility share exact pinned content definitions. Health has
an independent theme color, and tooltips scale and fit the viewport. Source lighting units and
material receiver/shadow classes are implemented. This is an ongoing parity build: sky color
space, family lighting/tone, some effect and editing interfaces, unsupported custom construction,
asset streaming and Windows/GPU acceptance remain
open. The [parity contract](https://wiki.sidereal.dastari.net/Architecture/Godot%20Browser%20Parity)
records source pins, verified behavior and remaining work.

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
The source ZIP is an editor snapshot; clone Git to use the test and lifecycle scripts below.
`powershell -ExecutionPolicy Bypass -File scripts/update-windows.ps1 -Build` pulls without resetting
local work and builds the client. Reload changed scenes in Godot when prompted.
Use `Ui/ThemeGallery.tscn` to edit/review shared components; the Interface window changes your local
theme and layout. The editable default theme is `Ui/default_palette.tres`.

The Windows agent can run focused tests with `dotnet run --project Tests/Tests.csproj`.
Use WASD to walk relative to the camera, Shift to sprint, E to use nearby objects or a control seat,
X for cruise or EVA suit hold, V for combat, mouse/left click to aim/fire, and R to reload.
I opens inventory, C character, N navigation, Tab deck/flight view, Z loot labels,
and Escape cancels or opens the menu. Right drag orbits and the wheel zooms outside UI panels.
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
Add `--export-directory output/review-downloads` to stage packages for validation before serving them.
The existing backend is a separate project and is never installed or republished by these commands.

Design, setup, auth, validation, limitations, and the asset-streaming proposal:
[Sidereal wiki](https://wiki.sidereal.dastari.net/Architecture/Godot%20Frontend%20Evaluation).
Shared UI contracts: [Godot UI framework](https://wiki.sidereal.dastari.net/Architecture/Godot%20UI%20Framework).
Model tasks and scale/socket rules: [Godot frontend asset tasks](https://wiki.sidereal.dastari.net/Art/Godot%20Frontend%20Asset%20Tasks).
