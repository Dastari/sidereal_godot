using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Sidereal.Native;
using Sidereal.Native.Input;

// Optional Linux source audit: Tests <isolated-origin> <database-smoke> --gameplay-probe.
// SIDEREAL_BROWSER_SOURCE must be an already installed, clean checkout of SourceRevision.
// Ordinary SDK/Windows tests do not invoke Node or need this checkout. The source helper only
// plans proposed walking routes; all movement, seating, inventory and EVA remain server owned.
public static class GameplayNetworkProbe
{
    private const string SourceRevision = "a35632deedf210cf43f64c43cb741d841f4a1ce0";
    private readonly record struct Point(double X, double Y)
    {
        public static Point Read(JsonElement value) => new(value[0].GetDouble(), value[1].GetDouble());
        public double Distance(Point other) => Math.Sqrt(Math.Pow(X - other.X, 2) + Math.Pow(Y - other.Y, 2));
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Gameplay network probe: " + message); }

    public static void Run(ClientCore client, Action<Func<bool>, string> wait)
    {
        Require(client.Character is { Connected: true } && client.Instance != null && client.Location != null && client.Alive && !client.IsPiloting && !client.Resting && client.Eva == null,
            "an accepted standing actor and private current deck are required");
        var source = Environment.GetEnvironmentVariable("SIDEREAL_BROWSER_SOURCE") ?? "/root/sidereal-worktrees/shadow-transform-refresh";
        Require(Execute("git", source, "rev-parse", "HEAD").Trim() == SourceRevision, "the browser source revision differs from the recorded immutable pin");
        Require(Execute("git", source, "status", "--porcelain", "--", "packages/sim", "packages/content", "packages/world", "package-lock.json").Trim().Length == 0,
            "the pinned browser collision/content/world planner or dependency lock has local modifications");
        var sourceRunner = Path.Combine(source, "node_modules", ".bin", "tsx");
        Require(File.Exists(sourceRunner), "use the existing locked browser dependencies; this probe never installs packages");
        var temporary = Path.Combine(Path.GetTempPath(), "sidereal-native-gameplay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var helper = Path.Combine(temporary, "source-route.mts"); File.WriteAllText(helper, SourcePlanner);
        var initialActorId = client.Character!.Id; var initialShipId = client.Character.ShipId;
        var initialPosition = new Point(client.Character.LocalX, client.Character.LocalY);
        var initialEquipment = client.Inventory.Items.Where(item => item.EquipmentSlot.Length > 0).ToArray();
        var originalInventory = client.Inventory.Items.ToArray();
        var initialMass = client.Inventory.CarriedMassKg;
        var initialView = client.InteriorView;
        var initialPower = client.Connection!.Db.OwnAuthoredFlightPowerFittings.Iter().Where(row => row.ShipId == initialShipId).Select(row => (row.PlacedObjectId, row.Kind, row.Powered)).ToArray();
        string? suitLocker = null;
        var suitItems = new List<InventoryItemView>();
        var parked = new List<InventoryItemView>();
        bool completed = false;

        JsonElement SourcePlan(string mode, Point? target = null)
        {
            Require(client.Instance != null && client.Location != null, "source navigation requires the current disclosed private scene");
            var snapshot = new {
                mode, source, shipId = client.Instance!.Id, deckId = client.Location!.DeckId,
                documentJson = client.Instance.DocumentJson, furnishingsJson = client.Instance.FurnishingsJson, blueprintSha256 = client.Instance.BlueprintSha256,
                start = new[] { client.Character!.LocalX, client.Character.LocalY },
                target = target.HasValue ? new[] { target.Value.X, target.Value.Y } : null,
                doors = client.Connection!.Db.OwnConstructionDoors.Iter().Where(row => row.InstanceId == client.Instance.Id && row.DeckId == client.Location.DeckId).Select(row => new { id = row.Id, fraction = row.Fraction }),
                logicStates = client.Connection.Db.VisibleShipLogic.Iter().Where(row => row.ShipId == client.Instance.Id).Select(row => new { deviceId = row.DeviceId, open = row.Open })
            };
            return JsonDocument.Parse(Execute(sourceRunner, source, new[] { "--tsconfig", Path.Combine(source, "tsconfig.json"), helper }, JsonSerializer.Serialize(snapshot))).RootElement.Clone();
        }
        void Tick(double forward = 0, double horizontal = 0, double dx = 0, double dy = 0, bool shift = false)
        {
            client.Tick(); client.TickGameplay(.02, true, true);
            client.ClaimControls(); client.SendGameplayIntent(forward, horizontal, dx, dy, shift);
        }
        void PumpFor(double seconds, double forward = 0, double horizontal = 0, double dx = 0, double dy = 0, bool shift = false)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed.TotalSeconds < seconds) { Tick(forward, horizontal, dx, dy, shift); Thread.Sleep(20); }
        }
        void Claim()
        { client.TickGameplay(.02, true, true); client.ClaimControls(); wait(() => client.ControlsClaimed, "Gameplay probe input lease did not become acknowledged"); }
        void Stop()
        { client.CancelGameplayInput(); client.TickGameplay(.02, true, true); PumpFor(.15); client.CancelGameplayInput(); }
        void DrainRelease()
        {
            client.CancelGameplayInput();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < .25) { client.Tick(); Thread.Sleep(10); }
            Require(!client.ControlsClaimed, "explicit interaction regression must begin with a released lease");
        }
        void WaitInteraction(Func<bool> accepted, string message, bool keyboard = true, bool uiFocus = true)
        { wait(() => { client.TickGameplay(.02, keyboard, keyboard, uiFocus); return !client.GameplayPending && accepted(); }, message); }
        void Walk(Point target)
        {
            client.SetInteriorView(true); Claim();
            var plan = SourcePlan("route", target);
            Require(plan.GetProperty("reachable").GetBoolean(), "browser collision planner found no route to " + target);
            var points = plan.GetProperty("points").EnumerateArray().Select(Point.Read).ToArray();
            var timer = Stopwatch.StartNew();
            foreach (var point in points)
            {
                while (new Point(client.Character!.LocalX, client.Character.LocalY).Distance(point) > .13)
                {
                    Require(timer.Elapsed.TotalSeconds < 25 && client.Eva == null && !client.Resting && !client.IsPiloting && client.Alive,
                        "walking did not reach the next source waypoint " + point + "; accepted pose=" + client.Character.LocalX + "," + client.Character.LocalY);
                    var dx = point.X - client.Character.LocalX; var dy = point.Y - client.Character.LocalY;
                    var distance = Math.Sqrt(dx * dx + dy * dy); var speed = Math.Min(1, distance / .3);
                    Tick(dx: dx / distance * speed, dy: dy / distance * speed); Thread.Sleep(20);
                }
            }
            PumpFor(.08); client.SendIntent(0, 0, 0, 0, false);
            Require(new Point(client.Character!.LocalX, client.Character.LocalY).Distance(target) < .3, "accepted walking pose differs from the source destination");
        }
        void Mutate(Func<bool> dispatch, Func<bool> accepted, string label)
        {
            Claim(); Require(dispatch(), label + " did not dispatch");
            wait(() => !client.GameplayPending && accepted(), label + " did not produce its accepted subscribed state: " + client.GameplayMessage);
        }
        void RefusePower(Func<bool> dispatch, string label)
        {
            Claim(); var revision = client.Flight!.Revision;
            Require(dispatch(), label + " rejection probe did not dispatch");
            wait(() => !client.GameplayPending, label + " reducer receipt did not settle");
            Require(client.GameplayMessage.StartsWith("Rejected:", StringComparison.Ordinal), label + " did not return a rejected reducer receipt: " + client.GameplayMessage);
            Require(client.Flight!.Revision == revision && initialPower.All(power => client.Connection!.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.PlacedObjectId == power.PlacedObjectId && row.Powered == power.Powered)),
                "Rejected power edit changed fitting state or expected revision");
            Console.WriteLine(label + ": " + client.GameplayMessage);
        }
        void Transfer(string item, string destination)
        {
            Require(client.TransferItem(item, destination, client.Inventory.Revision), "inventory transfer did not dispatch: " + client.InventoryMessage);
            wait(() => !client.InventoryPending && client.Inventory.Item(item)?.ContainerId == destination, "inventory transfer did not reconcile destination: " + client.InventoryMessage);
        }
        void Equip(string item, string slot)
        {
            Require(client.EquipItem(item, client.Inventory.Revision), "equipment command did not dispatch");
            wait(() => !client.InventoryPending && client.Inventory.Item(item)?.EquipmentSlot == slot, "equipment command did not reconcile slot " + slot + ": " + client.InventoryMessage);
        }
        void ReturnToLocker(InventoryItemView item)
        {
            var current = client.Inventory.Item(item.Id);
            if (current?.ContainerId == suitLocker) return;
            // Scoped cargo transfers identify a real source container; they cannot claim an
            // equipped source slot. The ordinary carried transaction stows it first.
            if (current?.EquipmentSlot.Length > 0 || current != null && client.Inventory.Container(current.ContainerId)?.IsScopedCargo != true && client.Inventory.Container(current.ContainerId)?.Carried != true)
            {
                var destination = client.Inventory.Containers.FirstOrDefault(container => container.Carried && client.Inventory.FirstPlacement(item.Id, container.Id).HasValue);
                Require(destination != null, "cleanup needs a real carried cell before returning suit gear");
                Transfer(item.Id, destination!.Id);
            }
            Transfer(item.Id, suitLocker!);
        }
        void Press(JsonElement panel)
        {
            Walk(Point.Read(panel.GetProperty("front")));
            var id = panel.GetProperty("deviceId").GetString()!;
            var before = client.Connection!.Db.VisibleShipLogic.Iter().First(row => row.ShipId == initialShipId && row.DeviceId == id).PressedMicros;
            var action = client.ResolveInteraction();
            Require(action?.Kind == "ship-button" && action.Id == id && action.ShipId == initialShipId, "context E resolved a different action at a source panel");
            Mutate(() => client.Interact(), () => client.Connection!.Db.VisibleShipLogic.Iter().Any(row => row.ShipId == initialShipId && row.DeviceId == id && row.PressedMicros > before), "Contextual wall button " + id);
        }
        JsonElement metadata = default;
        try
        {
            var nativeRoot = Environment.GetEnvironmentVariable("SIDEREAL_NATIVE_SOURCE") ?? Directory.GetCurrentDirectory();
            if (!File.Exists(Path.Combine(nativeRoot, "project.godot"))) nativeRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
            client.LoadGameplayGeometry(File.ReadAllText(Path.Combine(nativeRoot, "Assets/Environment/gameplay-geometry.json")), File.ReadAllText(Path.Combine(nativeRoot, "Assets/World/manifest.json")));
            metadata = SourcePlan("metadata");
            Console.WriteLine("Gameplay network source " + SourceRevision + "; actor document " + metadata.GetProperty("documentSha256").GetString() + "; ship " + metadata.GetProperty("prefabId").GetString() + " r" + metadata.GetProperty("prefabRevision").GetInt32());
            var helm = Point.Read(metadata.GetProperty("helmApproach")); Walk(helm);
            Require(client.NearStation && client.Flight is { Active: true, FlightAdmitted: true }, "source helm approach is not an admitted reachable station");
            var epoch = client.GameplayEpoch;
            // Fresh E after focus/menu release must work without a prior WASD or test claim.
            DrainRelease(); client.TickGameplay(.02, true, true);
            Require(client.InteractSelected(client.Flight!.StationId) && client.GameplayPending, "cancel regression did not queue an unacknowledged first E");
            client.CancelGameplayInput();
            DrainRelease();
            Require(!client.IsPiloting && !client.GameplayPending, "canceled first E survived a delayed control ACK");
            client.TickGameplay(.02, true, true);
            Require(client.InteractSelected(client.Flight!.StationId) && client.GameplayPending, "focus regression did not queue first E");
            client.TickGameplay(.02, false, false, false);
            DrainRelease();
            Require(!client.IsPiloting && !client.GameplayPending, "focus-lost first E survived a delayed control ACK");
            client.TickGameplay(.02, true, true);
            Require(client.InteractSelected(client.Flight!.StationId) && client.GameplayPending, "selection regression did not queue first E");
            var originalSelection = client.SelectedPlacementId;
            client.SelectedPlacementId = "test-selection-change";
            client.TickGameplay(.02, true, true);
            client.SelectedPlacementId = originalSelection;
            DrainRelease();
            Require(!client.IsPiloting && !client.GameplayPending, "a changed selection left the old pending action alive after its lease ACK");
            client.TickGameplay(.02, true, true);
            Require(client.InteractSelected(client.Flight!.StationId), "first E after release did not dispatch or queue");
            WaitInteraction(() => client.IsPiloting, "first E after release did not enter the accepted helm: " + client.GameplayMessage);
            Require(client.IsPiloting && client.Flight?.SeatState == "seated" && client.Station?.OccupantId == initialActorId,
                "first E without previous WASD did not produce accepted helm entry: " + client.GameplayMessage);
            Console.WriteLine("First E after focus/menu lease release entered the actual helm without prior WASD.");
            DrainRelease(); client.TickGameplay(.02, false, false, true);
            Require(client.InteractSelected(client.Flight!.StationId, fromUi: true), "blocked inspector explicit Use did not queue its action");
            client.StopMovementForUiCapture();
            WaitInteraction(() => !client.IsPiloting, "blocked inspector Use did not leave the accepted helm", keyboard: false);
            DrainRelease();
            Require(client.Flight?.SeatState == "none" && client.Station?.OccupantId == null, "UI-blocked explicit action did not reconcile seat release");
            client.TickGameplay(.02, true, true);
            Require(client.InteractSelected(client.Flight!.StationId), "second keyboard E after UI action did not queue");
            WaitInteraction(() => client.IsPiloting, "keyboard E after the blocked UI action did not re-enter the accepted helm");
            Console.WriteLine("Canceled/focus-lost/changed-selection pending E ignored late ACK; blocked inspector Use accepted with zero movement and released its lease.");
            client.TickGameplay(.02, true, true); Require(client.GameplayEpoch > epoch, "seating did not invalidate prior gameplay context"); Claim();
            var beforeThrust = GameplayRules.ForwardSpeed(client.Ship!.Heading, client.Ship.Vx, client.Ship.Vy);
            PumpFor(1, forward: 1);
            Require(GameplayRules.ForwardSpeed(client.Ship!.Heading, client.Ship.Vx, client.Ship.Vy) > beforeThrust + .05, "leased helm thrust did not advance accepted ship speed");
            Require(client.CruiseAvailable && client.ToggleCruiseOrSuit(), "cruise did not engage after the acknowledged helm lease");
            var targetSpeed = client.CruiseSpeed; PumpFor(.5);
            Require(client.CruiseActive && targetSpeed > 0 && targetSpeed <= 30, "cruise did not retain its browser target intent without manual throttle");
            PumpFor(.15, forward: -1); Require(!client.CruiseActive, "manual reverse thrust did not cancel cruise");
            var beforeBrake = Math.Sqrt(client.Ship!.Vx * client.Ship.Vx + client.Ship.Vy * client.Ship.Vy); PumpFor(1.5);
            Require(Math.Sqrt(client.Ship!.Vx * client.Ship.Vx + client.Ship.Vy * client.Ship.Vy) < beforeBrake + .01, "zero pilot intent did not brake accepted motion");
            var computer = initialPower.First(row => row.Kind == "computer" && row.Powered);
            var engine = initialPower.First(row => row.Kind == "actuator" && row.Powered);
            if (metadata.GetProperty("devicePowerQualified").GetBoolean())
            {
                Require(client.ToggleCruiseOrSuit(), "cruise did not re-engage before power-loss test");
                Mutate(() => client.SetComputerPower(computer.PlacedObjectId, false), () => client.Connection!.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.PlacedObjectId == computer.PlacedObjectId && !row.Powered), "Flight computer disconnect");
                client.TickGameplay(.02, true, true);
                Require(!client.CruiseAvailable && !client.CruiseActive, "cruise survived loss of its powered computer relationship");
                Mutate(() => client.SetComputerPower(computer.PlacedObjectId, true), () => client.Connection!.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.PlacedObjectId == computer.PlacedObjectId && row.Powered), "Flight computer reconnect");
                Mutate(() => client.SetEnginePower(engine.PlacedObjectId, false), () => client.Connection!.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.PlacedObjectId == engine.PlacedObjectId && !row.Powered), "Engine disconnect");
                Mutate(() => client.SetEnginePower(engine.PlacedObjectId, true), () => client.Connection!.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.PlacedObjectId == engine.PlacedObjectId && row.Powered), "Engine reconnect");
            }
            else
            {
                RefusePower(() => client.SetComputerPower(computer.PlacedObjectId, false), "Unqualified prefab computer power");
                RefusePower(() => client.SetEnginePower(engine.PlacedObjectId, false), "Unqualified prefab engine power");
                Console.WriteLine("Current browser source has no qualified device-power ships: real rejected computer/engine receipts preserved all fitting states/revisions.");
            }
            Mutate(() => client.InteractSelected(client.Flight!.StationId), () => !client.IsPiloting && client.Flight?.SeatState == "none" && client.Station?.OccupantId == null, "Authored helm leave");
            Stop(); Console.WriteLine("Network helm enter/thrust/cruise/manual cancel/brake/qualified power policy/leave passed.");

            var logic = metadata.GetProperty("logic");
            if (logic.ValueKind == JsonValueKind.Null)
            { Console.WriteLine("SKIP contextual airlock and EVA: the current disclosed source prefab has no authored airlock logic; no exit or inventory was seeded."); completed = true; return; }
            var panels = logic.GetProperty("panels").EnumerateArray().ToArray();
            string? Port(JsonElement panel) => logic.GetProperty("graph").GetProperty("wires").TryGetProperty(panel.GetProperty("deviceId").GetString() + ".pressed", out var wire) && wire.GetArrayLength() > 0 ? wire[0].GetProperty("port").GetString() : null;
            var innerPanel = panels.FirstOrDefault(panel => panel.GetProperty("side").GetString() == "interior" && Port(panel) == "open_inner");
            var cyclePanel = panels.FirstOrDefault(panel => panel.GetProperty("side").GetString() == "interior" && Port(panel) == "cycle");
            if (innerPanel.ValueKind == JsonValueKind.Undefined || cyclePanel.ValueKind == JsonValueKind.Undefined)
            { Console.WriteLine("SKIP contextual airlock and EVA: source has no paired reachable open-inner/cycle controls supported by this probe."); completed = true; return; }
            Press(innerPanel);
            var innerDoor = logic.GetProperty("doors").EnumerateArray().First(door => !door.GetProperty("exterior").GetBoolean()).GetProperty("deviceId").GetString()!;
            wait(() => client.Connection!.Db.VisibleShipLogic.Iter().Any(row => row.ShipId == initialShipId && row.DeviceId == innerDoor && row.Open), "Airlock inner door did not become accepted open");
            var storages = metadata.GetProperty("storages").EnumerateArray().ToArray();
            var bed = metadata.GetProperty("beds").EnumerateArray().FirstOrDefault(candidate => client.Connection!.Db.OwnInteractions.Iter().Any(row => row.PlacementId == candidate.GetProperty("placementId").GetString()));
            if (bed.ValueKind == JsonValueKind.Undefined)
                Console.WriteLine("SKIP furniture seating: no source-defined prefab seat is currently disclosed in OwnInteractions.");
            else
            {
            Walk(new(bed.GetProperty("approachX").GetDouble(), bed.GetProperty("approachY").GetDouble()));
            // Current Wren issues only its suit locker. Other storage meshes are unbound;
            // clear bed gear using the actual floor-drop transaction, preserving pack contents.
            foreach (var item in initialEquipment.Where(item => item.EquipmentSlot is "back" or "belt"))
            {
                Require(client.DropItem(item.Id, client.Inventory.Revision), "bed clearance drop did not dispatch");
                wait(() => !client.InventoryPending && client.Connection!.Db.OwnGroundItems.Iter().Any(row => row.Id == item.Id && row.Reachable), "bed clearance did not produce a reachable ground UUID");
                parked.Add(item);
            }
            var bedPlacement = bed.GetProperty("placementId").GetString()!;
            var bedRow = client.Connection!.Db.OwnInteractions.Iter().First(row => row.PlacementId == bedPlacement);
            var bedRevision = bedRow.Revision;
            Mutate(() => client.InteractSelected(bedPlacement), () => client.Resting && client.Connection!.Db.OwnInteractions.Iter().Any(row => row.PlacementId == bedPlacement && row.SeatedByYou && row.Revision > bedRevision), "Furniture sit");
            var sitting = new Point(client.Character!.LocalX, client.Character.LocalY); PumpFor(.35, dx: 1);
            Require(new Point(client.Character.LocalX, client.Character.LocalY).Distance(sitting) < .01, "walking intent moved a resting actor");
            Mutate(() => client.Interact(), () => !client.Resting && client.Connection!.Db.OwnInteractions.Iter().Any(row => row.PlacementId == bedPlacement && !row.SeatedByYou), "Contextual stand");
            foreach (var item in parked) Equip(item.Id, item.EquipmentSlot); parked.Clear();
            Console.WriteLine("Network furniture ground gear clearance/sit/immobile/stand passed; original back/belt UUIDs and contents restored.");
            }

            if (!metadata.TryGetProperty("suitStock", out var stock) || stock.ValueKind != JsonValueKind.Object)
            { Console.WriteLine("SKIP positive EVA: the current exact source pin does not issue an EVA suit locker."); completed = true; return; }
            var lockerKey = stock.GetProperty("socketKey").GetString()!;
            var locker = storages.FirstOrDefault(storage => storage.GetProperty("key").GetString() == lockerKey);
            if (locker.ValueKind == JsonValueKind.Undefined)
            { Console.WriteLine("SKIP positive EVA: the declared issue-stock socket has no current source geometry."); completed = true; return; }
            Walk(Point.Read(locker.GetProperty("approachesM")[0]));
            suitLocker = client.Connection!.Db.OwnReachableCargoContainers.Iter().FirstOrDefault(row => row.Name == stock.GetProperty("containerName").GetString())?.Id;
            if (suitLocker == null)
            { Console.WriteLine("SKIP positive EVA: no issued suit container is currently disclosed at its source approach."); completed = true; return; }
            var requiredSuit = new[] { "wardrobe-suit-body", "wardrobe-suit-helmet", "wardrobe-suit-pack" };
            if (requiredSuit.Any(definition => !client.Inventory.Items.Any(row => row.DefinitionId == definition && row.ContainerId == suitLocker)))
            { Console.WriteLine("SKIP positive EVA: the disclosed issued locker lacks the complete body/helmet/jetpack set; no gear was minted."); completed = true; return; }
            foreach (var definition in requiredSuit)
            {
                var item = client.Inventory.Items.First(row => row.DefinitionId == definition && row.ContainerId == suitLocker); suitItems.Add(item);
                var destination = client.Inventory.Containers.FirstOrDefault(container => container.Carried && client.Inventory.FirstPlacement(item.Id, container.Id).HasValue);
                if (destination == null)
                {
                    var bulky = client.Inventory.Items.Where(row => client.Inventory.Container(row.ContainerId)?.Carried == true && row.Definition != null && !requiredSuit.Contains(row.DefinitionId)).OrderByDescending(row => row.Definition!.Width * row.Definition.Height).First();
                    parked.Add(bulky); Transfer(bulky.Id, suitLocker);
                    destination = client.Inventory.Containers.First(container => container.Carried && client.Inventory.FirstPlacement(item.Id, container.Id).HasValue);
                }
                Transfer(item.Id, destination.Id); Equip(item.Id, item.Definition!.EquipSlot);
            }
            Require(client.EvaSuitEquipped, "taking and equipping issued suit parts did not satisfy the real suit gate");
            Press(cyclePanel);
            var outerDoor = logic.GetProperty("doors").EnumerateArray().First(door => door.GetProperty("exterior").GetBoolean()).GetProperty("deviceId").GetString()!;
            wait(() => client.Connection!.Db.VisibleShipLogic.Iter().Any(row => row.ShipId == initialShipId && row.DeviceId == outerDoor && row.Open), "Airlock cycle did not open the accepted outer door");
            var entry = metadata.GetProperty("entries").EnumerateArray().First();
            var normal = Point.Read(entry.GetProperty("normal")); var inside = Point.Read(entry.GetProperty("inside")); Walk(inside);
            var evaDeadline = Stopwatch.StartNew();
            while (client.Eva == null && evaDeadline.Elapsed.TotalSeconds < 5) { Tick(dx: normal.X, dy: normal.Y); Thread.Sleep(20); }
            Require(client.Eva is { } eva && eva.CharacterId == initialActorId && eva.ExitShipId == initialShipId && client.Location == null, "open-hatch walking did not hand off to authoritative EVA");
            client.TickGameplay(.02, true, true); Claim(); wait(() => client.EvaSuit?.Mode == "hold", "accepted EVA hold-mode suit state did not arrive");
            Require(client.ToggleCruiseOrSuit(), "X suit mode request did not dispatch"); PumpFor(.3);
            wait(() => client.EvaSuit?.Mode == "free", "X did not confirm free suit mode");
            var heading = client.Eva!.LocalHeading; PumpFor(.3, horizontal: -1);
            Require(Math.Abs(GameplayRules.WrapAngle(client.Eva!.LocalHeading - heading)) > .01, "free suit A/D did not change authoritative yaw");
            var evaPosition = new Point(client.Eva.LocalX, client.Eva.LocalY); PumpFor(.25, horizontal: 1, shift: true);
            Require(new Point(client.Eva!.LocalX, client.Eva.LocalY).Distance(evaPosition) > .01, "free suit Shift+A/D did not strafe accepted EVA position");
            Require(client.ToggleCruiseOrSuit(), "hold suit mode request did not dispatch"); PumpFor(.3);
            wait(() => client.EvaSuit?.Mode == "hold", "suit hold restoration did not confirm");
            var board = new Point(inside.X - normal.X * .25, inside.Y - normal.Y * .25); var boardingDeadline = Stopwatch.StartNew();
            while (client.Eva is { } body && boardingDeadline.Elapsed.TotalSeconds < 8)
            {
                var dx = board.X - body.LocalX; var dy = board.Y - body.LocalY;
                client.SetPointerDirection(dx, dy); Tick(forward: Math.Min(1, Math.Sqrt(dx * dx + dy * dy))); Thread.Sleep(20);
            }
            Require(client.Eva == null && client.Location?.InstanceId == initialShipId && client.Character!.Id == initialActorId, "accepted EVA doorway did not board the same owned deck and actor");
            Stop(); Press(innerPanel);
            wait(() => client.Connection!.Db.VisibleShipLogic.Iter().Any(row => row.ShipId == initialShipId && row.DeviceId == innerDoor && row.Open), "boarding return did not reopen the inner doorway");
            Console.WriteLine("Network issued-suit transfer/equip, contextual airlock cycle, deck-to-EVA, hold/free/yaw/shift-strafe and same-deck boarding passed.");
            completed = true;
        }
        finally
        {
            client.CancelGameplayInput();
            if (client.Connection != null && client.Eva == null && client.Location?.InstanceId == initialShipId && client.Alive)
            {
                if (client.IsPiloting) { Claim(); client.ToggleSeat(); wait(() => !client.IsPiloting, "Probe cleanup could not leave the helm"); }
                if (client.Resting) { Claim(); client.Interact(); wait(() => !client.Resting, "Probe cleanup could not stand up"); }
                foreach (var power in initialPower)
                {
                    if (client.Connection.Db.OwnAuthoredFlightPowerFittings.Iter().FirstOrDefault(row => row.PlacedObjectId == power.PlacedObjectId)?.Powered == power.Powered) continue;
                    Mutate(() => power.Kind == "computer" ? client.SetComputerPower(power.PlacedObjectId, power.Powered) : client.SetEnginePower(power.PlacedObjectId, power.Powered),
                        () => client.Connection.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.PlacedObjectId == power.PlacedObjectId && row.Powered == power.Powered), "Cleanup power restore");
                }
                if (suitLocker != null && metadata.ValueKind != JsonValueKind.Undefined)
                {
                    var lockerKey = metadata.GetProperty("suitStock").GetProperty("socketKey").GetString()!;
                    var locker = metadata.GetProperty("storages").EnumerateArray().First(storage => storage.GetProperty("key").GetString() == lockerKey); Walk(Point.Read(locker.GetProperty("approachesM")[0]));
                    // Stow helmet/uniform before swapping back the original pack. A server-staged
                    // ground pack remains the same UUID and is reachable at this locker approach.
                    foreach (var item in suitItems.Where(item => item.Definition!.EquipSlot != "back")) ReturnToLocker(item);
                    foreach (var item in initialEquipment)
                    {
                        if (client.Inventory.Item(item.Id)?.EquipmentSlot == item.EquipmentSlot) continue;
                        // Equip the reachable old floor pack directly, exactly as the preceding
                        // furniture test did; no invented carried cell or temporary extra pack.
                        Equip(item.Id, item.EquipmentSlot);
                    }
                    foreach (var item in suitItems.Where(item => item.Definition!.EquipSlot == "back")) ReturnToLocker(item);
                    foreach (var item in parked.Where(item => item.EquipmentSlot.Length == 0))
                    {
                        if (client.Inventory.Item(item.Id)?.ContainerId == item.ContainerId) continue;
                        Transfer(item.Id, item.ContainerId);
                    }
                    foreach (var item in suitItems) Require(client.Inventory.Item(item.Id)?.ContainerId == suitLocker, "cleanup did not return an issued suit UUID to its locker");
                }
                // Furniture may fail or be unavailable before a suit locker is selected.
                // Restore any exact dropped back/belt UUID at that still-reachable floor
                // scene too, rather than making equipment cleanup depend on EVA content.
                foreach (var item in initialEquipment)
                    if (client.Inventory.Item(item.Id)?.EquipmentSlot != item.EquipmentSlot) Equip(item.Id, item.EquipmentSlot);
                Walk(initialPosition); client.SetInteriorView(initialView); Stop();
                if (completed)
                {
                    Require(originalInventory.All(item => client.Inventory.Item(item.Id) != null), "gameplay probe lost an original inventory UUID");
                    Require(initialEquipment.All(item => client.Inventory.Item(item.Id)?.EquipmentSlot == item.EquipmentSlot), "gameplay probe did not restore the original equipment");
                    Require(Math.Abs(initialMass - client.Inventory.CarriedMassKg) < .0001, "gameplay probe did not restore carried inventory mass");
                    Console.WriteLine("Gameplay network probe restored actor UUID/current deck, equipment, carried UUIDs/mass, power, initial walking position and released input lease.");
                }
            }
            else Console.WriteLine("Gameplay probe cleanup requires an explicit recovery: current scene/admission/actor changed or the actor remains outside; no transform was invented.");
            client.CancelGameplayInput(); Directory.Delete(temporary, true);
        }
    }

    private static string Execute(string command, string directory, params string[] arguments) => Execute(command, directory, arguments, null);
    private static string Execute(string command, string directory, string[] arguments, string? input)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(command) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        Require(process.Start(), "source helper failed to start");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (input != null) { process.StandardInput.Write(input); process.StandardInput.Close(); }
        if (!process.WaitForExit(20000)) { process.Kill(true); throw new InvalidOperationException("Source route helper exceeded its20-second bound"); }
        Require(process.ExitCode == 0, "source helper failed: " + errors.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    private const string SourcePlanner = """
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import path from "node:path";
const input = JSON.parse(readFileSync(0, "utf8"));
const load = p => import(pathToFileURL(path.join(input.source, p)).href);
const collision = await load("packages/sim/src/construction-collision.ts");
const objects = await load("packages/sim/src/prefab-deck-objects.ts");
const flight = await load("packages/sim/src/prefab-flight.ts");
const seats = await load("packages/sim/src/prefab-seats.ts");
const cargo = await load("packages/sim/src/prefab-cargo-sockets.ts");
const logicModule = await load("packages/sim/src/ship-logic-model.ts");
const evaModule = await load("packages/sim/src/eva.ts");
const furnishings = await load("packages/content/src/wayfarer-furnishings.ts");
const qualification = await load("packages/world/src/ship-feature-qualification.ts");
const prefabPins = await load("packages/world/src/prefab-ship-pins.ts");
const doc = JSON.parse(input.documentJson), overrides = furnishings.readFurnishingOverrides(input.furnishingsJson);
const binding = flight.prefabDocumentOf(input.documentJson);
const logic = logicModule.shipLogicModel(binding.document, binding.catalog);
if (input.mode === "metadata") {
 const helm = flight.prefabFlightModel(binding.document, binding.catalog).station;
 if (!helm) throw Error("Accepted source document has no pilot station");
 console.log(JSON.stringify({ documentSha256:createHash("sha256").update(input.documentJson).digest("hex"),
  prefabId:binding.document.id,prefabRevision:binding.document.revision,
  helmApproach:[helm[0],helm[1]-.875], beds:seats.prefabBedsOfDocument(input.documentJson,overrides),
  storages:cargo.prefabCargoSockets(binding.document,0,binding.catalog,overrides),
  logic:logic && {...logic,graph:{devices:[...logic.graph.devices.values()],wires:Object.fromEntries(logic.graph.wires)}},
  devicePowerQualified:qualification.shipFeatureQualified("device-power",input.blueprintSha256),
  suitStock:binding.document.id===prefabPins.FED_WREN_PIN.prefabId&&input.blueprintSha256===prefabPins.FED_WREN_PIN.blueprintSha256
   ? prefabPins.FED_WREN_PIN.issueStock?.find(stock=>stock.kit==="eva-suit") : null,
  entries:evaModule.prefabEvaModel(binding.document,binding.catalog).entries }));
} else {
 const width=doc.boundaryKit?.revision === "r001" ? .0625 : 0;
 const base=collision.compileDeckCollision(doc.layout,input.deckId,{shipId:input.shipId,perimeterHalfWidthM:width,
  partitionHalfWidthM:doc.pressureRoom ? .0625 : width,obstacles:objects.prefabConstructionObstacles(doc,overrides) ?? []});
 const states=new Map(input.doors.map(d=>[d.id,d.fraction===1]));
 for(const door of logic?.doors ?? []) if(door.openingId) states.set(door.openingId,input.logicStates.some(s=>s.deviceId===door.deviceId&&s.open));
 const frame=collision.resolveDeckCollision(base,[...states].filter(([id])=>base.openings.some(o=>o.id===id)).map(([openingId,passable])=>({openingId,passable})));
 const loc=position=>({shipId:input.shipId,deckId:input.deckId,position});
 const free=p=>collision.canOccupyDeck(frame,loc(p),.3);
 const can=(a,b)=>{ if(!free(a)||!free(b))return false;const q=collision.sweepDeckCircle(frame,loc(a),[b[0]-a[0],b[1]-a[1]],.3).position;return Math.hypot(q[0]-b[0],q[1]-b[1])<1e-5; };
 const start=input.start,target=input.target;
 if(!free(start)||!free(target))throw Error("Source route endpoint is unsupported or penetrating: "+JSON.stringify({start,target,startFree:free(start),targetFree:free(target)}));
 if(can(start,target)){console.log(JSON.stringify({reachable:true,points:[target]}));process.exit(0);}
 const step=.25,key=(x,y)=>x+","+y,point=(x,y)=>[x*step,y*step];
 const all=base.floors.flat(),minX=Math.floor(Math.min(...all.map(p=>p[0]))/step),maxX=Math.ceil(Math.max(...all.map(p=>p[0]))/step),
  minY=Math.floor(Math.min(...all.map(p=>p[1]))/step),maxY=Math.ceil(Math.max(...all.map(p=>p[1]))/step);
 if((maxX-minX+1)*(maxY-minY+1)>65536)throw Error("Source navigation exceeds bounded65536-cell probe");
 const nodes=[],seen=new Map(),queue=[],parent=new Map();
 for(let x=Math.floor(start[0]/step)-2;x<=Math.ceil(start[0]/step)+2;x++)for(let y=Math.floor(start[1]/step)-2;y<=Math.ceil(start[1]/step)+2;y++)if(can(start,point(x,y))){const k=key(x,y);seen.set(k,[x,y]);parent.set(k,null);queue.push(k);}
 let end=null;
 for(let cursor=0;cursor<queue.length&&end===null;cursor++){
  const k=queue[cursor],[x,y]=seen.get(k),p=point(x,y);if(can(p,target)){end=k;break;}
  for(const [dx,dy]of [[1,0],[-1,0],[0,1],[0,-1],[1,1],[1,-1],[-1,1],[-1,-1]]){
   const xx=x+dx,yy=y+dy,kk=key(xx,yy);if(xx<minX||xx>maxX||yy<minY||yy>maxY||seen.has(kk)||!can(p,point(xx,yy)))continue;
   seen.set(kk,[xx,yy]);parent.set(kk,k);queue.push(kk);
  }
 }
 if(end===null){console.log(JSON.stringify({reachable:false,points:[]}));process.exit(0);}
 const reverse=[target];for(let k=end;k!==null;k=parent.get(k)){const [x,y]=seen.get(k);reverse.push(point(x,y));}
 const raw=[start,...reverse.reverse()],result=[];let at=0;
 while(at<raw.length-1){let to=at+1;for(let i=raw.length-1;i>at+1;i--)if(can(raw[at],raw[i])){to=i;break;}result.push(raw[to]);at=to;}
 console.log(JSON.stringify({reachable:true,points:result}));
}
""";
}
