using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sidereal.Bindings;
using Sidereal.Native.Input;
using SpacetimeDB.ClientApi;

namespace Sidereal.Native;

public sealed record GameplayInteraction(string Kind, string Id, string Label, bool Enabled, ulong Revision = 0,
    string ShipId = "", string PlacementId = "", string ContainerId = "", string Reason = "");

public sealed record PresentedShip(string Id, string Name, double X, double Y, double Vx, double Vy,
    double Heading, double Omega, ulong Tick, bool Owned);

public sealed partial class ClientCore
{
    // Every entry is a public actor-filtered projection. Base tables are never subscribed.
    private static readonly string[] GameplaySubscriptions = new[] {
        "own_characters", "own_ships", "own_stations", "own_construction_instances", "own_construction_location",
        "own_construction_seat", "own_authored_flights", "own_interactions", "own_inventory_state",
        "own_character_vitals", "own_ship_power", "own_combat", "own_inventory_items", "own_inventory_containers",
        "own_inventory_hotbar", "own_item_definition_pins", "published_item_definitions",
        "own_reachable_cargo_items", "own_reachable_cargo_containers", "own_carried_inventory_revisions",
        "own_authored_flight_physics", "own_authored_flight_actuators", "own_authored_flight_fittings",
        "own_authored_flight_power_fittings", "own_actuator_outputs", "own_ship_component_damage",
        "own_ship_networks", "own_ship_systems_report", "own_ship_power_devices", "own_appearance", "own_identity_links",
        "own_combat_impact", "visible_combat_actions", "own_ground_items", "own_eva_body", "own_eva_suit",
        "own_eva_airlock_cycle", "visible_eva_bodies", "visible_ship_logic", "own_construction_doors",
        "own_native_airlocks", "own_construction_native_pressure", "own_construction_decks",
        "own_construction_traversals", "own_construction_traversal_links", "own_construction_stair_walks",
        "own_construction_stair_egress_geometry", "own_construction_grants", "own_passenger_grants",
        "own_passenger_visit", "current_passenger_interior", "current_interior_crew", "visible_crew_presentation",
        "own_world_admission", "own_game_ship_access", "own_space_bodies", "admitted_system_scapes", "own_ship_zones",
        "visible_body_motion", "visible_body_descriptions", "nearby_field_asteroids",
        "visible_ship_descriptions", "visible_actuator_exhaust", "visible_ship_system_effects"
    }.Select(name => "SELECT * FROM " + name).ToArray();

    private sealed record CommandAttempt(Session Session, string Kind, double Deadline, bool Exclusive, ulong Epoch);
    private readonly Dictionary<string, CommandAttempt> gameplayCommands = new(StringComparer.Ordinal);
    private readonly GameplayCallbackLedger<Session> gameplayCallbacks = new();
    private readonly CruiseControl cruise = new();
    private readonly SharedWorldJoin sharedJoin;
    private string? gameplayContext;
    private bool keyboardAllowed, pointerAllowed, uiActionsAllowed = true, triggerHeld, pendingTrigger, aimPending, aimPreviouslyActive;
    private sealed record PendingInteraction(Session Session, ulong Epoch, ulong WorldEpoch, string ActorId, string? InstanceId, ulong? InstanceRevision,
        ulong? AdmissionRevision, GameplayInteraction Action, string? SelectionId, string? SelectionSnapshot, bool FromUi, double Deadline);
    private PendingInteraction? pendingInteraction;
    private string? triggerWeapon;
    private sealed record AimAttempt(Session Session, bool Active, double Angle, ulong Serial, double Time, ulong Epoch, string? WeaponId);
    private readonly List<AimAttempt> aimAttempts = new();
    private ulong aimSerial, acceptedAimSerial;
    private bool acceptedAimActive, suitPending;
    private string? lastSuitMode;
    private double? aimAngle, pointerDx, pointerDy;
    private double lastAimAt = -100, lastFireAt = -100, lastReloadAt = -100, lastSuitAt = -100;
    private double lastSuitFacing;
    private string? requestedSuitMode;
    private (ulong Epoch, double Deadline)? pendingModeToggle;
    private bool lastSuitFacingActive;
    private Queue<string>? inventoryBatch;
    private string inventoryBatchDestination = "";
    public ulong SharedWorldEpoch { get; private set; }
    public bool SpatialReady => active?.WorldScopes?.Ready == true;
    public int SpatialCellSets => active?.WorldScopes?.CellSets ?? 0;
    public ulong GameplayEpoch { get; private set; }
    public bool InteriorView { get; private set; } = true;
    public bool CombatEnabled { get; private set; }
    public bool CruiseActive => cruise.Active;
    public double CruiseSpeed => cruise.Throttle * 30;
    public string GameplayMessage { get; private set; } = "";
    public bool GameplayPending => pendingInteraction != null || gameplayCommands.Values.Any(command => command.Exclusive);
    public string? SelectedPlacementId { get; set; }
    public event Action<string>? OpenStorageRequested;
    public event Action? PresentationViewChanged;
    public EvaBodyStatus? Eva => Connection?.Db.OwnEvaBody.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id);
    public EvaSuitStatus? EvaSuit => Connection?.Db.OwnEvaSuit.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id);
    public CharacterAppearanceStatus? Appearance => Connection?.Db.OwnAppearance.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id);
    public AuthoredFlightPhysics? FlightPhysics => Connection?.Db.OwnAuthoredFlightPhysics.Iter().FirstOrDefault(row => row.ShipId == Character?.ShipId);
    public ShipNetworkSummary? Systems => Connection?.Db.OwnShipNetworks.Iter().FirstOrDefault(row => row.ShipId == Character?.ShipId);
    public bool Alive => Vitals?.State == "active";
    public bool RecoveringPilot => Flight?.SeatState == "recovery-pending";
    public bool Resting => Seat != null || Connection?.Db.OwnInteractions.Iter().Any(row => row.Kind == "seat" && row.SeatedByYou) == true;
    public bool NearStation => !Resting && Eva == null && Character is { } actor && Station is { } station &&
        station.ShipId == actor.ShipId && Math.Sqrt(Math.Pow(actor.LocalX - station.LocalX, 2) + Math.Pow(actor.LocalY - station.LocalY, 2)) <= 1.8;
    public string ActiveShipId => Eva?.AnchorShipId is { Length: > 0 } anchor ? anchor : Character?.ShipId ?? "";
    public PassengerInteriorShip? PassengerInterior => Connection?.Db.CurrentPassengerInterior.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id && row.ShipId == Character?.ShipId);
    public PresentedShip? CurrentPresentedShip
    {
        get
        {
            if (Ship is { } ship && ship.Id == ActiveShipId) return new(ship.Id, ship.Name, ship.X, ship.Y, ship.Vx, ship.Vy, ship.Heading, ship.Omega, ship.Tick, true);
            if (SpatialReady && PassengerInterior is { } passenger && Connection?.Db.VisibleShipMotion.Iter().FirstOrDefault(row => row.ShipId == passenger.ShipId) is { } motion)
                return new(passenger.ShipId, passenger.Name, motion.X, motion.Y, motion.Vx, motion.Vy, motion.Heading, motion.Omega, motion.ServerTick, false);
            if (SpatialReady && Eva is { } eva && Connection?.Db.VisibleShipMotion.Iter().FirstOrDefault(row => row.ShipId == eva.AnchorShipId || row.ShipId == eva.ExitShipId) is { } anchor)
                return new(anchor.ShipId, "Nearby ship", anchor.X, anchor.Y, anchor.Vx, anchor.Vy, anchor.Heading, anchor.Omega, anchor.ServerTick, false);
            return null;
        }
    }

    private static double Now => Environment.TickCount64 / 1000d;
    private static string SafeReason(string reason)
    {
        var clean = reason.Replace('\r', ' ').Replace('\n', ' ');
        return clean[..Math.Min(clean.Length, 300)];
    }
    private Character? ReadCharacter()
    {
        if (Connection == null) return null;
        var actors = Connection.Db.OwnCharacters.Iter().ToArray();
        var admitted = Connection.Db.OwnWorldAdmission.Iter().FirstOrDefault();
        return admitted == null ? actors.Length == 1 ? actors[0] : null : actors.FirstOrDefault(actor => actor.Id == admitted.CharacterId);
    }
    private static bool IsCaller(Session session, ReducerEventContext context) => context.Event.CallerConnectionId == session.Connection?.ConnectionId;
    private static bool Accepted(ReducerEventContext context) => context.Event.Status is SpacetimeDB.Status.Committed;
    private static string? RejectedReason(ReducerEventContext context) => context.Event.Status is SpacetimeDB.Status.Failed(var reason) ? SafeReason(reason) : "The server could not apply the command.";
    private void InitializeGameplaySession(Session session)
    {
        var connection = session.Connection!;
        session.WorldScopes = new((queries, applied, error) => {
                var retained = new NativeScopeHandle();
                retained.Bind(connection.SubscriptionBuilder().OnApplied(_ => { retained.Applied(); applied(); }).OnError((_, _) => error()).Subscribe(queries));
                return retained;
            },
            () => { if (active == session && session.WorldScopes != null && session.WorldScopeEpoch != session.WorldScopes.Epoch) { session.WorldScopeEpoch = session.WorldScopes.Epoch; SharedWorldEpoch++; } }, () => Stall(session));
        session.Lease = new(() => connection.Reducers.ClaimInputControl(), () => connection.Reducers.ReleaseInputControl(),
            () => Stall(session), message => { if (active == session) Report(message); }, () => Now);
        session.Transmitter = new((serial, intent) => connection.Reducers.SetIntent(serial, intent.Throttle, intent.Turn, intent.Dx, intent.Dy, intent.Sprint),
            message => { if (active == session) { cruise.Cancel(); ReleaseControls(); Report(message); } }, () => Stall(session), () => Now);
        connection.Reducers.OnClaimInputControl += context => { if (!IsCaller(session, context)) return; session.Lease.ClaimResult(Accepted(context), RejectedReason(context)); if (active == session) controls = session.Lease.CanSend; };
        connection.Reducers.OnReleaseInputControl += context => { if (IsCaller(session, context)) session.Lease.ReleaseResult(); };
        connection.Reducers.OnSetIntent += (context, serial, _, _, _, _, _) => { if (IsCaller(session, context)) session.Transmitter.Result(serial, Accepted(context), RejectedReason(context)); };
        connection.Reducers.OnSetCombatAim += (context, enabled, angle) => {
            if (active != session || !IsCaller(session, context)) return;
            var attempt = aimAttempts.FirstOrDefault(a => a.Session == session && a.Active == enabled && a.Angle == angle);
            if (attempt == null) return;
            aimAttempts.Remove(attempt);
            if (attempt.Serial != aimSerial) return;
            aimPending = false;
            acceptedAimSerial = attempt.Serial; acceptedAimActive = Accepted(context) && enabled && attempt.Epoch == GameplayEpoch && attempt.WeaponId == Combat?.WeaponItemId;
            if (!Accepted(context) && attempt.Epoch == GameplayEpoch) { triggerHeld = pendingTrigger = false; Report("Aim refused: " + RejectedReason(context)); }
        };
        connection.Reducers.OnInteractObject += (context, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnEnterAuthoredPilot += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnLeaveAuthoredPilot += context => GameplayResultKind(session, context, "leave-pilot");
        connection.Reducers.OnUseStation += context => GameplayResultKind(session, context, "legacy-station");
        connection.Reducers.OnPressShipButton += (context, ship, device) => GameplayResultKind(session, context, "ship-button", ship, device);
        connection.Reducers.OnEvaCycleAirlock += (context, ship, airlock) => GameplayResultKind(session, context, "airlock-entry", ship, airlock);
        connection.Reducers.OnEvaSetSuit += (context, mode, facing, enabled) => {
            if (active != session || !IsCaller(session, context) || !suitPending || mode != lastSuitMode || facing != lastSuitFacing || enabled != lastSuitFacingActive) return;
            suitPending = false;
            if (!Accepted(context)) { requestedSuitMode = null; lastSuitMode = null; Report("Suit control refused: " + RejectedReason(context)); ReleaseControls(); }
        };
        connection.Reducers.OnEvaEmergencyReturn += context => GameplayResultKind(session, context, "rescue-beacon");
        connection.Reducers.OnFireWeapon += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnReloadWeapon += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnSetConstructionEnginePower += (context, _, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnSetConstructionComputerPower += (context, _, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnSetCharacterAppearance += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnRequestIdentityLink += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnAcceptIdentityLink += (context, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnJoinSharedSystem += (context, _, _, _, _, operation) => { if (active == session && IsCaller(session, context)) sharedJoin.Result(operation, Accepted(context), RejectedReason(context)); };
        connection.Reducers.OnSetConstructionDoor += (context, _, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnBeginConstructionTraversal += (context, _, _, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnCancelConstructionTraversal += (context, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnEnterConstructionReview += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnSwitchConstructionReview += (context, _, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnLeaveConstructionReview += (context, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnBeginAuthoredFlightReview += (context, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnReturnAuthoredFlightReview += (context, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnRenameShip += (context, _, _, operation, _) => GameplayResult(session, context, operation);
        connection.Reducers.OnEditShipFurnishing += (context, _, _, _, _, _, _, _, _, operation) => GameplayResult(session, context, operation);
        connection.Reducers.OnTransferInventoryItem += (context, _, _, _, operation) => { if (active == session && IsCaller(session, context)) InventoryResult(context, operation); };
        connection.Reducers.OnDropInventoryItem += (context, _, _, operation) => { if (active == session && IsCaller(session, context)) InventoryResult(context, operation); };
        connection.Reducers.OnTakeAllInventoryItems += (context, _, _, operation) => { if (active == session && IsCaller(session, context)) InventoryResult(context, operation); };
        connection.Reducers.OnStoreAllInventoryItems += (context, _, _, _, operation) => { if (active == session && IsCaller(session, context)) InventoryResult(context, operation); };
        connection.Reducers.OnTransferScopedCargoItem += (context, operation, _, _, _, _, _, _, _, _, _, _) => { if (active == session && IsCaller(session, context)) InventoryResult(context, operation); };
        connection.Reducers.OnClaimCharacterArmory += (context, _, operation) => { if (active == session && IsCaller(session, context)) InventoryResult(context, operation); };
    }
    private sealed class NativeScopeHandle : IWorldScopeHandle
    {
        private SubscriptionHandle? handle;
        private Action? awaitingRelease;
        private bool releaseSent;
        public void Bind(SubscriptionHandle retained) => handle = retained;
        public void Applied() { if (awaitingRelease != null) End(awaitingRelease); }
        public void End(Action ended)
        {
            if (releaseSent) return;
            if (handle?.IsEnded == true) { releaseSent = true; ended(); }
            else if (handle?.IsActive == true) { releaseSent = true; awaitingRelease = null; handle.UnsubscribeThen(_ => ended()); }
            else awaitingRelease = ended;
        }
    }
    private void UpdateWorldScopes()
    {
        if (Connection == null || active?.WorldScopes == null) return;
        var admission = Connection.Db.OwnWorldAdmission.Iter().FirstOrDefault();
        var motion = admission == null ? null : Connection.Db.VisibleShipMotion.Iter().FirstOrDefault(row => row.ShipId == admission.ShipId && row.SystemId == admission.SystemId);
        var eva = admission == null ? null : Connection.Db.OwnEvaBody.Iter().FirstOrDefault(row => row.CharacterId == admission.CharacterId && row.SystemId == admission.SystemId);
        active.WorldScopes.Accept(admission == null ? null : new(admission.CharacterId, admission.ShipId, admission.SystemId, admission.Revision),
            motion == null ? null : new(motion.ShipId, motion.SystemId, motion.X, motion.Y, motion.CellX, motion.CellY, motion.ServerTick),
            eva == null ? null : new(eva.CharacterId, eva.SystemId, eva.X, eva.Y, eva.ServerTick, eva.Revision));
    }
    private void Stall(Session session)
    {
        session.Failed = true; session.Lease?.Dispose(); session.Transmitter?.Dispose();
        session.Connection?.Disconnect();
        if (active == session) { controls = false; cruise.Cancel(); triggerHeld = pendingTrigger = false; Report("Input connection stalled. Reconnecting safely."); }
    }
    private bool Command(string kind, Action<DbConnection, string> send, bool exclusive = true, string[]? callbackArguments = null)
    {
        if (Connection is not { } connection || active is not { Failed: false } session || gameplayCallbacks.IsRetired(session) || (exclusive && GameplayPending)) return false;
        var operation = Guid.NewGuid().ToString("D");
        if (callbackArguments != null && !gameplayCallbacks.TryAdd(session, operation, kind, callbackArguments, Now + 10)) return false;
        gameplayCommands[operation] = new(session, kind, Now + 10, exclusive, GameplayEpoch);
        GameplayMessage = "Waiting for server confirmation…";
        try { send(connection, operation); return true; }
        catch
        {
            // A send exception can occur after the call reached the transport. Retire
            // unversioned calls instead of letting a later equal-argument receipt match.
            gameplayCallbacks.Forget(operation); gameplayCommands.Remove(operation);
            if (callbackArguments != null) Stall(session);
            GameplayMessage = "Connection changed. Review the current state before trying again."; return false;
        }
    }
    private void GameplayResult(Session session, ReducerEventContext context, string operation)
    {
        if (active != session || !IsCaller(session, context) || !gameplayCommands.Remove(operation, out var command) || command.Session != session) return;
        if (command.Epoch != GameplayEpoch) return;
        if (!Accepted(context))
        {
            GameplayMessage = "Rejected: " + RejectedReason(context); Report(GameplayMessage);
            if (command.Kind is "fire" or "reload") triggerHeld = pendingTrigger = false;
        }
        else GameplayMessage = "Confirmed by the server.";
    }
    private void GameplayResultKind(Session session, ReducerEventContext context, string kind, params string[] arguments)
    {
        if (active != session || session.Failed || !IsCaller(session, context)) return;
        var operation = gameplayCallbacks.Complete(session, kind, arguments);
        if (operation != null) GameplayResult(session, context, operation);
    }
    private void TickGameplayOperations()
    {
        foreach (var session in gameplayCallbacks.Expire(Now))
        {
            // All unversioned commands on this socket are now ambiguous. A new
            // subscribed socket, never merely a new local UUID, is required to retry.
            if (session == active) { Stall(session); GameplayMessage = "Confirmation is delayed. Reconnect before explicitly retrying."; return; }
        }
        foreach (var attempt in gameplayCommands.Where(pair => pair.Value.Session != active || Now > pair.Value.Deadline).ToArray())
        {
            gameplayCommands.Remove(attempt.Key);
            if (attempt.Value.Session == active) { GameplayMessage = "Confirmation is delayed. Review the current state before retrying."; triggerHeld = pendingTrigger = false; }
        }
        if (inventoryBatch != null && !InventoryPending)
        {
            if (Connection == null || InventoryMessage.StartsWith("Rejected", StringComparison.Ordinal) || InventoryMessage.StartsWith("Confirmation", StringComparison.Ordinal))
            { inventoryBatch = null; return; }
            if (inventoryBatch.TryDequeue(out var item))
            {
                if (Inventory.Item(item) == null) { InventoryMessage = "Storage access changed. Reopen storage before continuing."; inventoryBatch = null; return; }
                if (TransferItem(item, inventoryBatchDestination, Inventory.Revision)) return;
                inventoryBatch = null; return;
            }
            inventoryBatch = null;
        }
    }
    private void ResetGameplayContext()
    {
        gameplayContext = null; GameplayEpoch++; SharedWorldEpoch++; SelectedPlacementId = null;
        gameplayCommands.Clear(); gameplayCallbacks.Clear(); pendingInteraction = null; inventoryBatch = null; cruise.Cancel(); requestedSuitMode = null; pendingModeToggle = null;
        triggerHeld = pendingTrigger = aimPending = aimPreviouslyActive = false; triggerWeapon = null;
        aimAngle = pointerDx = pointerDy = null;
        aimAttempts.Clear(); acceptedAimSerial = aimSerial = 0; acceptedAimActive = suitPending = false; lastSuitMode = null;
        lastAimAt = lastFireAt = lastReloadAt = lastSuitAt = -100;
    }
    public void ClaimControls()
    {
        if (Connection == null || Character?.Connected != true) return;
        active?.Lease?.Activate(true); controls = active?.Lease?.CanSend == true;
    }
    public void ReleaseControls()
    {
        pendingInteraction = null;
        if (active?.Lease?.CanSend == true) active.Transmitter?.Offer(MovementIntent.Zero, force: true);
        active?.Lease?.Activate(false); controls = false; cruise.Cancel();
    }
    public void SendIntent(double throttle, double turn, double dx, double dy, bool sprint)
    {
        if (!ControlsClaimed || active == null || Connection == null) return;
        active.Transmitter?.Offer(new(throttle, turn, dx, dy, sprint), IsPiloting);
    }
    public void SendGameplayIntent(double forward, double horizontal, double mappedDx, double mappedDy, bool shift)
    {
        if (!keyboardAllowed || !Alive || Resting || RecoveringPilot) { if (ControlsClaimed) SendIntent(0, 0, 0, 0, false); cruise.Cancel(); return; }
        if (Eva is { } eva)
        {
            var local = (eva.Phase is "local" or "maglocked") && eva.AnchorShipId == CurrentPresentedShip?.Id;
            var mode = requestedSuitMode ?? EvaSuit?.Mode ?? "hold";
            var pointer = mode != "free" && pointerAllowed && pointerDx.HasValue && pointerDy.HasValue;
            var facing = pointer ? Math.Atan2(-pointerDx!.Value, pointerDy!.Value) + (local ? 0 : CurrentPresentedShip?.Heading ?? 0)
                : local ? eva.LocalHeading : eva.Heading;
            var intent = GameplayRules.Eva(forward, horizontal, facing, mode == "free", shift);
            if (ControlsClaimed) active?.Transmitter?.Offer(intent);
            if (suitPending && Now - lastSuitAt > 1.5) { Stall(active!); return; }
            if (ControlsClaimed && Connection != null && !suitPending && Now - lastSuitAt >= .1 && (mode != lastSuitMode || pointer != lastSuitFacingActive ||
                Math.Abs(GameplayRules.WrapAngle(facing - lastSuitFacing)) > .02))
            {
                lastSuitMode = mode; lastSuitFacing = facing; lastSuitFacingActive = pointer; lastSuitAt = Now; suitPending = true;
                Connection.Reducers.EvaSetSuit(mode, facing, pointer);
            }
            return;
        }
        var throttle = cruise.Demand(IsPiloting ? forward : 0, CruiseRelationship, false);
        if (IsPiloting) SendIntent(throttle, -horizontal, 0, 0, false);
        else if (InteriorView) SendIntent(0, 0, mappedDx, mappedDy, shift && (mappedDx != 0 || mappedDy != 0));
        else SendIntent(0, 0, 0, 0, false);
    }
    public void CancelGameplayInput(string reason = "")
    {
        pendingInteraction = null;
        ClearHeldGameplayIntent(); ReleaseControls();
    }
    private void ClearHeldGameplayIntent()
    {
        triggerHeld = pendingTrigger = false; triggerWeapon = null; aimAngle = pointerDx = pointerDy = null;
        cruise.Cancel(); requestedSuitMode = null; pendingModeToggle = null;
        if (aimPreviouslyActive && Connection != null) SendCombatAim(false, 0);
        aimPreviouslyActive = false; acceptedAimActive = false;
    }
    // A UI capture stops all continuous intent. Its one explicit Use click may still wait
    // for the server lease; focus loss and ordinary release retain full cancellation.
    public void StopMovementForUiCapture()
    {
        if (pendingInteraction?.FromUi != true) pendingInteraction = null;
        ClearHeldGameplayIntent();
        if (pendingInteraction != null) SendIntent(0, 0, 0, 0, false);
        else ReleaseControls();
    }
    public void TickGameplay(double delta, bool allowKeyboard, bool allowPointer, bool allowUiActions = true)
    {
        keyboardAllowed = allowKeyboard; pointerAllowed = allowPointer; uiActionsAllowed = allowUiActions;
        var context = $"{Character?.Id}/{Character?.ShipId}/{Location?.VisitId}/{Location?.DeckId}/{Flight?.SeatState}/{Seat?.ObjectId}/{Vitals?.State}/{Eva?.Phase}/{Combat?.WeaponItemId}";
        if (gameplayContext != context)
        {
            CancelGameplayInput(); gameplayContext = context; GameplayEpoch++; SelectedPlacementId = null;
            if (Eva != null) InteriorView = false;
        }
        if (!keyboardAllowed || !Alive || RecoveringPilot) cruise.Cancel();
        else cruise.Demand(0, CruiseRelationship, false);
        if (requestedSuitMode == EvaSuit?.Mode) requestedSuitMode = null;
        if (pendingModeToggle is { } toggle)
        {
            if (toggle.Epoch != GameplayEpoch || Now > toggle.Deadline) pendingModeToggle = null;
            else if (keyboardAllowed) { pendingModeToggle = null; ToggleCruiseOrSuit(); }
        }
        TickPendingInteraction();
        TickCombat();
    }
    public void SetInteriorView(bool interior)
    {
        if (Character?.ShipId.Length > 0 && Eva == null && InteriorView != interior)
        { CancelGameplayInput(); InteriorView = interior; GameplayEpoch++; PresentationViewChanged?.Invoke(); }
    }
    public void ToggleView() => SetInteriorView(!InteriorView);
    private string? CruiseRelationship
    {
        get
        {
            var actor = Character; var flight = Flight; var station = Station; var physics = FlightPhysics;
            if (actor?.Connected != true || !Alive || Eva != null || !IsPiloting || flight is not { Active: true, FlightAdmitted: true, SeatState: "seated" } ||
                station?.Operational != true || station.OccupantId != actor.Id || physics?.Status != "ready" ||
                Power is not { CorePowered: true } power || power.InstanceRevision != Instance?.Revision ||
                Connection?.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.ShipId == actor.ShipId && row.Kind == "computer" && row.Powered) != true) return null;
            try { using var envelope = JsonDocument.Parse(physics.EnvelopeJson); if (!(envelope.RootElement.GetProperty("forward").GetDouble() > 0)) return null; }
            catch { return null; }
            return $"{Connection.ConnectionId}/{actor.Id}/{actor.ShipId}/{station.Id}/{flight.SeatRevision}/{flight.AdmissionRevision}/{Instance?.Revision}";
        }
    }
    public bool CruiseAvailable => CruiseRelationship != null;
    public bool ToggleCruise()
    {
        if (!keyboardAllowed || Ship is not { } ship || CruiseRelationship == null) return false;
        ClaimControls();
        return cruise.Toggle(CruiseRelationship, GameplayRules.ForwardSpeed(ship.Heading, ship.Vx, ship.Vy));
    }
    public bool ToggleCruiseOrSuit()
    {
        if (!keyboardAllowed)
        {
            if (!Alive || Character?.Connected != true || Eva == null && !CruiseAvailable) return false;
            pendingModeToggle = (GameplayEpoch, Now + .5); return true;
        }
        if (Eva != null && keyboardAllowed)
        { ClaimControls(); requestedSuitMode = (requestedSuitMode ?? EvaSuit?.Mode) == "free" ? "hold" : "free"; return true; }
        return ToggleCruise();
    }
    public bool RescueBeacon() => Eva is { } body && (body.Stranded || body.ReturnEndsMicros > 0) &&
        Command("rescue-beacon", (connection, _) => connection.Reducers.EvaEmergencyReturn(), callbackArguments: Array.Empty<string>());

    public GameplayInteraction? Interaction => ResolveInteraction(SelectedPlacementId);
    public GameplayInteraction? ResolveInteraction(string? selectedPlacementId = null)
    {
        if (Character is not { Connected: true } actor || !Alive || Connection == null) return null;
        var rows = Connection.Db.OwnInteractions.Iter().ToArray();
        var resting = rows.FirstOrDefault(row => row.Kind == "seat" && row.SeatedByYou);
        var ordinary = resting ?? (!IsPiloting ? rows.FirstOrDefault(row => row.PlacementId == selectedPlacementId && row.Reachable && (!row.Occupied || row.SeatedByYou) && (row.Kind != "seat" || row.Enabled)) ??
            (!NearStation ? rows.Where(row => row.Reachable && (!row.Occupied || row.SeatedByYou) && (row.Kind != "seat" || row.Enabled)).OrderBy(row => Math.Pow(row.LocalX - actor.LocalX, 2) + Math.Pow(row.LocalY - actor.LocalY, 2)).FirstOrDefault() : null) : null);
        var storage = Eva == null && !IsPiloting ? Connection.Db.OwnReachableCargoContainers.Iter().FirstOrDefault(row => row.ParentItemId.Length == 0 && row.Kind == "grid" && row.PlacedObjectId.Length > 0) : null;
        var panel = ResolvePanelInteraction();
        if (storage != null && panel != null && !EvaSuitEquipped) return StorageInteraction(storage);
        if (panel != null && (Eva != null || ordinary == null)) return panel;
        if (ordinary != null)
        {
            var action = ordinary.Kind == "seat" ? ordinary.SeatedByYou ? "stand" : "sit" : ordinary.Enabled ? "set-light-off" : "set-light-on";
            var label = ordinary.Kind == "seat" ? ordinary.SeatedByYou ? "Stand up" : "Sit on " + ordinary.Name.ToLowerInvariant() : ordinary.Enabled ? "Turn grow light off" : "Turn grow light on";
            return new(action, ordinary.Id, label, ordinary.Reachable && (!ordinary.Occupied || ordinary.SeatedByYou), ordinary.Revision, PlacementId: ordinary.PlacementId);
        }
        if (Eva == null && IsPiloting) return new("leave-pilot", Flight?.StationId ?? Station?.Id ?? "", RecoveringPilot ? "Pilot exit recovery pending" : "Leave control seat", !RecoveringPilot);
        if (Eva == null && NearStation) return new("enter-pilot", Flight?.StationId ?? Station?.Id ?? "", "Control seat", Station?.Operational == true && (Station.OccupantId == null || Station.OccupantId == actor.Id) && (Flight is null ? Location == null : Flight.Active && Flight.FlightAdmitted), Flight?.StationRevision ?? 0);
        return storage == null ? null : StorageInteraction(storage);
    }
    private GameplayInteraction StorageInteraction(ScopedCargoContainerStatus row) => new("open-storage", row.Id, "Open " + row.Name.ToLowerInvariant(), true, row.Revision, ContainerId: row.Id, PlacementId: row.PlacedObjectId);
    public bool EvaSuitEquipped => new[] { (Slot: "uniform", Id: "wardrobe-suit-body"), (Slot: "helmet", Id: "wardrobe-suit-helmet"), (Slot: "back", Id: "wardrobe-suit-pack") }.All(part => Inventory.Items.Any(item => item.EquipmentSlot == part.Slot && item.DefinitionId == part.Id));
    public GameplayInteraction? ResolveSelectedInteraction(string placementId)
    {
        if (Character?.Connected != true || !Alive || Connection == null) return null;
        var storage = Connection.Db.OwnReachableCargoContainers.Iter().FirstOrDefault(row => row.PlacedObjectId == placementId && row.Kind == "grid");
        if (storage != null) return StorageInteraction(storage);
        var row = Connection.Db.OwnInteractions.Iter().FirstOrDefault(row => row.PlacementId == placementId);
        if (row == null)
        {
            if (placementId != Flight?.StationId && placementId != Station?.Id) return null;
            return new(IsPiloting ? "leave-pilot" : "enter-pilot", placementId, IsPiloting ? "Leave control seat" : "Control seat",
                !RecoveringPilot && (IsPiloting || NearStation && Station?.Operational == true && (Station.OccupantId == null || Station.OccupantId == Character?.Id)), PlacementId: placementId);
        }
        var action = row.Kind == "seat" ? row.SeatedByYou ? "stand" : "sit" : row.Enabled ? "set-light-off" : "set-light-on";
        var label = row.Kind == "seat" ? row.SeatedByYou ? "Stand up" : "Sit on " + row.Name.ToLowerInvariant() : row.Enabled ? "Turn grow light off" : "Turn grow light on";
        return new(action, row.Id, label, row.Reachable && (!row.Occupied || row.SeatedByYou) && (row.Kind != "seat" || row.Enabled), row.Revision, PlacementId: row.PlacementId);
    }
    private GameplayInteraction WithPilotRevision(GameplayInteraction action) => action.Kind switch {
        "enter-pilot" => action with { Revision = Flight?.StationRevision ?? 0 },
        "leave-pilot" => action with { Revision = Flight?.SeatRevision ?? 0 },
        _ => action
    };
    private ulong? InteractionAdmissionRevision => Connection?.Db.OwnWorldAdmission.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id)?.Revision;
    private bool QueueInteraction(GameplayInteraction action, string? selectionId, bool fromUi)
    {
        if (!action.Enabled || GameplayPending || active is not { Failed: false } session || Character is not { Connected: true } actor || !Alive ||
            (fromUi ? !uiActionsAllowed : !keyboardAllowed)) return false;
        if (action.Kind == "open-storage") { OpenStorageRequested?.Invoke(action.ContainerId); return true; }
        action = WithPilotRevision(action);
        if (ControlsClaimed)
        {
            var sent = DispatchInteraction(action);
            if (fromUi) ReleaseControls();
            return sent;
        }
        pendingInteraction = new(session, GameplayEpoch, SharedWorldEpoch, actor.Id, Instance?.Id, Instance?.Revision,
            InteractionAdmissionRevision, action, selectionId, SelectedPlacementId, fromUi, Now + 1.5);
        GameplayMessage = "Waiting for interaction control confirmation…";
        ClaimControls();
        return pendingInteraction != null;
    }
    private void TickPendingInteraction()
    {
        if (pendingInteraction is not { } pending) return;
        if (pending.Session != active || pending.Session.Failed || pending.Epoch != GameplayEpoch || pending.WorldEpoch != SharedWorldEpoch ||
            pending.InstanceId != Instance?.Id || pending.InstanceRevision != Instance?.Revision || pending.AdmissionRevision != InteractionAdmissionRevision ||
            pending.SelectionSnapshot != SelectedPlacementId || Character?.Id != pending.ActorId || !Alive ||
            Now > pending.Deadline || (pending.FromUi ? !uiActionsAllowed : !keyboardAllowed))
        { pendingInteraction = null; ReleaseControls(); GameplayMessage = "Interaction canceled. Use the current target again."; return; }
        var current = pending.SelectionId == null ? ResolveInteraction() : ResolveSelectedInteraction(pending.SelectionId);
        if (current == null || !current.Enabled || WithPilotRevision(current) != pending.Action)
        { pendingInteraction = null; ReleaseControls(); GameplayMessage = "Interaction target changed. Use the current target again."; return; }
        if (!ControlsClaimed) return;
        pendingInteraction = null;
        DispatchInteraction(pending.Action);
        // The command and following release share the same ordered socket. UI actions
        // cannot leave continuous movement active behind an open inspector or menu.
        if (pending.FromUi) ReleaseControls();
    }
    private bool DispatchInteraction(GameplayInteraction action)
    {
        switch (action.Kind)
        {
            case "enter-pilot":
                return Flight is { } flight
                    ? Command("enter-pilot", (connection, operation) => connection.Reducers.EnterAuthoredPilot(flight.StationId, flight.StationRevision, operation))
                    : Command("legacy-station", (connection, _) => connection.Reducers.UseStation(), callbackArguments: Array.Empty<string>());
            case "leave-pilot":
                return Flight != null
                    ? Command("leave-pilot", (connection, _) => connection.Reducers.LeaveAuthoredPilot(), callbackArguments: Array.Empty<string>())
                    : Command("legacy-station", (connection, _) => connection.Reducers.UseStation(), callbackArguments: Array.Empty<string>());
            case "ship-button": return Command("ship-button", (connection, _) => connection.Reducers.PressShipButton(action.ShipId, action.Id), callbackArguments: new[] { action.ShipId, action.Id });
            case "airlock-entry": return Command("airlock-entry", (connection, _) => connection.Reducers.EvaCycleAirlock(action.ShipId, action.Id), callbackArguments: new[] { action.ShipId, action.Id });
            default: return Command("interaction", (connection, operation) => connection.Reducers.InteractObject(action.Id, action.Kind, action.Revision, operation));
        }
    }
    public bool InteractSelected(string placementId, bool fromUi = false)
    {
        var action = ResolveSelectedInteraction(placementId); if (action?.Enabled != true) return false;
        return QueueInteraction(action, placementId, fromUi);
    }
    public bool Interact(string? selectedPlacementId = null, bool fromUi = false)
    {
        var selected = selectedPlacementId ?? SelectedPlacementId;
        var action = selected == null ? ResolveInteraction() : ResolveSelectedInteraction(selected);
        if (action?.Enabled != true) return false;
        return QueueInteraction(action, selected, fromUi);
    }
    public void ToggleSeat(bool fromUi = false)
    {
        if (Connection == null || Character?.Connected != true || !Alive || Resting || Eva != null) return;
        if (Flight is { } flight)
        {
            if (flight.SeatState == "recovery-pending") { GameplayMessage = "Pilot exit is obstructed; waiting for a safe exit."; return; }
            if ((flight.SeatState != "none" || NearStation && flight.Active && flight.FlightAdmitted) && ResolveSelectedInteraction(flight.StationId) is { } action)
                QueueInteraction(action, flight.StationId, fromUi);
            else GameplayMessage = "Approach an active admitted pilot station.";
        }
        else if (Location == null && (IsPiloting || NearStation) && Station is { } station && ResolveSelectedInteraction(station.Id) is { } action)
            QueueInteraction(action, station.Id, fromUi);
    }

    public void SetAimAngle(double? localAngle) => aimAngle = localAngle.HasValue && double.IsFinite(localAngle.Value) ? localAngle : null;
    public void SetPointerDirection(double? dx, double? dy)
    {
        var length = dx.HasValue && dy.HasValue ? Math.Sqrt(dx.Value * dx.Value + dy.Value * dy.Value) : 0;
        if (double.IsFinite(length) && length > .001) { pointerDx = dx / length; pointerDy = dy / length; }
        else pointerDx = pointerDy = null;
    }
    public void ToggleCombat() { CombatEnabled = !CombatEnabled; triggerHeld = pendingTrigger = false; if (!CombatEnabled) SetAimAngle(null); }
    private bool CombatAllowed => CombatEnabled && keyboardAllowed && pointerAllowed && Alive && Character?.Connected == true && Connection != null && !IsPiloting && !Resting && (InteriorView || Eva != null);
    public void SetTrigger(bool pressed)
    {
        triggerHeld = pressed && CombatAllowed;
        if (pressed && CombatAllowed && !Reloading) { pendingTrigger = true; triggerWeapon = Combat?.WeaponItemId; }
    }
    private bool Reloading => Connection?.Db.VisibleCombatActions.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id)?.ReloadUntilMicros > (ulong)Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) * 1000;
    private bool CanReload
    {
        get
        {
            if (Combat is not { } combat || Connection == null) return false;
            var pin = Connection.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(row =>
                row.ItemId == combat.WeaponItemId && row.DefinitionId == combat.WeaponDefinitionId);
            return PinnedWeaponDefinitions.SupportsReload(combat.WeaponDefinitionId, pin?.WeaponRevision ?? 1,
                Connection.Db.PublishedItemDefinitions.Iter());
        }
    }
    public bool ReloadWeapon()
    {
        if (!CombatAllowed || !CanReload || Reloading || Combat is not { } combat || combat.Energy >= combat.Capacity || Now - lastReloadAt < 1 || gameplayCommands.Values.Any(command => command.Kind == "reload")) return false;
        lastReloadAt = Now;
        return Command("reload", (connection, operation) => connection.Reducers.ReloadWeapon(combat.WeaponItemId, combat.Revision, operation), false);
    }
    private bool SendCombatAim(bool enabled, double angle)
    {
        if (active is not { Failed: false } session || Connection == null) return false;
        if (aimAttempts.Count >= 4) { Stall(session); return false; }
        var serial = ++aimSerial;
        aimAttempts.Add(new(active, enabled, angle, serial, Now, GameplayEpoch, Combat?.WeaponItemId));
        aimPending = true; aimPreviouslyActive = enabled; lastAimAt = Now;
        try { Connection.Reducers.SetCombatAim(enabled, angle); return active == session && !session.Failed; }
        catch { Stall(session); return false; }
    }
    private void TickCombat()
    {
        var aiming = CombatAllowed && aimAngle.HasValue;
        if (!aiming) { triggerHeld = pendingTrigger = false; triggerWeapon = null; }
        else if (Reloading) { pendingTrigger = false; triggerWeapon = null; }
        if (Connection == null || active == null) return;
        if (aimAttempts.Any(a => a.Session == active && Now - a.Time > 2))
        { Report("Combat confirmation is delayed. Reconnecting before firing."); Stall(active); return; }
        var angle = aiming ? GameplayRules.WrapAngle(aimAngle!.Value - (Eva != null ? CurrentPresentedShip?.Heading ?? 0 : 0)) : 0;
        // The browser awaits aim and fires outside its next render frame. Here SDK
        // receipts are render-pumped: an acknowledged aim may already exceed the
        // server's 300ms lifetime. Refresh immediately before fire on the same ordered
        // socket after the acknowledged/current context gate; never fire on send failure.
        var ready = aiming && !aimPending && acceptedAimActive && acceptedAimSerial == aimSerial;
        if (ready && Combat is { } combat && !Reloading && !gameplayCommands.Values.Any(command => command.Kind is "fire" or "reload"))
        {
            var pressed = pendingTrigger && triggerWeapon == combat.WeaponItemId;
            if (triggerHeld || pressed)
            {
                pendingTrigger = false;
                if (combat.Energy < combat.ShotCost) ReloadWeapon();
                else if (combat.WeaponItemId.Length > 0 && Now - lastFireAt >= combat.CooldownMs / 1000d)
                {
                    var session = active;
                    if (!SendCombatAim(true, angle) || active != session || session.Failed) return;
                    if (Command("fire", (connection, operation) => connection.Reducers.FireWeapon(combat.WeaponItemId, combat.Revision, operation), false)) lastFireAt = Now;
                }
            }
        }
        if (!aimPending && (aiming || aimPreviouslyActive) && Now - lastAimAt >= .1) SendCombatAim(aiming, angle);
    }

    public bool RenameShip(string name)
    {
        name = name.Trim();
        if (Instance != null || Ship is not { } ship || name.Length < 2 || name.Length > 48) return false;
        return Command("rename-ship", (connection, operation) => connection.Reducers.RenameShip(ship.Id, ship.Revision, operation, name));
    }
    private SharedJoinContext ReadSharedJoinContext()
    {
        var connection = Connection;
        var identity = connection?.Identity?.ToString() ?? "";
        if (identity.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) identity = identity[2..];
        return new(connection != null, SharedAdmissionReady, connection?.Db.OwnConstructionLocation.Iter().Any() == true,
            identity, settings.Database, connection?.Db.OwnCharacters.Iter().Select(row => new SharedJoinActor(row.Id, row.ShipId, row.Connected)).ToArray() ?? Array.Empty<SharedJoinActor>(),
            connection?.Db.OwnShips.Iter().Select(row => new SharedJoinShip(row.Id, row.Revision)).ToArray() ?? Array.Empty<SharedJoinShip>(),
            connection?.Db.OwnWorldAdmission.Iter().Select(row => new WorldScopeAdmission(row.CharacterId, row.ShipId, row.SystemId, row.Revision)).ToArray() ?? Array.Empty<WorldScopeAdmission>());
    }
    public bool SharedAdmissionReady => active?.Applied == true;
    public bool CanJoinSharedWorld => SharedWorldJoin.Decide(ReadSharedJoinContext()).Kind == "join";
    public bool SharedJoinPending => sharedJoin.Pending;
    public string SharedJoinPhase => sharedJoin.Phase;
    public string SharedJoinMessage => sharedJoin.Message;
    public bool SharedJoinReviewRequired => sharedJoin.ReviewRequired;
    public bool SharedJoinCleanupFailed => sharedJoin.JournalCleanupFailed;
    public void ConfigureSharedJoinJournal(string directory) => sharedJoin.UseJournal(new FileSharedJoinJournal(directory));
    public bool JoinSharedWorld() => sharedJoin.Join(ReadSharedJoinContext());
    public bool DiscardPendingSharedJoin() => sharedJoin.DiscardPending(ReadSharedJoinContext());

    // Current a356 browser authority retires every device-power qualification in
    // ship-feature-qualification.ts. Disclosed fitting telemetry does not grant edits.
    public bool DevicePowerAvailable => false;
    public string DevicePowerUnavailableReason => "Device power circuits are not qualified for current ship prefabs.";
    public bool SetEnginePower(string placedObjectId, bool connected)
    {
        if (Flight is not { Active: true } flight || Connection?.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.ShipId == flight.ShipId && row.Kind == "actuator" && row.PlacedObjectId == placedObjectId) != true) return false;
        return Command("engine-power", (connection, operation) => connection.Reducers.SetConstructionEnginePower(flight.ShipId, placedObjectId, connected, flight.Revision, operation));
    }
    public bool SetComputerPower(string placedObjectId, bool connected)
    {
        if (Flight is not { Active: true } flight || Connection?.Db.OwnAuthoredFlightPowerFittings.Iter().Any(row => row.ShipId == flight.ShipId && row.Kind == "computer" && row.PlacedObjectId == placedObjectId) != true) return false;
        return Command("computer-power", (connection, operation) => connection.Reducers.SetConstructionComputerPower(flight.ShipId, placedObjectId, connected, flight.Revision, operation));
    }
    public bool SaveAppearance(string json)
    {
        if (Appearance is not { } appearance) return false;
        try { using var document = JsonDocument.Parse(json); if (document.RootElement.ValueKind != JsonValueKind.Object) return false; } catch { return false; }
        return Command("appearance", (connection, operation) => connection.Reducers.SetCharacterAppearance(json, appearance.Revision, operation));
    }
    public bool RequestIdentityLink(string targetIdentity)
    {
        targetIdentity = targetIdentity.Trim(); if (targetIdentity.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) targetIdentity = targetIdentity[2..];
        if (Character is not { } actor || targetIdentity.Length != 64 || targetIdentity.Any(character => !Uri.IsHexDigit(character))) return false;
        return Command("identity-link", (connection, operation) => connection.Reducers.RequestIdentityLink(targetIdentity, actor.Id, operation));
    }
    public bool AcceptIdentityLink(string linkId) => Character == null && Connection?.Db.OwnIdentityLinks.Iter().Any(row => row.Id == linkId && row.Side == "target" && row.Status == "pending") == true &&
        Command("identity-link-accept", (connection, operation) => connection.Reducers.AcceptIdentityLink(linkId, operation));

    public bool SetDoor(string openingId, bool open)
    {
        if (Location is not { } visit || Connection?.Db.OwnConstructionDoors.Iter().FirstOrDefault(row => row.Id == openingId && row.InstanceId == visit.InstanceId && row.DeckId == visit.DeckId) is not { } door) return false;
        return Command("door", (connection, operation) => connection.Reducers.SetConstructionDoor(door.Id, visit.VisitId, door.Revision, open, operation));
    }
    public bool BeginTraversal(string linkId)
    {
        if (Connection?.Db.OwnConstructionTraversalLinks.Iter().FirstOrDefault(row => row.LinkId == linkId && row.CharacterId == Character?.Id && row.AtLanding) is not { } link) return false;
        return Command("traversal", (connection, operation) => connection.Reducers.BeginConstructionTraversal(link.LinkId, link.VisitId, link.LocationRevision, link.InstanceRevision, link.LinkRevision, operation));
    }
    public bool CancelTraversal()
    {
        if (Location is not { } visit || Connection?.Db.OwnConstructionTraversals.Iter().FirstOrDefault(row => row.CharacterId == Character?.Id) is not { } traversal) return false;
        return Command("cancel-traversal", (connection, operation) => connection.Reducers.CancelConstructionTraversal(traversal.TraversalId, visit.VisitId, traversal.Revision, operation));
    }
    public bool BeginFlightReview()
    {
        if (Location is not { } visit || Flight is not { Active: true, FlightAdmitted: false } flight) return false;
        return Command("flight-review", (connection, operation) => connection.Reducers.BeginAuthoredFlightReview(visit.VisitId, visit.Revision, flight.AdmissionRevision, operation));
    }
    public bool EnterReview(string instanceId) => Character is { } actor &&
        Command("review", (connection, operation) => connection.Reducers.EnterConstructionReview(instanceId, actor.ShipId, operation));
    public bool CanReturnFromReview => Location != null && Eva == null && PassengerInterior == null &&
        Connection?.Db.OwnGameShipAccess.Iter().Any(row => row.CharacterId == Character?.Id && row.InstanceId == Location.InstanceId) == false &&
        (Instance == null || Instance.WorkspaceId != "trusted-starter-templates");
    public bool ReturnFromReview()
    {
        if (!CanReturnFromReview || Location is not { } visit || IsPiloting || Resting) return false;
        return Flight is { FlightAdmitted: true } flight
            ? Command("return-review", (connection, operation) => connection.Reducers.ReturnAuthoredFlightReview(visit.VisitId, visit.Revision, flight.AdmissionRevision, operation))
            : Command("return-review", (connection, operation) => connection.Reducers.LeaveConstructionReview(visit.VisitId, visit.Revision, operation));
    }
    public bool EditFurnishing(string sourceObjectId, string action, double dx, double dy, double yaw, bool snap)
    {
        if (Instance is not { } instance || !double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(yaw) || action is not ("move" or "delete" or "snap")) return false;
        return Command("furnishing", (connection, operation) => connection.Reducers.EditShipFurnishing(instance.Id, sourceObjectId, action, dx, dy, yaw, snap, instance.FurnishingRevision, operation));
    }

    private bool TransferCargo(string item, string destination, ulong revision, (int X, int Y, bool Rotated)? exact = null)
    {
        InventoryCargoPlan plan;
        try { plan = InventoryCargoPlan.Create(Inventory, item, destination, exact); }
        catch (InvalidOperationException error) { InventoryMessage = error.Message; return false; }
        if (!InventoryIntent(revision, (connection, operation) => connection.Reducers.TransferScopedCargoItem(operation,
            plan.ItemId, plan.ExpectedItemRevision, plan.SourceContainerId, plan.ExpectedSourceRevision,
            plan.DestinationContainerId, plan.ExpectedDestinationRevision, plan.ExpectedCharacterRevision, plan.X, plan.Y, plan.Rotated))) return false;
        inventoryCargo = plan; return true;
    }
    public bool TransferItem(string item, string container, ulong revision) => Inventory.UsesScopedCargo(item, container)
        ? TransferCargo(item, container, revision)
        : InventoryIntent(revision, (connection, operation) => connection.Reducers.TransferInventoryItem(item, container, revision, operation));
    public bool DropItem(string item, ulong revision) => Inventory.Item(item) is { } target &&
        (target.EquipmentSlot.Length > 0 || Inventory.Container(target.ContainerId)?.Carried == true) &&
        InventoryIntent(revision, (connection, operation) => connection.Reducers.DropInventoryItem(item, revision, operation));
    private bool BeginCargoBatch(string container, string destination, ulong revision)
    {
        if (InventoryPending || inventoryBatch != null || Inventory.Revision != revision || Inventory.Container(container) == null ||
            (destination.Length > 0 && Inventory.Container(destination) == null)) return false;
        inventoryBatch = new(Inventory.Items.Where(item => item.ContainerId == container).Select(item => item.Id));
        inventoryBatchDestination = destination;
        if (inventoryBatch.Count == 0) { inventoryBatch = null; return false; }
        InventoryMessage = "Transferring items with current storage revisions…";
        TickGameplayOperations(); return InventoryPending;
    }
    public bool TakeAll(string container, ulong revision) => Inventory.Container(container)?.IsScopedCargo == true
        ? BeginCargoBatch(container, "", revision)
        : InventoryIntent(revision, (connection, operation) => connection.Reducers.TakeAllInventoryItems(container, revision, operation));
    public bool StoreAll(string container, string destination, ulong revision) => Inventory.Container(container)?.IsScopedCargo == true || Inventory.Container(destination)?.IsScopedCargo == true
        ? BeginCargoBatch(container, destination, revision)
        : InventoryIntent(revision, (connection, operation) => connection.Reducers.StoreAllInventoryItems(container, destination, revision, operation));
    public bool TakeGroundItem(string itemId) => PickupGroundItem(itemId, Inventory.Revision);
    public bool PickupGroundItem(string itemId) => TakeGroundItem(itemId);
    public bool PickupGroundItem(string itemId, ulong expectedRevision)
    {
        var actor = Character;
        var ground = Connection?.Db.OwnGroundItems.Iter().FirstOrDefault(row => row.Id == itemId);
        var location = Location;
        var nativeScene = location != null && Instance?.Id == location.InstanceId;
        var stair = Connection?.Db.OwnConstructionStairWalks.Iter().Any(row => row.CharacterId == actor?.Id) == true;
        var sameScene = nativeScene ? ground?.InstanceId == location!.InstanceId && ground?.DeckId == location.DeckId : location == null && ground?.InstanceId == "" && ground?.DeckId == "";
        if (actor?.Connected != true || !Alive || Eva != null || Resting || IsPiloting || stair || ground?.Reachable != true || !sameScene)
        { InventoryMessage = "Stand within reach of this item on your current deck."; return false; }
        if (!InventoryIntent(expectedRevision, (connection, operation) => connection.Reducers.TransferInventoryItem(itemId, "", expectedRevision, operation))) return false;
        inventoryPickup = (itemId, expectedRevision, actor.Id);
        return true;
    }
    public bool ClaimArmory(ulong revision) => InventoryIntent(revision,
        (connection, operation) => connection.Reducers.ClaimCharacterArmory(revision, operation));

    // Geometry is supplied from the pinned browser content adapter, never hand-authored coordinates.
    private GameplayGeometryCatalog? gameplayGeometry;
    public void LoadGameplayGeometry(string json, string? worldManifestJson = null) => gameplayGeometry = GameplayGeometryCatalog.Parse(json, worldManifestJson);
    private GameplayInteraction? ResolvePanelInteraction() => gameplayGeometry?.Resolve(this);
}
