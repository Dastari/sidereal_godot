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
using Sidereal.Native.Input;
using System.Security.Cryptography;

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
var weaponIds = new[] { "compact-pistol", "heavy-handgun", "carbine", "long-rifle", "pistol", "smg", "compact-carbine", "rifle", "shotgun", "heavy-gun", "beam-rifle", "rail-rifle", "stun-gun", "baton", "grenade" };
var seedPayload = "{" + string.Join(",", weaponIds.Select(id => JsonSerializer.Serialize(id) + ":" +
    (PinnedWeaponDefinitions.ResolvePayload(id, 1, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>())?.Payload ?? throw new Exception("Missing source weapon seed " + id)))) + "}";
Require(Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seedPayload))).ToLowerInvariant() ==
    "63d6b4c6b0cd376e140a1077796ae4442858ca6f603fccccb462dba434a5e615", "Revision-one weapon balance differs from independent immutable source output");
foreach (var id in weaponIds)
{
    Require(PinnedWeaponDefinitions.ResolvePayload(id, 0, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>()) == null &&
        PinnedWeaponDefinitions.ResolvePayload(id, 2, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>()) == null, "Weapon fallback guessed zero or newer revision");
    Require(PinnedWeaponDefinitions.SupportsReload(id, 1, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>()) == (id is not ("baton" or "grenade")), "Exact source manual reload eligibility differs: " + id);
}
Require(PinnedWeaponDefinitions.ResolvePayload("unpublished-weapon", 1, Array.Empty<Sidereal.Bindings.PublishedItemDefinition>()) == null, "Unknown source weapon was guessed");
var publishedWeapon = new Sidereal.Bindings.PublishedItemDefinition("weapon:compact-pistol@1", "weapon", "compact-pistol", 1, "published", "{\"reloadMs\":2400}", "pinned-test");
foreach (var status in new[] { "published", "retired" })
{
    var exact = new Sidereal.Bindings.PublishedItemDefinition(publishedWeapon.DefinitionRef, publishedWeapon.Kind, publishedWeapon.DefinitionId, publishedWeapon.Revision, status, publishedWeapon.PayloadJson, publishedWeapon.Sha256);
    Require(PinnedWeaponDefinitions.ResolvePayload("compact-pistol", 1, new[] { exact })?.Payload == exact.PayloadJson, "Exact published/retired weapon pin lost precedence");
}
foreach (var payload in new[] { "null", "invalid", "{}", "{\"reloadMs\":0}", "{\"reloadMs\":-1}", "{\"reloadMs\":\"1200\"}", "{\"reloadMs\":1e999}" })
{
    var malformed = new Sidereal.Bindings.PublishedItemDefinition(publishedWeapon.DefinitionRef, publishedWeapon.Kind, publishedWeapon.DefinitionId, publishedWeapon.Revision, "published", payload, "invalid-" + payload);
    Require(PinnedWeaponDefinitions.ResolvePayload("compact-pistol", 1, new[] { malformed })?.Payload == payload &&
        !PinnedWeaponDefinitions.SupportsReload("compact-pistol", 1, new[] { malformed }), "Existing malformed pin was replaced by authored reload eligibility");
}
Console.WriteLine("All15 exact source weapon fallback payloads and reload eligibility pass; published/retired pin precedence and malformed/unknown revision refusal pass.");


var cargoSnapshot = inventoryDemo with
{
    Items = inventoryDemo.Items.Select(i => i with { ScopedRevision = 10 }).ToArray(),
    Containers = inventoryDemo.Containers.Select(c => c with { ScopedRevision = 20 }).Concat(new[] {
        new InventoryContainerView("reachable-crate", "", "grid", "Supply crate", 8, 4, 500, 0, 0, "", false, "admitted-placement", 30, true)
    }).ToArray()
};
var cargoPlan = InventoryCargoPlan.Create(cargoSnapshot, "ui-demo-0", "reachable-crate", (2, 1, true));
Require(cargoPlan.ExpectedItemRevision == 10 && cargoPlan.ExpectedSourceRevision == 20 &&
    cargoPlan.ExpectedDestinationRevision == 30 && cargoPlan.ExpectedCharacterRevision == 42 &&
    cargoPlan.Rotated && cargoPlan.X == 2 && cargoPlan.Y == 1, "Scoped transfer lost exact disclosure revisions or placement");
Require(cargoSnapshot.UsesScopedCargo("ui-demo-0", "reachable-crate"), "Reachable storage used the carried-only transaction route");
Reject(() => InventoryCargoPlan.Create(cargoSnapshot, "ui-demo-4", "reachable-crate"), "Equipped item was transferred without first stowing it");
Reject(() => InventoryCargoPlan.Create(cargoSnapshot, "ui-demo-0", "undisclosed-crate"), "Undisclosed storage was admitted");
Reject(() => InventoryCargoPlan.Create(cargoSnapshot, "ui-demo-0", "reachable-crate", (7, 3, false)), "Out-of-bounds scoped placement was proposed");
var missingCargoRevision = cargoSnapshot with { Containers = cargoSnapshot.Containers.Select(c => c.Id == "reachable-crate" ? c with { ScopedRevision = null } : c).ToArray() };
Reject(() => InventoryCargoPlan.Create(missingCargoRevision, "ui-demo-0", "reachable-crate"), "Missing disclosure revision was guessed");
var cyclicCargo = cargoSnapshot with { Containers = cargoSnapshot.Containers.Select(c => c.Id == "reachable-crate" ? c with { ParentItemId = "ui-demo-0" } : c).ToArray() };
Reject(() => InventoryCargoPlan.Create(cyclicCargo, "ui-demo-0", "reachable-crate"), "Self-contained inventory was proposed");
Console.WriteLine("Scoped cargo proposals retain exact revisions; disclosure loss, unknown destinations, equipped transfers and self-containment are rejected.");

var preferenceDirectory = Path.Combine(Path.GetTempPath(), "sidereal-preferences-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(preferenceDirectory);
try
{
    var preferenceFile = Path.Combine(preferenceDirectory, "presentation.json");
    var preferences = new NativePreferences();
    Require(!preferences.Load(preferenceFile), "A missing profile was reported as loaded");
    preferences.Set(new NativePreferencesSnapshot { Brightness = 1.3, Gamma = 1.8, PanelOpacity = .63, UiScale = 1.25, Shadows = false });
    var reloaded = new NativePreferences();
    Require(reloaded.Load(preferenceFile) && reloaded.Snapshot == preferences.Snapshot, "Display preferences did not round-trip exactly");
    reloaded.ResetGraphics();
    Require(reloaded.Snapshot.Brightness == 1 && reloaded.Snapshot.Gamma == 1 && reloaded.Snapshot.Shadows &&
        reloaded.Snapshot.UiScale == 1.25 && reloaded.Snapshot.PanelOpacity == .63, "Graphics reset changed independent UI preferences");
    File.WriteAllText(preferenceFile, "{corrupt");
    Require(!reloaded.Load(preferenceFile) && reloaded.Snapshot == new NativePreferencesSnapshot(), "A corrupt profile retained stale prior settings");
    var normalized = new NativePreferencesSnapshot { Brightness = double.NaN, Gamma = 900, Saturation = -1, UiScale = 6, MsaaSamples = 99, LocalLightLimit = "999" }.Normalized();
    Require(normalized.Brightness == 1 && normalized.Gamma == 2 && normalized.Saturation == 0 && normalized.UiScale == 1.5 && normalized.MsaaSamples == 4 && normalized.LocalLightLimit == "all", "Invalid graphics preferences escaped normalization");
    foreach (var color in new[] { new[] { 0d, 0d, 0d }, new[] { 1d, 1d, 1d }, new[] { .125, .5, .875 } })
        Require(new NativePreferencesSnapshot().TransferDisplay(color[0], color[1], color[2]).Zip(color).All(pair => Math.Abs(pair.First - pair.Second) < 1e-12), "Neutral display correction was not identity");
    var desaturated = (new NativePreferencesSnapshot { Saturation = 0 }).TransferDisplay(1, 0, 0);
    Require(desaturated.All(channel => Math.Abs(channel - .2126) < 1e-12), "Display saturation does not match browser luminance transfer");
    var gamma = (new NativePreferencesSnapshot { Gamma = 2 }).TransferDisplay(.25, .25, .25);
    Require(gamma.All(channel => Math.Abs(channel - .5) < 1e-12), "Display gamma does not match browser transfer");
    Console.WriteLine("Native display transfer, finite normalization, versioned persistence/corruption recovery and scoped resets passed.");
}
finally { Directory.Delete(preferenceDirectory, true); }

var mappedWalk = GameplayRules.ScreenToDeck(1, 1, Math.PI / 2, 0);
Require(Math.Abs(mappedWalk.Dx - Math.Sqrt(.5)) < 1e-12 && Math.Abs(mappedWalk.Dy - Math.Sqrt(.5)) < 1e-12, "Camera-relative diagonal walking changed direction or magnitude");
var keys = new GameplayKeyState();
keys.Press("W", true); keys.Clear(); keys.Press("D", true);
Require(!keys.IsPressed("W") && keys.IsPressed("D") && !keys.Press("S", false), "A cleared held key resumed across a UI blocker");
var cruise = new CruiseControl();
Require(cruise.Toggle("accepted-seat", 12, 30) && Math.Abs(cruise.Demand(0, "accepted-seat", false) - .4) < 1e-12, "Cruise did not retain accepted forward demand");
Require(cruise.Demand(-1, "accepted-seat", false) == -1 && !cruise.Active, "Manual reverse did not cancel cruise");
cruise.Toggle("accepted-seat", 0, 30); cruise.Demand(0, "different-seat", false);
Require(!cruise.Active && !cruise.Toggle(null, 0), "Cruise survived seat relationship loss");
var clock = 0d; var claims = 0; var releases = 0; var stalls = 0;
var lease = new ControlLease(() => claims++, () => releases++, () => stalls++, _ => { }, () => clock);
lease.Activate(true);
Require(claims == 1 && !lease.CanSend, "Movement began before claim acknowledgment");
lease.Activate(false); lease.ClaimResult(true);
Require(!lease.CanSend && releases == 1, "Late claim acknowledgment revived a released lease");
lease.ReleaseResult(); lease.Activate(true); lease.ClaimResult(true);
Require(lease.CanSend && claims == 2, "Fresh acknowledged control claim did not resume");
lease.Activate(false); clock = 2; lease.Tick();
Require(stalls == 1 && !lease.CanSend, "Timed-out release retained authority locally");
foreach(var blur in new[]{false,true})
{
    var eventTime=0d;var eventClaims=0;var eventReleases=0;var eventStalls=0;
    var queued=new ControlLease(()=>eventClaims++,()=>eventReleases++,()=>eventStalls++,_=>{},()=>eventTime);
    queued.Activate(true);queued.ClaimResult(true);queued.Activate(false);
    eventTime=.8;queued.Activate(!blur);
    Require(eventStalls==0&&!queued.CanSend&&eventClaims==1&&eventReleases==1,"Input/focus phase expired an acknowledgement before the SDK could drain it");
    queued.ReleaseResult();queued.Tick();
    if(blur){Require(!queued.CanSend&&eventClaims==1,"Blur revived a canceled grant");queued.Activate(true);}
    Require(eventClaims==2&&!queued.CanSend,"Queued release bypassed the fresh claim ACK");
    queued.ClaimResult(true);Require(queued.CanSend,"Post-drain fresh acknowledged claim failed");
    queued.Activate(false);eventTime+=.501;queued.Tick();
    Require(eventStalls==1&&!queued.CanSend,"Post-drain release timeout was weakened");
}
var claimTime=0d;var claimStalls=0;
var noClaimAck=new ControlLease(()=>{},()=>{},()=>claimStalls++,_=>{},()=>claimTime);
noClaimAck.Activate(true);claimTime=1.501;noClaimAck.Tick();
Require(claimStalls==1&&!noClaimAck.CanSend,"Post-drain missing-claim deadline was weakened");
Console.WriteLine("Queued release ACKs survive input/blur ordering, canceled grants stay canceled, and post-SDK-pump0.5s/1.5s retirement deadlines remain enforced.");

var offered = new List<(ulong Sequence, MovementIntent Intent)>(); clock = 0;
var transmitter = new IntentTransmitter((serial, intent) => offered.Add((serial, intent)), _ => { }, () => stalls++, () => clock);
Require(transmitter.Offer(MovementIntent.Zero, piloting: true), "First braking demand was omitted");
Require(!transmitter.Offer(MovementIntent.Zero, piloting: true), "Unchanged pilot input flooded the socket");
clock = .101;
Require(transmitter.Offer(MovementIntent.Zero, piloting: true), "Zero pilot braking heartbeat was omitted");
for (var index = 0; index < 40; index++) { clock += .101; transmitter.Offer(MovementIntent.Zero, piloting: true); }
Require(transmitter.PendingCount == 32 && stalls == 2, "Unacknowledged intent requests were not bounded");
Require(offered.Select(pair => pair.Sequence).SequenceEqual(Enumerable.Range(1, 32).Select(i => (ulong)i)), "Movement sequence was duplicated or regressed");
Console.WriteLine("Camera-relative movement, held-key clearing, cruise relationship loss, acknowledged leases and bounded pilot heartbeats passed.");

var scopeHandles = new List<ControlledWorldScope>();
var scopeFailures = 0;
using (var scopes = new SharedWorldScopes((queries, applied, _) => {
    var handle = new ControlledWorldScope(queries, applied); scopeHandles.Add(handle); return handle;
}, () => { }, () => scopeFailures++))
{
    var scopeActor = "11111111-1111-4111-8111-111111111111";
    var scopeShip = "22222222-2222-4222-8222-222222222222";
    var scopeSystem = "ad7bf00a-caa0-50ee-b307-332afaac71a1";
    var admission = new WorldScopeAdmission(scopeActor, scopeShip, scopeSystem, 1);
    var accepted = new WorldScopeMotion(scopeShip, scopeSystem, -.01, -400.01, -1, -2, 1);
    Require(scopes.Accept(admission, accepted, null) && scopeHandles.Count == 1 && !scopes.Ready, "Spatial coverage became ready before own-motion subscription applied");
    scopeHandles[0].Apply();
    Require(scopeHandles.Count == 2 && scopeHandles[1].Queries.Length == 9, "Spatial discovery did not create exactly nine cell queries");
    Require(scopeHandles[1].Queries.All(q => q.Contains("system_id = '" + scopeSystem + "'") && q.Contains("cell_x = ") && q.Contains("cell_y = ")), "Cell subscription lost system or cell bounds");
    scopeHandles[1].Apply(); Require(scopes.Ready, "Applied spatial coverage was not ready");
    scopes.Accept(admission, accepted with { X = 400, Y = 800, CellX = 1, CellY = 2, Tick = 2 }, null);
    Require(scopes.CellSets == 2 && scopeHandles.Count == 3, "Crossing a cell did not retain old applied coverage while replacement was pending");
    scopes.Accept(admission, accepted with { X = 1200, Y = 1600, CellX = 3, CellY = 4, Tick = 3 }, null);
    Require(scopes.CellSets == 2 && scopeHandles.Count == 3, "Rapid crossing allocated an unbounded third discovery scope");
    scopeHandles[2].Apply();
    Require(scopeHandles[1].Retiring && scopes.CellSets == 2, "Previous discovery scope retired before replacement ACK or did not remain bounded");
    scopeHandles[1].Retired();
    Require(scopes.CellSets == 2 && scopeHandles.Count == 4 && scopeHandles[3].Queries.Any(q => q.EndsWith("cell_y = 5")), "Latest queued observer target was not coalesced after retirement ACK");
    scopeHandles[3].Apply(); scopeHandles[2].Retired();
    Require(scopes.Ready && scopes.CellSets == 1, "Settled spatial scopes retained old discovery subscriptions");
    var epoch = scopes.Epoch;
    scopes.Accept(null, null, null);
    Require(!scopes.Ready && scopes.Epoch > epoch && scopeHandles[0].Retiring && scopeHandles[3].Retiring, "Admission loss retained old discovery coverage");
    scopeHandles[0].Retired(); scopeHandles[3].Retired();
    Require(scopes.CellSets == 0 && scopeFailures == 0, "Spatial retirement did not dispose all discovery handles");
    Reject(() => SharedWorldScopes.CellQueries("forged' OR true", 0, 0), "Unvalidated system identity reached a subscription query");
}
Console.WriteLine("Spatial scopes use nine bounded cells, negative floor coordinates, coalesced crossings, ACK retirement and admission-loss disposal.");

var joinJournal = new MemorySharedJoinJournal();
var joinRequests = new List<SharedJoinRequest>(); clock = 0;
var joinActor = "11111111-1111-4111-8111-111111111111";
var joinShip = "22222222-2222-4222-8222-222222222222";
var joinContext = new SharedJoinContext(true, true, false, new string('a', 64), "isolated-smoke",
    new[] { new SharedJoinActor(joinActor, joinShip, true) }, new[] { new SharedJoinShip(joinShip, 7) }, Array.Empty<WorldScopeAdmission>());
var join = new SharedWorldJoin(joinJournal, request => joinRequests.Add(request), () => clock);
Require(join.Join(joinContext) && join.Pending && joinRequests.Count == 1 && joinJournal.Values.Count == 1, "Shared entry was sent before its request was durably recorded");
var firstJoin = joinRequests[0];
Require(SharedWorldJoin.Decode(SharedWorldJoin.Encode(firstJoin)) == firstJoin && Guid.TryParseExact(firstJoin.OperationId, "D", out _), "Shared entry revisions/operation did not round-trip");
clock = 11; join.Observe(joinContext);
Require(!join.Pending && joinJournal.Values.Count == 1 && join.Phase == "error", "Uncertain entry confirmation erased its durable operation");
var restartedJoin = new SharedWorldJoin(joinJournal, request => joinRequests.Add(request), () => clock);
Require(restartedJoin.Join(joinContext) && joinRequests[1] == firstJoin, "Restart minted another join operation instead of replaying the exact request");
clock = 22; restartedJoin.Observe(joinContext);
var revisedJoinContext = joinContext with { Ships = new[] { new SharedJoinShip(joinShip, 8) } };
Require(!restartedJoin.Join(revisedJoinContext) && restartedJoin.ReviewRequired && joinRequests.Count == 2, "Stale join revisions were silently rebased and sent");
Require(restartedJoin.DiscardPending(revisedJoinContext) && restartedJoin.Join(revisedJoinContext) && joinRequests[2].OperationId != firstJoin.OperationId && joinRequests[2].ExpectedShipRevision == 8, "Reviewed join discard did not create a fresh explicit request");
var admittedJoinContext = revisedJoinContext with { Admissions = new[] { new WorldScopeAdmission(joinActor, joinShip, "ad7bf00a-caa0-50ee-b307-332afaac71a1", 1) } };
restartedJoin.Observe(admittedJoinContext);
Require(!restartedJoin.Pending && restartedJoin.Phase == "admitted" && joinJournal.Values.Count == 0, "Accepted admission did not retire the join journal");
Reject(() => SharedWorldJoin.AccountKey("isolated-smoke", "unverified"), "Unverified identity reached the join journal key");
Require(SharedWorldJoin.Decide(joinContext with { ConstructionReview = true }).Kind == "blocked", "Legacy migration left a construction review implicitly");
Console.WriteLine("Durable shared entry retains uncertain requests, exact UUID/revision replay, explicit stale-request review and accepted-admission cleanup.");

foreach (var unauthorized in new[] { false, true })
{
    var failingJournal = new FailingSharedJoinJournal { Unauthorized = unauthorized, Saved = SharedWorldJoin.Encode(firstJoin) };
    var unexpectedSends = 0;
    var cleanupJoin = new SharedWorldJoin(failingJournal, _ => unexpectedSends++, () => clock);
    cleanupJoin.Observe(admittedJoinContext);
    Require(cleanupJoin.Phase == "admitted" && !cleanupJoin.Pending && cleanupJoin.JournalCleanupFailed && failingJournal.Saved != null,
        "Journal cleanup failure escaped or replaced accepted admission");
    for (var tick = 0; tick < 100; tick++) cleanupJoin.Observe(admittedJoinContext);
    Require(failingJournal.RemoveCalls == 1 && unexpectedSends == 0, "Admitted frames repeatedly retried unavailable storage or sent another join");
    Require(!cleanupJoin.Join(admittedJoinContext) && failingJournal.RemoveCalls == 2 && cleanupJoin.Phase == "admitted",
        "Explicit admitted cleanup retry changed server admission");
    Require(!cleanupJoin.DiscardPending(revisedJoinContext) && failingJournal.Saved == SharedWorldJoin.Encode(firstJoin) && cleanupJoin.ReviewRequired,
        "Failed journal discard erased the original operation or permitted silent rebasing");
    failingJournal.DenyRemoval = false;
    Require(cleanupJoin.DiscardPending(revisedJoinContext) && !cleanupJoin.JournalCleanupFailed && failingJournal.Saved == null && unexpectedSends == 0,
        "Explicit recovery did not clear the old journal without sending a new operation");
}
var callbackLedger = new GameplayCallbackLedger<object>();
var oldCommandSocket = new object(); var freshCommandSocket = new object();
Require(callbackLedger.TryAdd(oldCommandSocket, "operation-A", "ship-button", new[] { "ship", "button-A" }, 10) &&
    callbackLedger.TryAdd(oldCommandSocket, "operation-B", "ship-button", new[] { "ship", "button-B" }, 10), "Distinct callback arguments collided");
Require(callbackLedger.Complete(oldCommandSocket, "ship-button", new[] { "ship", "button-A" }) == "operation-A" && callbackLedger.PendingCount == 1,
    "Button A acknowledged a different pending device");
Require(!callbackLedger.TryAdd(oldCommandSocket, "duplicate-B", "ship-button", new[] { "ship", "button-B" }, 10), "Indistinguishable concurrent callback was accepted");
Require(callbackLedger.Expire(11).Single() == oldCommandSocket && callbackLedger.IsRetired(oldCommandSocket) && callbackLedger.PendingCount == 0,
    "An uncertain command did not retire all attempts on its socket");
Require(!callbackLedger.TryAdd(oldCommandSocket, "retry-B", "ship-button", new[] { "ship", "button-B" }, 20) &&
    callbackLedger.Complete(oldCommandSocket, "ship-button", new[] { "ship", "button-B" }) == null,
    "Late equal-argument callback revived a timed-out socket");
Require(callbackLedger.TryAdd(freshCommandSocket, "fresh-B", "ship-button", new[] { "ship", "button-B" }, 20) &&
    callbackLedger.Complete(oldCommandSocket, "ship-button", new[] { "ship", "button-B" }) == null && callbackLedger.PendingCount == 1 &&
    callbackLedger.Complete(freshCommandSocket, "ship-button", new[] { "ship", "button-B" }) == "fresh-B", "Retired callback settled a fresh socket request");
foreach (var kind in new[] { "leave-pilot", "legacy-station", "rescue-beacon" })
    Require(!callbackLedger.TryAdd(oldCommandSocket, "old-" + kind, kind, Array.Empty<string>(), 20), "No-argument command reused an uncertain socket");
Console.WriteLine("Unavailable join journals preserve admission and exact requests without frame-loop IO; device callbacks correlate arguments and retire uncertain sockets.");

var motionHistory = new SpaceMotionHistory();
var oldYaw = 179 * Math.PI / 180; var newYaw = -179 * Math.PI / 180;
motionHistory.Remember(new("accepted-system", 10, 0, 0, 2, 0, oldYaw, 0), 1000);
motionHistory.Remember(new("accepted-system", 12, 10, 4, 2, 0, newYaw, 0), 1100);
var halfway = motionHistory.At(1150)!.Value;
Require(Math.Abs(halfway.Pose.X - 5) < 1e-12 && Math.Abs(halfway.Pose.Y - 2) < 1e-12 && Math.Abs(Math.Abs(halfway.Pose.Heading) - Math.PI) < 1e-12, "Remote motion interpolation changed accepted midpoint or long-way yaw");
Require(motionHistory.At(1500)!.Value.Pose.X == 10 && motionHistory.At(2500)!.Value.Stale, "Remote presentation extrapolated beyond accepted motion or lost staleness");
motionHistory.Remember(new("accepted-system", 11, 999, 999, 0, 0, 0, 0), 1600);
Require(motionHistory.Count == 2 && motionHistory.At(1600)!.Value.Pose.X == 10, "Older shared motion rewound the accepted render history");
motionHistory.Remember(new("replacement-system", 1, 20, 30, 0, 0, 0, 0), 1700);
Require(motionHistory.Count == 1 && motionHistory.At(1700)!.Value.Pose.X == 20, "New system mixed old rendering samples");
Console.WriteLine("Shared motion uses accepted midpoint/short yaw interpolation, old-tick rejection, epoch reset, staleness and no extrapolation.");

var crewCatalog = new CrewCatalog(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "crew-catalog.json")), File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "crew-semantics.json")));
var animationBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "crew-anims.glb.bin"));
var animationBank = new CrewAnimationBank(animationBytes);
Require(animationBank.Count == 244 && animationBank.BoneNames.Length == 32 && crewCatalog.Scale == .9, "Released crew animation count, rig or single root scale changed");
using (var crewGolden = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "crew-golden.json"))))
{
    Require(Convert.ToHexString(SHA256.HashData(animationBytes)).ToLowerInvariant() == crewGolden.RootElement.GetProperty("sourceAnimationSha256").GetString(), "Independent animation goldens came from different released bytes");
    foreach (var fixture in crewGolden.RootElement.GetProperty("cases").EnumerateArray())
    {
        var clip = fixture.GetProperty("clip").GetString()!;
        Require(animationBank.Has(clip) && Math.Abs(animationBank.Duration(clip) - fixture.GetProperty("duration").GetDouble()) < 2e-5, "Native crew lost a released clip/duration");
        var poseSample = animationBank.Sample(clip, fixture.GetProperty("time").GetDouble(), fixture.GetProperty("loop").GetBoolean());
        for (var i = 0; i < animationBank.BoneNames.Length; i++)
        {
            var expected = fixture.GetProperty("bones").GetProperty(animationBank.BoneNames[i]);
            System.Numerics.Vector3 Vector(string field) { var v = expected.GetProperty(field); return new(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()); }
            var q = expected.GetProperty("rotation"); var rotation = new System.Numerics.Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle());
            Require(System.Numerics.Vector3.Distance(poseSample[i].Position, Vector("position")) <= 2e-5 &&
                System.Numerics.Vector3.Distance(poseSample[i].Scale, Vector("scale")) <= 2e-5 &&
                1 - Math.Abs(System.Numerics.Quaternion.Dot(poseSample[i].Rotation, rotation)) <= 2e-5,
                "Native crew animation differs from independent released glTF sample: " + clip + "/" + animationBank.BoneNames[i]);
        }
    }
}
foreach (var fixture in crewCatalog.Semantics.GetProperty("faceGoldens").EnumerateArray())
{
    var face = crewCatalog.Catalog.GetProperty("face").GetProperty(fixture.GetProperty("variant").GetString()!);
    var colors = fixture.GetProperty("colors"); var state = fixture.GetProperty("state");
    string Hex(string field) => "#" + string.Concat(colors.GetProperty(field).EnumerateArray().Select(channel => channel.GetByte().ToString("x2")));
    var age = state.GetProperty("age").GetString(); var detail = state.GetProperty("detail").GetString();
    var look = crewCatalog.Resolve(JsonSerializer.Serialize(new { bodyType = fixture.GetProperty("variant").GetString()!.StartsWith("f") ? "female" : "male",
        skin = Hex("skin"), hair = Hex("hair"), eyes = Hex("eye"), expression = state.GetProperty("expression").GetString(),
        faceAge = age == "lines" ? "mature" : age == "older" ? "elder" : "adult", faceDetail = detail == "scars" ? "scar" : detail }), new Dictionary<string, string>());
    var atlas = new CrewFaceAtlas(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, face.GetProperty("json").GetString()!)),
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, face.GetProperty("png").GetString()! + ".bin")));
    var pixels = atlas.Compose(look, state.GetProperty("expression").GetString(), state.TryGetProperty("blinkEyes", out var blink) ? blink.GetString() : null);
    Require(Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant() == fixture.GetProperty("sha256").GetString(), "Native face tint/layers/blink differs from the source browser pixels");
}
Console.WriteLine("Released crew32-joint/244-clip bank matches11 independent samples; male/female face tint/marks/age/blink pixels match browser hashes.");

void VerifyCrewPose(CrewBonePose[] actual, JsonElement expected, string context)
{
    for (var index = 0; index < animationBank.BoneNames.Length; index++)
    {
        var bone = expected.GetProperty(animationBank.BoneNames[index]);
        System.Numerics.Vector3 V(string field) { var v = bone.GetProperty(field); return new(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle()); }
        var q = bone.GetProperty("rotation"); var rotation = new System.Numerics.Quaternion(q[0].GetSingle(), q[1].GetSingle(), q[2].GetSingle(), q[3].GetSingle());
        Require(System.Numerics.Vector3.Distance(actual[index].Position, V("position")) <= 1e-4 &&
            System.Numerics.Vector3.Distance(actual[index].Scale, V("scale")) <= 1e-4 &&
            1 - Math.Abs(System.Numerics.Quaternion.Dot(actual[index].Rotation, rotation)) <= 1e-4,
            "Source crew kinematics differ: " + context + "/" + animationBank.BoneNames[index]);
    }
}
var planting = new CrewFootPlanting();
var kinematics = crewCatalog.Semantics.GetProperty("kinematicsGoldens").EnumerateArray().ToArray();
var stepIndex = 0;
foreach (var input in crewCatalog.Semantics.GetProperty("footSteps").EnumerateArray())
{
    var clip = input.GetProperty("clip").GetString()!;
    var poseSample = animationBank.Sample(clip, input.GetProperty("time").GetDouble(), true);
    var p = input.GetProperty("position");
    var matrix = System.Numerics.Matrix4x4.CreateScale(.9f) * System.Numerics.Matrix4x4.CreateRotationY(input.GetProperty("yaw").GetSingle()) *
        System.Numerics.Matrix4x4.CreateTranslation(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
    planting.Step(animationBank, poseSample, matrix, input.GetProperty("dt").GetDouble(), input.GetProperty("plant").GetBoolean());
    foreach (var golden in kinematics.Where(g => g.GetProperty("kind").GetString() == "feet" && g.GetProperty("step").GetInt32() == stepIndex))
    {
        VerifyCrewPose(poseSample, golden.GetProperty("bones"), "stance step" + stepIndex);
        Require(Math.Abs(planting.Error - golden.GetProperty("error").GetDouble()) <= 1e-4, "Foot planting error differs from source at step" + stepIndex);
    }
    stepIndex++;
}
Console.WriteLine("Source stance planting matches124 sequential inputs and independently solved32-bone snapshots.");

foreach (var fixture in kinematics.Where(g => g.GetProperty("kind").GetString() == "seat"))
{
    var poseSample = animationBank.Sample(fixture.GetProperty("clip").GetString()!, fixture.GetProperty("time").GetDouble(), true);
    var contact = fixture.GetProperty("contact");
    double? Optional(string key) => contact.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    CrewKinematics.Seat(animationBank, poseSample, .9, contact.GetProperty("lift").GetDouble(), contact.GetProperty("lean").GetDouble(),
        contact.GetProperty("footSupport").GetDouble(), contact.GetProperty("forward").GetDouble(), Optional("footForward"));
    VerifyCrewPose(poseSample, fixture.GetProperty("bones"), "canonical seat");
}

using (var environment = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "environment-manifest.json"))))
using (var goldens = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "space-golden.json"))))
{
    Require(environment.RootElement.GetProperty("sourceRevision").GetString() == goldens.RootElement.GetProperty("sourceRevision").GetString(), "Browser/native space goldens came from another source revision");
    var stars = SpaceMath.Stars(); var reference = environment.RootElement.GetProperty("stars");
    Require(stars.Count == 8192 && reference.GetArrayLength() == stars.Count, "Distant star catalogue identity/count changed");
    for (var i = 0; i < stars.Count; i++)
    {
        var expected = reference[i]; var actual = stars[i];
        var direction = expected.GetProperty("direction"); var color = expected.GetProperty("color");
        Require(new[] { actual.X - direction[0].GetDouble(), actual.Y - direction[1].GetDouble(), actual.Z - direction[2].GetDouble(),
            actual.Red - color[0].GetDouble(), actual.Green - color[1].GetDouble(), actual.Blue - color[2].GetDouble(),
            actual.AngularRadius - expected.GetProperty("angularRadius").GetDouble() }.All(error => Math.Abs(error) <= 1e-10), "Stable native distant star differs from the pinned browser catalogue");
    }
    double Number(JsonElement e, string property) => e.GetProperty(property).GetDouble();
    void Near(double actual, double expected, string detail) => Require(double.IsFinite(actual) && Math.Abs(actual - expected) <= 1e-8, "Browser/native space rule differs: " + detail);
    foreach (var fixture in goldens.RootElement.GetProperty("dustLayout").EnumerateArray())
    {
        var actual = SpaceMath.DustLayout(Number(fixture, "half"), Number(fixture, "aspect")); var expected = fixture.GetProperty("expected");
        Require(actual.Columns == Number(expected, "columns") && actual.Rows == Number(expected, "rows") && actual.Count == Number(expected, "count") && actual.Spacing == Number(expected, "spacing"), "Dust grid/adaptive spacing differs from the browser");
    }
    foreach (var fixture in goldens.RootElement.GetProperty("dustCell").EnumerateArray())
    {
        var actual = SpaceMath.DustCell((int)Number(fixture, "index"), Number(fixture, "x"), Number(fixture, "y"), Number(fixture, "spacing")); var expected = fixture.GetProperty("expected");
        Near(actual.X, Number(expected, "x"), "dust X/origin precision"); Near(actual.Y, Number(expected, "y"), "dust Y/origin precision");
        Near(actual.Height, Number(expected, "height"), "dust depth"); Near(actual.Size, Number(expected, "size"), "dust size"); Near(actual.LengthVariation, Number(expected, "lengthVariation"), "dust variant");
    }
    foreach (var fixture in goldens.RootElement.GetProperty("dustMotion").EnumerateArray())
    {
        var actual = SpaceMath.DustMotion(Number(fixture, "vx"), Number(fixture, "vy"), fixture.GetProperty("reduced").GetBoolean()); var expected = fixture.GetProperty("expected");
        Near(actual.Speed, Number(expected, "speed"), "dust speed"); Near(actual.Length, Number(expected, "length"), "dust exposure");
        Near(actual.StreakRatio, Number(expected, "streakRatio"), "dust streak"); Near(actual.WarpBlend, Number(expected, "warpBlend"), "dust travel response");
        Near(actual.Intensity, Number(expected, "intensity"), "dust intensity"); Near(actual.Heading, Number(expected, "heading"), "dust direction");
    }
    foreach (var fixture in goldens.RootElement.GetProperty("background").EnumerateArray())
    {
        var point = fixture.GetProperty("point"); var actual = SpaceMath.BackgroundWeights(fixture.GetProperty("region").GetRawText(), Number(point, "x"), Number(point, "y"), Number(point, "height"));
        var expected = fixture.GetProperty("expected").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => Number(e, "weight"));
        Require(actual.Keys.Order().SequenceEqual(expected.Keys.Order()), "Scape ancestor clipping selected another vista");
        foreach (var (id, weight) in expected) Near(actual[id], weight, "scape weight/ancestor clipping");
    }
    Console.WriteLine("All8192 pinned stars, browser dust at ±1e12 metre origins, reduced-motion response and nested scape weights passed.");
    using var effectGoldens = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "environment-golden.json")));
    foreach (var fixture in effectGoldens.RootElement.GetProperty("eruptions").EnumerateArray())
    {
        var actual = SpaceMath.StellarEruption(Number(fixture, "time")); var expected = fixture.GetProperty("result");
        Near(actual.Angle, Number(expected, "angle"), "source stellar event angle");
        Near(actual.Progress, Number(expected, "progress"), "source stellar event progress");
        Near(actual.Strength, Number(expected, "strength"), "source stellar event strength");
    }
    foreach (var fixture in effectGoldens.RootElement.GetProperty("depthLayers").EnumerateArray())
    {
        SpaceMath.Point3 Point(string key) { var p = fixture.GetProperty(key); return new(Number(p,"x"), Number(p,"y"), Number(p,"z")); }
        var actual = SpaceMath.DustDepthLayers(Point("camera"), Point("target"), SpaceMath.FieldOfView, Number(fixture,"aspect"));
        var expected = fixture.GetProperty("result"); Require(actual.Length == expected.GetArrayLength(), "Source dust depth strata count changed");
        for (var index = 0; index < actual.Length; index++)
        {
            var a=actual[index]; var e=expected[index]; var center=e.GetProperty("center");
            Near(a.Height,Number(e,"height"),"dust stratum height"); Near(a.CenterX,Number(center,"x"),"dust frustum X"); Near(a.CenterZ,Number(center,"z"),"dust frustum Z");
            Near(a.DepthDistance,Number(e,"depthDistance"),"dust stratum distance"); Near(a.HalfX,Number(e,"halfX"),"dust frustum width"); Near(a.HalfZ,Number(e,"halfZ"),"dust frustum depth");
            Near(a.Thickness,Number(e,"thickness"),"dust stratum thickness"); Near(a.SizeScale,Number(e,"sizeScale"),"dust depth size");
        }
    }
    Require(SpaceMath.DustDepthLayers(new(0,1,0),new(0,2,0),.5,1).Length==0 &&
        SpaceMath.DustDepthLayers(new(double.NaN,1,0),new(0,0,0),.5,1).Length==0,"Invalid or upward-facing camera produced dust layers");
    Console.WriteLine("Source stellar event phases and perspective/frustum dust strata match independent pinned renderer outputs.");
}

InventoryAttemptTests.Run();
InventoryInteractionTests.Run();
OwnedMotionTests.Run(Path.Combine(AppContext.BaseDirectory, "owned-motion-golden.json"));
OwnedMotionTests.RunYaw(Path.Combine(AppContext.BaseDirectory, "owned-yaw-golden.json"));

var worldCatalog = new ReplicatedWorldCatalog(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "world-manifest.json")));
var combatFx = new CombatEffectCatalog(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "combat-manifest.json")));
using (var combatGoldens = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"combat-golden.json"))))
{
    var root=combatGoldens.RootElement;
    Require(root.GetProperty("sourceCommit").GetString()==CombatEffectCatalog.SourceRevision,"Combat comparison uses a different source revision");
    void FxNear(double actual,double expected,string field)=>Require(double.IsFinite(actual)&&Math.Abs(actual-expected)<1e-8,"Source combat FX differs: "+field);
    CombatPoint3 FxPoint(JsonElement p)=>new(p[0].GetDouble(),p[1].GetDouble(),p[2].GetDouble());
    void FxVector(CombatPoint3 actual,JsonElement expected,string field)
    {FxNear(actual.X,expected[0].GetDouble(),field+" X");FxNear(actual.Y,expected[1].GetDouble(),field+" Y");FxNear(actual.Z,expected[2].GetDouble(),field+" Z");}
    foreach(var fixture in root.GetProperty("sampler").EnumerateArray())
    {
        var id=fixture.GetProperty("fx").GetString()!;var sample=combatFx.Effect(id)!.Sample(fixture.GetProperty("t").GetDouble());
        FxVector(sample.Scale,fixture.GetProperty("scale"),id+" sampler scale");
        FxNear(sample.Opacity,fixture.GetProperty("opacity").GetDouble(),id+" opacity");FxNear(sample.Emissive,fixture.GetProperty("emissive").GetDouble(),id+" emission");
        Require(sample.Finished==fixture.GetProperty("finished").GetBoolean(),"Combat sampler lifetime differs: "+id);
    }
    foreach(var fixture in root.GetProperty("tints").EnumerateArray())
    {
        var tint=combatFx.Tint(fixture.GetProperty("item").GetString(),fixture.GetProperty("fx").GetString()!);var expected=fixture.GetProperty("tint");
        Require(tint.HasValue==(expected.ValueKind!=JsonValueKind.Null),"Source item FX tint availability differs");
        if(tint.HasValue)FxVector(tint.Value,expected,"item FX tint");
    }
    foreach(var fixture in root.GetProperty("timeline").EnumerateArray())
    {
        var definition=combatFx.Effect(fixture.GetProperty("fx").GetString()!)!;var from=FxPoint(fixture.GetProperty("from"));
        var to=fixture.GetProperty("to").ValueKind==JsonValueKind.Null?(CombatPoint3?)null:FxPoint(fixture.GetProperty("to"));
        double? Optional(string key)=>fixture.GetProperty(key).ValueKind==JsonValueKind.Null?null:fixture.GetProperty(key).GetDouble();
        var timeline=new CombatFxTimeline(definition,fixture.GetProperty("size").GetDouble(),Optional("lengthM"),to.HasValue?(to.Value-from).Length:null,Optional("holdS"));
        foreach(var frame in fixture.GetProperty("frames").EnumerateArray())
        {
            var sample=timeline.Step(frame.GetProperty("delta").GetDouble());var live=frame.GetProperty("live").GetBoolean();
            Require(sample.Finished==!live,"Source first-frame hold/render lifetime differs: "+definition.Id);
            if(!live)continue;
            FxVector(sample.Scale,frame.GetProperty("scale"),definition.Id+" rendered scale");
            FxVector(to.HasValue?CombatPoint3.Lerp(from,to.Value,timeline.TravelFraction):from,frame.GetProperty("position"),definition.Id+" travel");
            foreach(var expected in frame.GetProperty("opacity").EnumerateArray())FxNear(sample.Opacity,expected.GetDouble(),definition.Id+" material opacity");
            foreach(var expected in frame.GetProperty("emissive").EnumerateArray())FxNear(sample.Emissive,expected.GetDouble(),definition.Id+" material emission");
        }
    }
    var legacyOffsets=Enumerable.Repeat(new CombatPoint3(0,0,0),6).ToArray();
    foreach(var fixture in root.GetProperty("legacy").EnumerateArray())
    {
        var age=fixture.GetProperty("age").GetDouble();var sample=CombatEffectProjection.LegacyImpact(age);
        var live=fixture.GetProperty("live").GetBoolean();Require(sample.Finished==!live,"Source legacy impact lifetime differs");
        if(!live)continue;
        FxNear(sample.Scale,fixture.GetProperty("coreScale").GetDouble(),"legacy impact scale");FxNear(sample.Opacity,fixture.GetProperty("opacity").GetDouble(),"legacy opacity");
        var at=new CombatPoint3(2,CombatEffectProjection.LegacyImpactHeight,3);var points=fixture.GetProperty("positions");
        FxVector(at,points[0],"legacy impact height");
        for(var index=0;index<6;index++)
        {legacyOffsets[index]+=CombatEffectProjection.SparkVelocity(index)*Math.Clamp(fixture.GetProperty("delta").GetDouble(),0,.1);FxVector(at+legacyOffsets[index],points[index+1],"legacy capped spark");}
    }
    foreach(var fixture in root.GetProperty("worldProjection").EnumerateArray())
    {
        var ship=fixture.GetProperty("ship");var point=fixture.GetProperty("world");
        FxVector(CombatEffectProjection.WorldToShip(point[0].GetDouble(),point[1].GetDouble(),fixture.GetProperty("height").GetDouble(),
            ship.GetProperty("x").GetDouble(),ship.GetProperty("y").GetDouble(),ship.GetProperty("heading").GetDouble()),fixture.GetProperty("expected"),"double-origin world-to-ship combat");
    }
    foreach(var fixture in root.GetProperty("rotations").EnumerateArray())
    {
        var (axis,angle)=CombatEffectProjection.ForwardRotation(FxPoint(fixture.GetProperty("direction")));
        var expected=fixture.GetProperty("quaternion");var sine=Math.Sin(angle/2);
        FxNear(axis.X*sine,expected[0].GetDouble(),"forward rotation quaternion X");FxNear(axis.Y*sine,expected[1].GetDouble(),"forward rotation quaternion Y");
        FxNear(axis.Z*sine,expected[2].GetDouble(),"forward rotation quaternion Z");FxNear(Math.Cos(angle/2),expected[3].GetDouble(),"forward rotation quaternion W");
    }
    var observer=new CombatActionObserver();
    var action=new CombatActionInput("actor","ship","deck","pistol","rays",7,2,-3,"[[4,3,1],[2,5,0]]",0,0,false,0,4,2);
    Require(observer.Observe(new[]{action}).Events.Count==0,"Historical combat rows replayed on first disclosure");
    var changed=observer.Observe(new[]{action with {ShotSequence=8}});
    Require(changed.Events.Count==1&&changed.Events[0].Kind==CombatVisualEventKind.Shot,"Accepted shot edge did not produce one event");
    Require(observer.Observe(new[]{action with {ShotSequence=8}}).Events.Count==0,"Unchanged accepted combat row repeated an effect");
    Require(observer.Observe(Array.Empty<CombatActionInput>()).RemovedActors.SequenceEqual(new[]{"actor"}),"Disclosure loss retained combat actor state");
    Require(observer.Observe(new[]{action with {ShotSequence=8}}).Events.Count==0,"Redisclosed historical shot was replayed");
    var shot=CombatEffectProjection.Shot(action,.1875);
    FxVector(shot.Origin,root.GetProperty("calls")[0].GetProperty("origin"),"source fallback muzzle");
    FxVector(shot.Direction,root.GetProperty("calls")[0].GetProperty("direction"),"source shot direction");
    var rays=root.GetProperty("calls")[0].GetProperty("rays");Require(shot.Rays.Count==rays.GetArrayLength(),"Source accepted ray count changed");
    for(var index=0;index<shot.Rays.Count;index++)
    {FxVector(shot.Rays[index].End,rays[index].GetProperty("end"),"source shot endpoint");Require(shot.Rays[index].Struck==rays[index].GetProperty("struck").GetBoolean(),"Source impact disclosure changed");}
    var near=CombatEffectProjection.WorldRenderPoint(1e12+.125,-1e12-.25,1.3,1e12,-1e12);
    FxNear(near.X,.125,"double-origin combat X");FxNear(near.Z,.25,"double-origin combat Z");
    Require(CombatEffectProjection.ParsePoints("[[1,2,1],[3,4],[\"5\",6,1]]").Count==1&&CombatEffectProjection.ParsePoints("invalid").Count==0,"Malformed combat rays were presented");
}
Console.WriteLine("Source combat FX135 samples/420 tints/15 rendered timelines/5 legacy frames/5 f64 projections/7 rotations and accepted history/disclosure tests passed.");
foreach(var response in new[] { .6, .9, 1d, 2.4 })
    Require(Math.Abs(SourceLightUnitsRules.Energy(1.7,response)-1.7*response/Math.PI)<1e-7,"Source directional irradiance conversion differs");
foreach(var invalid in new[] { -1d, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    Reject(()=>SourceLightUnitsRules.Energy(invalid,1),"Invalid source intensity reached native light energy");
    Reject(()=>SourceLightUnitsRules.Energy(1,invalid),"Invalid material direct response reached native light energy");
    Reject(()=>SourceLightUnitsRules.ManualHexChannelToLinear(invalid),"Invalid manual color override accepted");
}
Reject(()=>SourceLightUnitsRules.Energy(double.MaxValue,double.MaxValue),"Overflow light energy accepted");
Reject(()=>SourceLightUnitsRules.ManualHexChannelToLinear(1.001),"Out-of-range manual color override accepted");
Require(SourceLightUnitsRules.Energy(0,1)==0&&SourceLightUnitsRules.ManualHexChannelToLinear(0)==0&&
    SourceLightUnitsRules.ManualHexChannelToLinear(1)==1&&Math.Abs(SourceLightUnitsRules.ManualHexChannelToLinear(.5)-Math.Pow(.5,2.2))<1e-12,"Source manual color power path differs");
foreach(var role in new[] { "floor", "wall", "hull", "prop" })
foreach(var slot in new[] { "primary", "secondary", "trim", "dark", "metal", "glass", "emissive" })
{
    var legacyInterior=role=="floor"&&(slot is "primary" or "secondary" or "trim" or "dark" or "metal")||
        role=="wall"&&(slot is "primary" or "secondary" or "trim");
    Require(SourceLightUnitsRules.ShipDirect(true,role,slot)==.6&&
        SourceLightUnitsRules.ShipDirect(false,role,slot)==(legacyInterior?.9:.6),"Authored material/legacy clone direct-light class differs: "+role+"/"+slot);
}
Console.WriteLine("Source light irradiance, manual-color transform, authored/legacy material classes and finite native range checks passed.");
var skyLutBytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"source-sky-lut-golden.json"));
Require(Convert.ToHexString(SHA256.HashData(skyLutBytes)).ToLowerInvariant()=="de66c1a02fdbfc34ba942d7c85d9025bd20fa3cfc767e6489c5ed063e4bde2ea","Independent source sky lookup fixture pin changed");
using(var skyLookup=JsonDocument.Parse(skyLutBytes))
{
    foreach(var pair in new[]{("vulkan",SourceDisplaySpaceProfile.Vulkan),("gl-material",SourceDisplaySpaceProfile.CompatibilityMaterial),("gl-post",SourceDisplaySpaceProfile.CompatibilityPost)})
    {
        var expected=skyLookup.RootElement.GetProperty(pair.Item1);var actual=SourceDisplaySpaceRules.Lookup(pair.Item2);
        Require(expected.GetArrayLength()==256,"Source sky fixture omitted output codes");
        for(var code=0;code<actual.Length;code++)Require(BitConverter.SingleToInt32Bits(actual[code])==BitConverter.SingleToInt32Bits((float)expected[code].GetDouble()),"Native sky R32F input differs at "+pair.Item1+"/"+code);
    }
}
var skyContext=new SourceDisplaySpaceContext("gl_compatibility","opengl3",true,true,.97,1,1,false);
Require(SourceDisplaySpaceRules.Evaluate(skyContext).Profile==SourceDisplaySpaceProfile.CompatibilityMaterial&&
    SourceDisplaySpaceRules.Evaluate(skyContext with{Glow=true}).Profile==SourceDisplaySpaceProfile.CompatibilityPost&&
    SourceDisplaySpaceRules.Evaluate(skyContext with{RenderScale=.75}).Profile==SourceDisplaySpaceProfile.CompatibilityPost&&
    SourceDisplaySpaceRules.Evaluate(skyContext with{Renderer="forward_plus",Driver="vulkan",Glow=true,RenderScale=.75}).Profile==SourceDisplaySpaceProfile.Vulkan,"Native sky engine profile selection differs");
foreach(var unsupported in new[]{skyContext with{EnginePinned=false},skyContext with{Filmic=false},skyContext with{Exposure=1},skyContext with{White=2},
    skyContext with{RenderScale=double.NaN},skyContext with{RenderScale=.49},skyContext with{Ssao=true},skyContext with{NativeAdjustments=true},
    skyContext with{CanvasBackground=true},skyContext with{Hdr2D=true},skyContext with{CameraEffects=true},skyContext with{Fog=true},
    skyContext with{TransparentBackground=true},skyContext with{SupportedScaler=false},skyContext with{Driver="unsupported"}})
    Require(!SourceDisplaySpaceRules.Evaluate(unsupported).Qualified,"Unsupported sky pipeline silently claimed source output equivalence");
Reject(()=>SourceDisplaySpaceRules.NativeInput(-1,SourceDisplaySpaceProfile.Vulkan),"Invalid sky output code accepted");
Reject(()=>SourceDisplaySpaceRules.NativeInput(256,SourceDisplaySpaceProfile.Vulkan),"Out-of-range sky output code accepted");
Reject(()=>SourceDisplaySpaceRules.Lookup(SourceDisplaySpaceProfile.Unqualified),"Unqualified output generated a sky LUT");
Console.WriteLine("All768 sky R32F lookup entries match independent source/native expectations; active engine profile and unsupported-output guards pass.");
var cameraGoldenBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "camera-golden.json"));
Require(Convert.ToHexString(SHA256.HashData(cameraGoldenBytes)).ToLowerInvariant() == worldCatalog.Root.GetProperty("cameraGolden").GetProperty("sha256").GetString(),
    "Camera comparison fixture changed without updating its immutable pin");
using (var cameraGoldens = JsonDocument.Parse(cameraGoldenBytes))
{
    Require(cameraGoldens.RootElement.GetProperty("sourceRevision").GetString() == worldCatalog.Root.GetProperty("sourceRevision").GetString(), "Camera fixtures use another browser source");
    var fixtures=cameraGoldens.RootElement.GetProperty("fixtures");
    Require(fixtures.GetArrayLength() == worldCatalog.Root.GetProperty("ships").GetArrayLength(), "Camera fixtures omitted a bundled current or historical ship");
    foreach(var fixture in fixtures.EnumerateArray())
    {
        var ship=worldCatalog.Root.GetProperty("ships").EnumerateArray().Single(s=>s.GetProperty("prefabSha256").GetString()==fixture.GetProperty("prefabSha256").GetString());
        var frame=ReplicatedWorldCatalog.ReadCameraFrame(ship,fixture.GetProperty("deckId").GetString());
        void CameraNear(double actual,string key)=>Require(Math.Abs(actual-fixture.GetProperty(key).GetDouble())<1e-10,"Camera deck-frame/source zoom mismatch: "+fixture.GetProperty("prefabId").GetString()+"/"+key);
        CameraNear(frame.CenterX,"centerX"); CameraNear(frame.CenterY,"centerY");CameraNear(frame.HalfExtent,"halfExtent");
        CameraNear(frame.InitialDeckZoom,"initialDeckZoom");CameraNear(frame.InitialFlightZoom,"initialFlightZoom");
    }
    foreach(var fixture in cameraGoldens.RootElement.GetProperty("actorWeights").EnumerateArray())
        Require(Math.Abs(SpaceMath.DeckActorWeight(fixture.GetProperty("half").GetDouble(),fixture.GetProperty("overview").GetDouble())-fixture.GetProperty("expected").GetDouble())<1e-10,"Camera actor-follow weight differs from source overview");
    foreach(var fixture in cameraGoldens.RootElement.GetProperty("zoomEasing").EnumerateArray())
        Require(Math.Abs(SpaceMath.Ease(fixture.GetProperty("current").GetDouble(),fixture.GetProperty("target").GetDouble(),fixture.GetProperty("dt").GetDouble())-fixture.GetProperty("expected").GetDouble())<1e-10,"Camera zoom easing differs from source");
}
Console.WriteLine("All20 canonical deck frames/default zooms and source-executed follow/zoom math match browser outputs.");
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
var gameplayProbe = args.Length == 3 && args[2] == "--gameplay-probe";
Require(args.Length == 2 || gameplayProbe, "Native smoke requires an explicit isolated endpoint and database; optional --gameplay-probe uses the pinned browser source planner");
var settings = new ClientSettings(args[0], args[1], "https://auth.dastari.net/realms/dastari", "sidereal-game", 43817);
Require(settings.IsIsolatedFixture, "Native smoke requires the isolated local :3131 or Tailscale :8448 endpoint and a -smoke database");
using var client = new ClientCore(settings);
var clients = new List<ClientCore> { client };
void Wait(Func<bool> ready, string message)
{
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (!ready() && DateTime.UtcNow < deadline) { foreach (var c in clients) c.Tick(); Thread.Sleep(10); }
    if (!ready() && message.StartsWith("Accepted drop", StringComparison.Ordinal))
        Console.WriteLine(JsonSerializer.Serialize(new { pending = client.InventoryPending, feedback = client.InventoryMessage,
            itemCarried = client.Inventory.Items.Any(i => i.DefinitionId == "power-cell"), ground = client.Connection?.Db.OwnGroundItems.Iter().Select(row => new {
                row.DefinitionId, row.Reachable, row.ElevationM, sameInstance = row.InstanceId == client.Location?.InstanceId, sameDeck = row.DeckId == client.Location?.DeckId }),
            actorState = client.Vitals?.State, pilot = client.IsPiloting, resting = client.Resting }));
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
Wait(() => client.SpatialReady, "The acknowledged nine-cell shared observer scope did not become ready");
Require(client.Connection!.Db.OwnWorldAdmission.Iter().Any(a => a.CharacterId == uuid && a.ShipId == client.Character.ShipId), "Starter boarding did not establish matching shared admission");
Require(client.SpatialCellSets == 1 && client.Connection.Db.VisibleBodyDescriptions.Iter().Count(b => b.Kind is "planet" or "star") == 29,
    "Shared observer did not receive the bounded canonical celestial chart");
Require(!client.CanReturnFromReview && client.Instance != null, "Normal physical owned deck was mistaken for a temporary construction review");
Console.WriteLine("New character boarding preserves the private owned deck and supplies acknowledged shared scope with all29 authoritative celestial identities.");
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
// Source-backed feature paths, all on the designated isolated fixture.
var floorCandidate = client.Inventory.Items.First(i => i.ContainerId.Length > 0 && i.DefinitionId == "power-cell");
var floorMass = client.Inventory.CarriedMassKg;
Require(client.DropItem(floorCandidate.Id, client.Inventory.Revision), "Explicit floor drop did not dispatch");
Wait(() => !client.InventoryPending && client.Inventory.Item(floorCandidate.Id) is { } dropped && client.Inventory.Container(dropped.ContainerId)?.Carried == false &&
    client.Connection!.Db.OwnGroundItems.Iter().Any(row => row.Id == floorCandidate.Id && row.Reachable), "Accepted drop did not leave carried inventory and enter the scoped floor view");
var droppedContainer = client.Inventory.Container(client.Inventory.Item(floorCandidate.Id)!.ContainerId)!;
var disclosedGroundContainer = client.Connection!.Db.OwnInventoryContainers.Iter().FirstOrDefault(row => row.Id == droppedContainer.Id);
Require(disclosedGroundContainer != null && disclosedGroundContainer.PlacementId.StartsWith("ground:", StringComparison.Ordinal) &&
    droppedContainer.PlacementId == disclosedGroundContainer.PlacementId, "Native inventory projection discarded the disclosed ground wrapper placement identity");
Console.WriteLine("Native ground storage retains the exact disclosed placement identity for routing and attempt correlation.");
Require(client.TakeGroundItem(floorCandidate.Id), "Reachable floor pickup did not dispatch the supported transfer transaction");
Wait(() => !client.InventoryPending && client.Inventory.Item(floorCandidate.Id) is { } picked && client.Inventory.Container(picked.ContainerId)?.Carried == true &&
    !client.Connection!.Db.OwnGroundItems.Iter().Any(row => row.Id == floorCandidate.Id), "Accepted pickup did not reconcile carried/floor views");
Require(Math.Abs(client.Inventory.CarriedMassKg - floorMass) < .0001, "Drop/pickup changed item mass or identity");
Wait(() => client.Appearance != null, "Actor-filtered saved appearance did not arrive");
var oldAppearance = client.Appearance!.AppearanceJson; var oldAppearanceRevision = client.Appearance.Revision;
var appearanceProposal = JsonNode.Parse(oldAppearance)!.AsObject(); appearanceProposal["expression"] = "determined";
Require(client.SaveAppearance(appearanceProposal.ToJsonString()), "Saved appearance proposal was not dispatched");
Wait(() => !client.GameplayPending && client.Appearance?.Revision > oldAppearanceRevision, "Appearance change was not confirmed by its current revision");
Require(JsonNode.Parse(client.Appearance!.AppearanceJson)!["expression"]!.GetValue<string>() == "determined", "Appearance UI was not backed by accepted server data");
var changedAppearanceRevision = client.Appearance.Revision;
Require(client.SaveAppearance(oldAppearance), "Restoring saved appearance did not dispatch");
Wait(() => !client.GameplayPending && client.Appearance?.Revision > changedAppearanceRevision, "Saved appearance restoration did not settle");
var firearm = client.Inventory.Items.First(i => i.DefinitionId == "compact-pistol");
Require(client.EquipItem(firearm.Id, client.Inventory.Revision), "Combat hand equip did not dispatch");
Wait(() => !client.InventoryPending && client.Combat?.WeaponItemId == firearm.Id, "Equipped weapon did not reach scoped combat state");
if (!client.CombatEnabled) client.ToggleCombat();
client.TickGameplay(.02, true, true); client.SetAimAngle(0); client.SetTrigger(true); client.SetTrigger(false);
var beforeShot = client.Combat!.ShotSequence;
var shotDeadline = DateTime.UtcNow.AddSeconds(10);
while (client.Combat.ShotSequence == beforeShot && DateTime.UtcNow < shotDeadline)
{ foreach (var c in clients) c.Tick(); client.TickGameplay(.02, true, true); Thread.Sleep(20); }
Require(client.Combat.ShotSequence > beforeShot && Math.Abs(client.Combat.LastShotAngle) < .01, "Short click did not fire after acknowledged clockwise aim");
var acceptedCombat = client.Connection!.Db.VisibleCombatActions.Iter().FirstOrDefault(row => row.CharacterId == uuid);
Require(acceptedCombat != null && client.Combat.Energy < client.Combat.Capacity, "Accepted short shot did not disclose a depleted weapon");
var beforeReload = acceptedCombat!.ReloadSequence;
Require(client.ReloadWeapon(), "Native R eligibility blocked the exact source pinned/revision-one reload");
Wait(() => !client.GameplayPending && client.Connection.Db.VisibleCombatActions.Iter().Any(row => row.CharacterId == uuid && row.ReloadSequence > beforeReload), "Accepted reload sequence did not arrive");
var reloadShot = client.Combat!.ShotSequence;
client.SetTrigger(true); client.SetTrigger(false);
var reloadWait = DateTime.UtcNow.AddMilliseconds(400);
while (DateTime.UtcNow < reloadWait) { foreach (var c in clients) c.Tick(); client.TickGameplay(.02, true, true); Thread.Sleep(10); }
Require(client.Combat.ShotSequence == reloadShot, "Native short click fired during the accepted reload interval");
Wait(() => client.Connection.Db.VisibleCombatActions.Iter().First(row => row.CharacterId == uuid).ReloadUntilMicros <= (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()*1000 &&
    client.Combat.Energy == client.Combat.Capacity, "Accepted reload did not finish/refill the exact weapon");
Console.WriteLine("Exact source weapon reload dispatch, accepted sequence/refill, and refusal of firing during reload passed over the isolated endpoint.");
// A real software-rendered frame can exceed the server's 300ms aim lifetime.
// Preserve a short click through that pause, and prove only server-accepted rows count.
client.TickGameplay(.02,true,true);client.SetAimAngle(.27);client.SetTrigger(true);client.SetTrigger(false);
var slowShot=client.Combat!.ShotSequence;
var slowDeadline=DateTime.UtcNow.AddSeconds(12);
while(client.Combat.ShotSequence==slowShot && DateTime.UtcNow<slowDeadline)
{ Thread.Sleep(650);foreach(var c in clients)c.Tick();client.TickGameplay(.65,true,true); }
if(client.Combat.ShotSequence==slowShot || Math.Abs(client.Combat.LastShotAngle-.27)>=.01)
{
    var debugNames=new[]{"aimPending","acceptedAimActive","aimSerial","acceptedAimSerial","lastAimAt","pendingTrigger","triggerHeld","keyboardAllowed","pointerAllowed"};
    var debug=debugNames.ToDictionary(name=>name,name=>typeof(ClientCore).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)?.GetValue(client));
    Console.WriteLine(JsonSerializer.Serialize(new {slowAimFailure=true,client.CombatEnabled,client.Alive,client.InteriorView,client.GameplayMessage,client.GameplayEpoch,
        actualShot=client.Combat.ShotSequence,beforeShot=slowShot,angle=client.Combat.LastShotAngle,client.GameplayPending,client.Status,debug}));
}
Require(client.Combat.ShotSequence>slowShot && Math.Abs(client.Combat.LastShotAngle-.27)<.01,
    "A >300ms native frame lost the short click or fired before refreshed server aim");
Require(!client.GameplayMessage.Contains("Aim intent expired",StringComparison.Ordinal),"Slow-frame shot reused expired aim");
Console.WriteLine("650ms network pump intervals preserve a short click and fresh accepted aim/shot without client prediction.");


client.CancelGameplayInput(); client.ToggleCombat();
Console.WriteLine("Explicit drop/pickup preserves item UUID/mass; saved appearance uses real revisions; a short combat click fires after acknowledged aim.");
if (gameplayProbe) GameplayNetworkProbe.Run(client, Wait);
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
