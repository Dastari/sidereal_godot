using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sidereal.Native;

static void Require(bool value, string message) { if (!value) throw new Exception(message); }
static void Reject(Action operation, string message) { try { operation(); } catch { return; } throw new Exception(message); }
Require(NativeAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "RFC 7636 S256 vector failed");
Require(NativeAuth.ValidateCallback("/callback?state=correct&code=opaque%2Fcode", "correct") == "opaque/code", "Code callback decode failed");
Reject(() => NativeAuth.ValidateCallback("/callback?state=wrong&code=opaque", "correct"), "Forged state accepted");
Reject(() => NativeAuth.ValidateCallback("/callback?state=correct&state=correct&code=opaque", "correct"), "Duplicate state accepted");
Reject(() => NativeAuth.ValidateCallback("/other?state=correct&code=opaque", "correct"), "Wrong callback path accepted");
Reject(() => NativeAuth.ValidateCallback("/callback?state=correct&error=access_denied", "correct"), "Provider rejection accepted");
var pose = RelativePose.World(1_000_000_000_000.25, 1_000_000_000_000.75, 1_000_000_000_000, 1_000_000_000_000);
Require(pose.X == .25 && pose.Z == -.75, "World origin subtraction lost precision");
Reject(() => ClientSettings.Parse("{\"gameOrigin\":\"http://example.com\",\"database\":\"test\",\"issuer\":\"https://auth.dastari.net\",\"clientId\":\"sidereal-game\",\"callbackPort\":43817}"), "Cleartext remote endpoint accepted");
var fixtureSettings = new ClientSettings("https://sidereal.tail7a58a6.ts.net:8448", "native-smoke", "https://auth.dastari.net/realms/dastari", "sidereal-game", 43817);
Require(fixtureSettings.IsIsolatedFixture, "Tailscale isolated fixture route was rejected");
Require(!((fixtureSettings with { GameOrigin = "https://sidereal.tail7a58a6.ts.net:8447" }).IsIsolatedFixture), "Live Tailscale route accepted automated entry");
Require(!((fixtureSettings with { Database = "sidereal-spacetime-dev" }).IsIsolatedFixture), "Live database accepted automated entry");
Console.WriteLine("Native auth callback, PKCE, endpoint and double-origin checks passed.");

InventoryCatalog.LoadRevisionOneSeed(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "item-seed-v1.json")));
var inventoryDemo = InventorySnapshot.Demo();
Require(!inventoryDemo.Fits("ui-demo-0", "demo-pockets", -1, 0, false), "Negative grid coordinate accepted");
Require(!inventoryDemo.Fits("ui-demo-0", "demo-pockets", 7, 3, false), "Multi-cell bounds were ignored");
Require(!inventoryDemo.Fits("ui-demo-0", "demo-pockets", 2, 0, false), "Occupied Tetris cells accepted");
Require(inventoryDemo.Fits("ui-demo-0", "demo-pockets", 0, 0, false), "Moving an item to its own footprint collided with itself");
Require(inventoryDemo.Fits("ui-demo-0", "demo-pockets", 4, 4, true), "Rotation did not exchange width and height");
Require(!inventoryDemo.Fits("ui-demo-0", "demo-pockets", 4, 4, false), "Unrotated tall item fit into a short gap");
Require(InventoryCatalog.Resolve("compact-pistol", 1, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>()) != null, "Revision-one catalogue fallback missing");
Require(InventoryCatalog.Resolve("compact-pistol", 2, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>()) == null, "Unknown revision guessed a revision-one footprint");
var pinned = new Sidereal.Bindings.PublishedItemDefinition("item:compact-pistol@2", "item", "compact-pistol", 2, "retired", "{\"id\":\"compact-pistol\",\"name\":\"Pinned pistol\",\"width\":3,\"height\":2,\"massKg\":2}", "test-revision-2");
Require(InventoryCatalog.Resolve("compact-pistol", 2, new[] { pinned })?.Width == 3, "Retired explicit instance pin failed to resolve");
Console.WriteLine("Tetris bounds, collision, rotation, self-placement and exact definition revision checks passed.");

var worldCatalog = new ReplicatedWorldCatalog(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world-manifest.json")));
foreach (var shipEntry in worldCatalog.Root.GetProperty("ships").EnumerateArray())
{
    Require(ReplicatedWorldCatalog.CanonicalSha256(shipEntry.GetProperty("prefab")) == shipEntry.GetProperty("prefabSha256").GetString(), "Native canonical hashing disagrees with the exact public TypeScript prefab pin");
    Require(worldCatalog.Match(shipEntry.GetProperty("document").GetRawText(), null).GetProperty("id").GetString() == shipEntry.GetProperty("id").GetString(), "Pinned authored assembly did not match its full public document");
}
var publicShip = worldCatalog.Preview("fed.s.wren");
var originalDocument = publicShip.GetProperty("document").GetRawText();
var changedShip = JsonNode.Parse(originalDocument)!;
changedShip["prefab"]!["document"]!["volumes"]![0]!["tiles"]![0]!["x"] = 999;
Reject(() => worldCatalog.Match(changedShip.ToJsonString(), null), "Same-ID changed ship document was silently replaced with stock art");
var changedLayout = JsonNode.Parse(originalDocument)!;
var vertex = changedLayout["layout"]!["tiles"]![0]!["vertices"]![0]!;
vertex[0] = vertex[0]!.GetValue<double>() + 1;
Reject(() => worldCatalog.Match(changedLayout.ToJsonString(), null), "Changed live construction was rendered as stock geometry despite an unchanged prefab pin");
var changedCeiling = JsonNode.Parse(originalDocument)!;
changedCeiling["layout"]!["decks"]![0]!["ceiling"] = 97;
Reject(() => worldCatalog.Match(changedCeiling.ToJsonString(), null), "Changed deck ceiling was rendered as stock construction");
var changedCatalog = JsonNode.Parse(originalDocument)!; changedCatalog["prefab"]!["catalog"] = "unbundled-revision";
Reject(() => worldCatalog.Match(changedCatalog.ToJsonString(), null), "An unknown component catalog was rendered as a known revision");
Reject(() => worldCatalog.Match(originalDocument, "unadmitted-deck"), "Unknown active deck was accepted");
var furnishing = ReplicatedWorldCatalog.ReadFurnishings("{\"chair\":{\"dx\":0.25,\"dy\":-0.5,\"yaw\":1.5707963267948966,\"deleted\":false}}")["chair"];
Require(furnishing.X == .25 && furnishing.Y == -.5 && !furnishing.Deleted, "Canonical furnishing pose did not retain its exact values");
Reject(() => ReplicatedWorldCatalog.ReadFurnishings("[]"), "An invalid furnishing map was accepted");
Reject(() => ReplicatedWorldCatalog.ReadFurnishings("{\"chair\":{\"dx\":1e999,\"dy\":0,\"yaw\":0,\"deleted\":false}}"), "Nonfinite furnishing pose was accepted");
Console.WriteLine("All immutable ship pins match across TypeScript/C#; changed documents/catalogs and unknown decks are rejected; furnishing transforms are finite.");

using (var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "surface-golden.json"))))
{
    Require(golden.RootElement.GetProperty("sourceRevision").GetString() == worldCatalog.Root.GetProperty("sourceRevision").GetString(), "Geometry goldens and bundled renderer came from different source revisions");
    float[] Floats(JsonElement value) => value.EnumerateArray().Select(v => v.GetSingle()).ToArray();
    double[] Doubles(JsonElement value) => value.EnumerateArray().Select(v => v.GetDouble()).ToArray();
    foreach (var fixture in golden.RootElement.GetProperty("fixtures").EnumerateArray())
    {
        var source = fixture.GetProperty("source"); var expected = fixture.GetProperty("expected");
        var channels = new ReplicatedWorldSurface.Channels(Floats(source.GetProperty("positions")), Floats(source.GetProperty("normals")),
            source.GetProperty("indices").EnumerateArray().Select(v => v.GetInt32()).ToArray(),
            Floats(source.GetProperty("uvs")), Floats(source.GetProperty("uvs2")), Floats(source.GetProperty("tangents")));
        var profile = fixture.GetProperty("profile");
        var result = ReplicatedWorldSurface.Transform(channels, Floats(fixture.GetProperty("matrix")),
            fixture.GetProperty("planes").EnumerateArray().Select(Doubles).ToArray(),
            profile.ValueKind == JsonValueKind.Null ? null : new[] { Doubles(profile.GetProperty("bottom")), Doubles(profile.GetProperty("top")) },
            Doubles(fixture.GetProperty("origin")));
        foreach (var (field, actual) in new[] { ("positions", result.Positions), ("normals", result.Normals), ("uvs", result.Uvs), ("uvs2", result.Uvs2), ("tangents", result.Tangents) })
        {
            var reference = Floats(expected.GetProperty(field));
            Require(actual != null && actual.Length == reference.Length, $"Browser/native {fixture.GetProperty("id")} {field} channel length differs");
            Require(actual!.Zip(reference).All(pair => float.IsFinite(pair.First) && Math.Abs(pair.First - pair.Second) <= 1e-5), $"Browser/native {fixture.GetProperty("id")} {field} geometry differs");
        }
        Require(result.Indices.SequenceEqual(expected.GetProperty("indices").EnumerateArray().Select(v => v.GetInt32())), "Browser/native triangle winding differs");
    }
}
Console.WriteLine("Pinned browser/native clipping, reflected tangent handedness, dual UVs and sloped surface golden comparisons passed.");

void VerifyInventory(ClientCore native, Action<Func<bool>, string> wait)
{
    native.ClaimStarterKit();
    wait(() => native.Inventory.Available && native.Inventory.Items.Count > 0, "Private starter inventory did not arrive");
    var before = native.Inventory;
    var ids = before.Items.Select(i => i.Id).OrderBy(id => id).ToArray();
    var item = before.Items.First(i => i.Definition?.EquipSlot == "hand" && i.ContainerId.Length > 0);
    (string Container, int X, int Y, bool Rotated)? destination = null;
    foreach (var container in before.Containers.Where(c => c.Kind == "grid" && c.Carried))
    {
        for (var y = 0; y < container.Height && destination == null; y++) for (var x = 0; x < container.Width && destination == null; x++)
            foreach (var rotated in new[] { false, true })
                if ((container.Id != item.ContainerId || x != item.X || y != item.Y || rotated != item.Rotated) && before.Fits(item.Id, container.Id, x, y, rotated))
                { destination = (container.Id, x, y, rotated); break; }
        if (destination != null) break;
    }
    Require(destination != null, "Starter fixture has no alternate Tetris placement");
    var target = destination!.Value;
    Require(native.MoveItem(item.Id, target.Container, target.X, target.Y, target.Rotated, before.Revision), "Move intent did not dispatch");
    Require(!native.EquipItem(item.Id, before.Revision), "A second mutation dispatched while the first was pending");
    wait(() => !native.InventoryPending, "Move confirmation did not arrive");
    var moved = native.Inventory.Item(item.Id)!;
    Require(moved.ContainerId == target.Container && moved.X == target.X && moved.Y == target.Y && moved.Rotated == target.Rotated, "Server-confirmed Tetris placement disagreed with intent");
    Require(!native.MoveItem(item.Id, target.Container, target.X, target.Y, target.Rotated, before.Revision), "Stale local revision dispatched");
    var revision = native.Inventory.Revision;
    var staleRejected = false;
    var staleOperation = "native-stale-probe:" + Guid.NewGuid().ToString("N");
    native.Connection!.Reducers.OnMoveInventoryItem += (ctx, _, _, _, _, _, _, operation) => { if (operation == staleOperation) staleRejected = ctx.Event.Status is SpacetimeDB.Status.Failed; };
    native.Connection.Reducers.MoveInventoryItem(item.Id, target.Container, target.X, target.Y, target.Rotated, before.Revision, staleOperation);
    wait(() => staleRejected, "Server did not reject stale inventory revision");
    Require(native.Inventory.Revision == revision, "Rejected move altered canonical revision");
    Require(native.AssignHotbar(0, item.Id, revision), "Hotbar assignment did not dispatch");
    wait(() => !native.InventoryPending, "Hotbar assignment was not acknowledged");
    Require(native.Inventory.Hotbar.TryGetValue(0, out var hotbarItem) && hotbarItem == item.Id, "Hotbar reference did not replicate");
    Require(native.ActivateHotbar(0, native.Inventory.Revision), "Hotbar activation did not dispatch");
    wait(() => !native.InventoryPending, "Hotbar activation was not acknowledged");
    Require(native.Inventory.Item(item.Id)?.EquipmentSlot == "hand", "Hotbar activation did not equip canonical item");
    var second = native.Inventory.Items.First(i => i.Id != item.Id && i.Definition?.EquipSlot == "hand" && i.ContainerId.Length > 0);
    Require(native.EquipItem(second.Id, native.Inventory.Revision), "Equipment swap did not dispatch");
    wait(() => !native.InventoryPending, "Equipment swap was not acknowledged");
    Require(native.Inventory.Item(second.Id)?.EquipmentSlot == "hand" && native.Inventory.Item(item.Id)?.EquipmentSlot == "", "Equipment swap did not atomically stow the old hand item");
    Require(native.Inventory.Items.Select(i => i.Id).OrderBy(id => id).SequenceEqual(ids), "Inventory move/equip/hotbar changed stable item IDs");
    Require(Math.Abs(native.Inventory.CarriedMassKg - before.CarriedMassKg) < .0001, "Carried-only moves/equip changed carried mass");
    Console.WriteLine("Canonical Tetris move, stale-revision rejection, five-slot hotbar assign/activate and atomic equipment swap passed; stable item IDs and carried mass preserved.");
}

if (args.Length == 0) return;
if (args[0] is "--auth-probe" or "--network-probe")
{
    var config = ClientSettings.Parse(File.ReadAllText(args[1]));
    var network = args[0] == "--network-probe";
    Require(config.IsIsolatedFixture && config.Issuer == "https://auth.dastari.net/realms/dastari" &&
        (network ? config.GameOrigin == "https://sidereal.tail7a58a6.ts.net:8448" : new Uri(config.GameOrigin).IsLoopback && new Uri(config.GameOrigin).Port != 3100), "Provider probe requires the designated isolated test server");
    var auth = new NativeAuth(config);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    using var authUrlReady = new ManualResetEventSlim();
    var login = auth.SignIn(url => {
        if (args.Length > 2)
        {
            File.WriteAllText(args[2], url);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(args[2], UnixFileMode.UserRead | UnixFileMode.UserWrite);
            authUrlReady.Set();
        }
        else { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); Console.WriteLine("Complete sign-in in your browser."); }
    }, timeout.Token);
    // An unrelated incomplete callback must time out without cancelling login.
    if (args.Length > 2)
    {
        while (!authUrlReady.IsSet)
        {
            if (login.IsCompleted) { login.GetAwaiter().GetResult(); throw new Exception("Provider did not publish an authorization URL"); }
            timeout.Token.ThrowIfCancellationRequested();
            authUrlReady.Wait(20, timeout.Token);
        }
        using (var stalled = new TcpClient())
        { stalled.Connect("127.0.0.1", 43817); Thread.Sleep(5500); }
        Require(!login.IsCompleted, "An unsolicited slow callback cancelled login");
        File.WriteAllText(args[2] + ".ready", "ready");
    }
    var identity = login.GetAwaiter().GetResult();
    using var native = new ClientCore(config);
    native.Error += message => Console.WriteLine(message);
    native.Connect(identity.IdToken);
    void ProviderWait(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (!ready() && DateTime.UtcNow < until) { native.Tick(); Thread.Sleep(10); }
        Require(ready(), "Real provider proof/subscription did not become ready");
    }
    ProviderWait(() => native.Connection != null);
    native.Enter("Godot OIDC"); ProviderWait(() => native.Character?.Connected == true);
    var id = native.Character!.Id;
    var renewed = auth.Refresh(identity, timeout.Token).GetAwaiter().GetResult();
    native.Connect(renewed.IdToken);
    ProviderWait(() => native.Status.StartsWith("Connected") && native.Character?.Id == id);
    VerifyInventory(native, (condition, _) => ProviderWait(condition));
    Console.WriteLine("Real provider PKCE, signature/nonce validation, socket proof, token refresh and UUID-preserving replacement passed.");
    return;
}
Require(args.Length == 2, "Native smoke requires an explicit isolated endpoint and database");
var settings = new ClientSettings(args[0], args[1], "https://auth.dastari.net/realms/dastari", "sidereal-game", 43817);
Require(settings.IsIsolatedFixture, "Native smoke requires the isolated local :3131 or Tailscale :8448 endpoint and a -smoke database");
using var client = new ClientCore(settings);
var clients = new List<ClientCore> { client };
void Wait(Func<bool> ready, string message)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!ready() && DateTime.UtcNow < deadline) { foreach (var c in clients) c.Tick(); Thread.Sleep(10); }
    Require(ready(), message);
}
client.Connect("", false);
Wait(() => client.Connection != null, "SDK subscription did not apply");
client.Enter("GodotPilot");
Wait(() => client.Character?.Connected == true && client.Instance != null, "Server character and admitted deck did not arrive");
if (Environment.GetEnvironmentVariable("SIDEREAL_NATIVE_WORLD_FIXTURE") is { Length: > 0 } fixtureOutput)
    File.WriteAllText(fixtureOutput, System.Text.Json.JsonSerializer.Serialize(new {
        client.Instance!.DocumentJson, client.Instance.FurnishingsJson, client.Instance.BlueprintSha256,
        client.Location!.DeckId, shipName = client.Ship?.Name
    }));
var uuid = client.Character!.Id;
Require(worldCatalog.Match(client.Instance!.DocumentJson, client.Location!.DeckId).GetProperty("id").GetString() == "fed.s.wren", "Native asset catalog did not match the actual isolated actor-visible ship document");
Wait(() => client.Vitals?.CharacterId == uuid, "Actor-filtered character vitals did not arrive");
Require(client.Vitals!.MaxHealth > 0 && client.Vitals.Health >= 0 && client.Vitals.Health <= client.Vitals.MaxHealth, "Replicated character health is invalid");
Require(client.Power == null || client.Power.ShipId == client.Character.ShipId, "Power telemetry belongs to another ship");
Require(client.Combat == null || client.Combat.CharacterId == uuid, "Weapon telemetry belongs to another actor");
Console.WriteLine("Actor-filtered native health/power/weapon telemetry is scoped to the current actor and ship.");
var x = client.Character.LocalX;
var y = client.Character.LocalY;
// Walk in several directions: the canonical prefab spawn may face a wall in one.
client.ClaimControls();
foreach (var direction in new[] { (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0) })
{
    var until = DateTime.UtcNow.AddMilliseconds(600);
    while (DateTime.UtcNow < until) { client.SendIntent(0, 0, direction.Item1, direction.Item2, false); client.Tick(); Thread.Sleep(50); }
    if (Math.Abs(client.Character.LocalX - x) + Math.Abs(client.Character.LocalY - y) > .1) break;
}
Require(Math.Abs(client.Character.LocalX - x) + Math.Abs(client.Character.LocalY - y) > .1, "Server-confirmed walking did not advance");
client.ReleaseControls();
VerifyInventory(client, Wait);
var afterRelease = DateTime.UtcNow.AddMilliseconds(300);
while (DateTime.UtcNow < afterRelease) { client.Tick(); Thread.Sleep(10); }
x = client.Character.LocalX; y = client.Character.LocalY;
client.Connection!.Reducers.SetIntent(10_000, 0, 0, 1, 0, false);
var stoppedUntil = DateTime.UtcNow.AddMilliseconds(500);
while (DateTime.UtcNow < stoppedUntil) { client.Tick(); Thread.Sleep(10); }
Require(Math.Abs(client.Character.LocalX - x) + Math.Abs(client.Character.LocalY - y) < .01, "Released socket still moved the actor");
Console.WriteLine("Native C# SDK passed filtered subscription, canonical deck, server-confirmed movement and rejection of input after release.");
// Replace the socket with the same identity and wait for subscriptions before retiring it.
var developmentToken = client.DevelopmentToken;
Require(!string.IsNullOrEmpty(developmentToken), "No development credential for replacement proof");
client.Connect(developmentToken!, false);
Wait(() => client.Connection != null && client.Character?.Id == uuid && client.Status.StartsWith("Connected"), "Connection replacement changed stable character identity");
using var takeover = new ClientCore(settings); clients.Add(takeover);
takeover.Connect(developmentToken!, false);
Wait(() => takeover.Connection != null && takeover.Character?.Id == uuid, "Same-account socket did not see stable character");
var firstClaimed = false;
client.Connection!.Reducers.OnClaimInputControl += ctx => firstClaimed = ctx.Event.Status is SpacetimeDB.Status.Committed;
client.ClaimControls();
Wait(() => firstClaimed, "First socket control claim was not committed");
var takeoverClaimed = false;
takeover.Connection!.Reducers.OnClaimInputControl += ctx => takeoverClaimed = ctx.Event.Status is SpacetimeDB.Status.Committed;
takeover.ClaimControls();
Wait(() => takeoverClaimed, "Takeover control claim was not committed");
var settle = DateTime.UtcNow.AddMilliseconds(300);
while (DateTime.UtcNow < settle) { foreach (var c in clients) c.Tick(); Thread.Sleep(10); }
x = client.Character.LocalX; y = client.Character.LocalY;
client.Connection!.Reducers.SetIntent(20_000, 0, 0, 1, 0, false);
settle = DateTime.UtcNow.AddMilliseconds(500);
while (DateTime.UtcNow < settle) { foreach (var c in clients) c.Tick(); Thread.Sleep(10); }
Require(Math.Abs(client.Character.LocalX - x) + Math.Abs(client.Character.LocalY - y) < .01, "Socket losing input takeover still moved the actor");
takeover.ReleaseControls();
using var other = new ClientCore(settings); clients.Add(other);
other.Connect("", false);
Wait(() => other.Connection != null, "Second actor subscription did not apply");
Require(other.Character == null, "Another identity received the first character");
Require(other.Instance == null, "Another identity received the first private interior");
Require(!other.Connection!.Db.OwnCharacterVitals.Iter().Any() && !other.Connection.Db.OwnShipPower.Iter().Any() && !other.Connection.Db.OwnCombat.Iter().Any(), "Unrelated identity received private health/power/weapon telemetry");
Require(!other.Connection!.Db.OwnInventoryState.Iter().Any() && !other.Connection.Db.OwnInventoryItems.Iter().Any() &&
    !other.Connection.Db.OwnInventoryContainers.Iter().Any() && !other.Connection.Db.OwnInventoryHotbar.Iter().Any() &&
    !other.Connection.Db.OwnItemDefinitionPins.Iter().Any(), "Another identity received private inventory rows");
Console.WriteLine("Native replacement retained UUID; takeover rejected stale-socket input; unrelated identity received no character or interior rows.");
