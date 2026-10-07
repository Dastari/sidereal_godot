using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sidereal.Bindings;
using Sidereal.Native.Input;
using SpacetimeDB;
using SpacetimeDB.ClientApi;

namespace Sidereal.Native;

public sealed record ClientSettings(string GameOrigin, string Database, string Issuer, string ClientId, int CallbackPort)
{
    public bool IsIsolatedFixture => Database.EndsWith("-smoke", StringComparison.Ordinal) &&
        GameOrigin.TrimEnd('/') is "http://127.0.0.1:3131" or "https://sidereal.tail7a58a6.ts.net:8448";
    public static ClientSettings Parse(string json)
    {
        var config = JsonSerializer.Deserialize<ClientSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Missing client settings.");
        var origin = new Uri(config.GameOrigin);
        if (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback))
            throw new InvalidOperationException("Use HTTPS for the game server.");
        if (new Uri(config.Issuer).Scheme != "https" || string.IsNullOrWhiteSpace(config.Database) ||
            config.ClientId != "sidereal-game" || config.CallbackPort != 43817)
            throw new InvalidOperationException("Invalid native sign-in settings.");
        return config;
    }
}

// Double subtraction happens before any renderer float conversion.
public readonly record struct RelativePose(double X, double Height, double Z)
{
    public static RelativePose World(double x, double y, double originX, double originY, double height = 0) => new(x - originX, height, -(y - originY));
}

public sealed partial class ClientCore : IDisposable
{
    private sealed class SessionRejectedException : Exception { }
    private sealed class Session : IDisposable
    {
        public DbConnection? Connection;
        public SubscriptionHandle? Subscription;
        public readonly CancellationTokenSource Cancellation = new();
        public Task? Proof;
        public bool Subscribing, Applied, Failed;
        public ControlLease? Lease;
        public IntentTransmitter? Transmitter;
        public SharedWorldScopes? WorldScopes;
        public ulong WorldScopeEpoch;
        public ulong InventoryGeneration;
        public DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        public void Dispose()
        {
            Cancellation.Cancel();
            Lease?.Dispose(); Transmitter?.Dispose(); WorldScopes?.Dispose();
            // Disconnect releases all server subscriptions atomically on this socket.
            Connection?.Disconnect();
            Subscription = null;
            Cancellation.Dispose();
        }
    }

    private readonly ClientSettings settings;
    private Session? active, pending;
    private string? token;
    private bool oidc, controls;
    private DateTimeOffset retryAt;
    private bool replacementNeeded;
    private ulong inventorySessionCounter;
    public string Status { get; private set; } = "Sign in to connect your character.";
    public DbConnection? Connection => active?.Applied == true ? active.Connection : null;
    public Character? Character => ReadCharacter();
    public OwnedShip? Ship => Connection?.Db.OwnShips.Iter().FirstOrDefault(s => s.Id == Character?.ShipId);
    public ConstructionLocationStatus? Location => Connection?.Db.OwnConstructionLocation.Iter().FirstOrDefault(v => v.CharacterId == Character?.Id);
    public ConstructionInstanceStatus? Instance => Connection?.Db.OwnConstructionInstances.Iter().FirstOrDefault(i => i.Id == (Location?.InstanceId ?? Eva?.ExitShipId));
    public AuthoredFlightStatus? Flight => Connection?.Db.OwnAuthoredFlights.Iter().FirstOrDefault(f => f.ShipId == Character?.ShipId);
    public OwnConstructionSeatStatus? Seat => Connection?.Db.OwnConstructionSeat.Iter().FirstOrDefault(v => v.CharacterId == Character?.Id && v.InstanceId == Location?.InstanceId && v.DeckId == Location.DeckId);
    public Station? Station => Connection?.Db.OwnStations.Iter().FirstOrDefault(s => s.ShipId == Character?.ShipId);
    public CharacterVitalsStatus? Vitals => Connection?.Db.OwnCharacterVitals.Iter().FirstOrDefault(v => v.CharacterId == Character?.Id);
    public ShipPowerSummary? Power => Connection?.Db.OwnShipPower.Iter().FirstOrDefault(p => p.ShipId == Character?.ShipId);
    public CombatStatus? Combat => Connection?.Db.OwnCombat.Iter().FirstOrDefault(c => c.CharacterId == Character?.Id);
    public bool IsPiloting => Character != null && (Station?.OccupantId == Character.Id || Flight?.SeatState is "seated" or "recovery-pending");
    public bool ControlsClaimed => controls;
    public string? DevelopmentToken { get; private set; }
    public event Action<string>? Error;
    private InventorySnapshot? inventorySnapshot;
    public InventorySnapshot Inventory => inventorySnapshot ??= InventoryCatalog.Read(Connection);
    private readonly InventoryAttemptState inventoryAttempts = new();
    private InventoryAttemptSnapshot? inventoryReportedAttempt;
    public InventoryAttemptSnapshot? InventoryAttempt => inventoryAttempts.Snapshot;
    public ulong InventorySessionGeneration => active?.InventoryGeneration ?? 0;
    public bool InventoryPending => inventoryAttempts.Pending;
    public string InventoryMessage { get; private set; } = "Drag an item to move it. R rotates while dragging.";
    private sealed record EnterAttempt(Session Session, string Name, double Deadline);
    private EnterAttempt? enterAttempt;
    private bool enterReceiptConfirmed;
    public string EnterPhase { get; private set; } = "idle";
    public string EnterMessage { get; private set; } = "Choose a character name and enter.";
    public bool EnterPending => enterAttempt != null;
    public bool EnterFailed => EnterPhase is "failed" or "uncertain";
    public ulong EnterEpoch { get; private set; }

    public ClientCore(ClientSettings settings)
    {
        this.settings = settings;
        sharedJoin = new(new FileSharedJoinJournal(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidereal", "shared-join-v1")),
            request => { if (Connection == null) throw new InvalidOperationException("Connection changed. Retry when connected.");
                Connection.Reducers.JoinSharedSystem(request.CharacterId, request.ShipId, request.ExpectedShipRevision, request.ExpectedAdmissionRevision, request.OperationId); }, () => Now);
    }

    public void Connect(string credential, bool provider = true)
    {
        token = string.IsNullOrEmpty(credential) ? null : credential;
        oidc = provider;
        Open();
    }

    private void Open()
    {
        pending?.Dispose();
        var session = new Session { InventoryGeneration = ++inventorySessionCounter };
        pending = session;
        Status = active == null ? "Connecting to your world…" : "Renewing game session…";
        session.Connection = DbConnection.Builder()
            .WithUri(settings.GameOrigin)
            .WithDatabaseName(settings.Database)
            .WithToken(token)
            .OnConnect((conn, _, returnedToken) => {
                if (pending != session) return;
                if (!oidc) DevelopmentToken = returnedToken;
                session.Proof = oidc ? BindProof(conn, token!, session.Cancellation.Token) : Task.CompletedTask;
            })
            .OnConnectError(error => {
                session.Failed = true;
                Report("Could not connect. Check Tailscale and sign in again.");
            })
            .OnDisconnect((_, error) => {
                session.Failed = true;
                if (active == session) { controls = false; Status = "Connection lost. Reconnecting…"; }
            }).Build();
        session.Connection.OnUnhandledReducerError += (_, failure) => { if (active == session) Report("Rejected: " + SafeReason(failure.Message)); };
        session.Connection.Reducers.OnEnterLab += (context, name) => {
            if (active != session || !IsCaller(session, context) || enterAttempt is not { } attempt || attempt.Session != session || attempt.Name != name) return;
            if (Accepted(context))
            { enterReceiptConfirmed = true; EnterMessage = "Character accepted. Waiting for your subscribed character…"; ObserveEnter(); }
            else CompleteEnter("failed", "Character entry refused: " + RejectedReason(context));
        };
        InitializeGameplaySession(session);
        session.Connection.Reducers.OnMoveInventoryItem += (ctx, _, _, _, _, _, _, operation) => { if (active == session && IsCaller(session, ctx)) InventoryResult(ctx, operation); };
        session.Connection.Reducers.OnEquipInventoryItem += (ctx, _, _, operation) => { if (active == session && IsCaller(session, ctx)) InventoryResult(ctx, operation); };
        session.Connection.Reducers.OnAssignInventoryHotbar += (ctx, _, _, _, operation) => { if (active == session && IsCaller(session, ctx)) InventoryResult(ctx, operation); };
        session.Connection.Reducers.OnActivateInventoryHotbar += (ctx, _, _, operation) => { if (active == session && IsCaller(session, ctx)) InventoryResult(ctx, operation); };
    }

    private async Task BindProof(DbConnection connection, string providerToken, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            settings.GameOrigin.TrimEnd('/') + "/v1/database/" + Uri.EscapeDataString(settings.Database) + "/call/bind_game_session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new[] { connection.ConnectionId.ToString().ToLowerInvariant() }), Encoding.UTF8, "application/json");
        // The pinned host matches this MIME type exactly, without a charset parameter.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, timeout.Token);
        if ((int)response.StatusCode is 400 or 401 or 403) throw new SessionRejectedException();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Game session proof is unavailable.", null, response.StatusCode);
    }

    // Called by exactly one owner: Godot's main thread, or a console test pump.
    public void Tick()
    {
        inventorySnapshot = null;
        var old = active;
        var next = pending;
        old?.Connection?.FrameTick();
        next?.Connection?.FrameTick();
        if (next != null && pending == next)
        {
            if (next.Failed || DateTimeOffset.UtcNow > next.Deadline || next.Proof?.IsFaulted == true || next.Proof?.IsCanceled == true)
            {
                next.Dispose();
                pending = null;
                replacementNeeded = true;
                retryAt = DateTimeOffset.UtcNow.AddSeconds(3);
                if (next.Proof?.IsFaulted == true)
                {
                    if (next.Proof.Exception!.GetBaseException() is SessionRejectedException)
                    { token = null; Report("Game session verification was rejected. Sign in again."); }
                    else Report(next.Proof.Exception.GetBaseException() is HttpRequestException { StatusCode: { } status }
                        ? $"Game session verification returned HTTP {(int)status}. Retrying…"
                        : "Game session verification is delayed. Retrying…");
                }
            }
            else if (next.Proof?.IsCompletedSuccessfully == true && !next.Subscribing)
            {
                next.Subscribing = true;
                next.Subscription = next.Connection!.SubscriptionBuilder()
                    .OnApplied(_ => next.Applied = true)
                    .OnError((_, _) => { next.Failed = true; Report("World subscription failed. The client may need an update."); })
                    .Subscribe(GameplaySubscriptions);
            }
            if (pending == next && next.Applied && !next.Failed)
            {
                var wantedControl = active?.Lease?.Wanted == true;
                ReleaseControls();
                active = next;
                pending = null;
                replacementNeeded = false;
                old?.Dispose();
                inventoryAttempts.SessionChanged("Session changed. Check the accepted inventory before explicitly trying again.");
                RefreshInventoryMessage();
                ResetGameplayContext();
                Status = "Connected. Server authority active.";
                if (wantedControl) ClaimControls();
            }
        }
        if (active?.Failed == true)
        {
            inventoryAttempts.SessionChanged("Connection lost. Reconnect and review the accepted inventory before explicitly trying again.");
            RefreshInventoryMessage();
            active.Dispose(); active = null; controls = false; ResetGameplayContext();
            retryAt = DateTimeOffset.UtcNow.AddSeconds(3);
        }
        if ((active == null || replacementNeeded) && pending == null && token != null && DateTimeOffset.UtcNow >= retryAt) Open();
        ObserveInventoryAttempt();
        inventoryAttempts.Tick(Now);
        RefreshInventoryMessage();
        active?.Lease?.Tick(); controls = active?.Lease?.CanSend == true;
        UpdateWorldScopes();
        sharedJoin.Observe(ReadSharedJoinContext());
        TickGameplayOperations();
        ObserveEnter();
        inventorySnapshot = null;
    }

    private void Report(string message) { Status = message; Error?.Invoke(message); }
    private void RefreshInventoryMessage()
    {
        if (inventoryAttempts.Snapshot is { } attempt && !ReferenceEquals(attempt, inventoryReportedAttempt))
        { inventoryReportedAttempt = attempt; InventoryMessage = attempt.Reason; }
    }
    private void InventoryResult(ReducerEventContext context, string operation)
    {
        var session = active;
        if (session == null || Character?.Id is not { } actor || !IsCaller(session, context)) return;
        switch (context.Event.Status)
        {
            case SpacetimeDB.Status.Committed:
                if (inventoryAttempts.Receipt(session.InventoryGeneration, actor, operation, true, true)) ObserveInventoryAttempt();
                break;
            case SpacetimeDB.Status.Failed(var reason):
                inventoryAttempts.Receipt(session.InventoryGeneration, actor, operation, true, false, reason); break;
            default: inventoryAttempts.Receipt(session.InventoryGeneration, actor, operation, true, false, "The server could not apply the change."); break;
        }
        RefreshInventoryMessage();
    }
    private InventoryAttemptRows ReadInventoryAttemptRows()
    {
        var connection = Connection;
        var pins = connection?.Db.OwnItemDefinitionPins.Iter().ToDictionary(pin => pin.ItemId,
            pin => (pin.DefinitionId, pin.ItemRevision, pin.WeaponRevision)) ?? new();
        var source = inventoryAttempts.Snapshot?.Source;
        var ground = connection?.Db.OwnGroundItems.Iter().Where(row => source == null || row.InstanceId == source.InstanceId && row.DeckId == source.DeckId)
            .Select(row => row.Id).ToHashSet(StringComparer.Ordinal) ?? new();
        return new(InventorySessionGeneration, Character?.Id ?? "", InventoryCatalog.Read(connection), pins, ground);
    }
    private void ObserveInventoryAttempt()
    {
        if (inventoryAttempts.Snapshot is not { } attempt || attempt.Phase is InventoryAttemptPhase.Confirmed or InventoryAttemptPhase.Rejected or InventoryAttemptPhase.SessionChanged) return;
        if (Connection == null || InventorySessionGeneration != attempt.SessionGeneration || Character?.Id != attempt.ActorId)
        { inventoryAttempts.SessionChanged("The actor or connection changed. Review the accepted inventory before explicitly trying again."); return; }
        var rows = ReadInventoryAttemptRows();
        inventoryAttempts.Observe(rows);
        if (!inventoryAttempts.CargoTransferReady) return;
        // The browser's two-intent route uses the next accepted character revision.
        // Never resubmit the transfer or undo its committed item movement.
        var source = attempt.Source;
        var location = Location;
        if (source == null || Character?.Connected != true || !Alive || rows.Inventory.Item(source.ItemId) is not { } item ||
            rows.Inventory.Container(item.ContainerId)?.Carried != true || !InventoryAttemptState.PinMatches(source, rows) ||
            (location?.InstanceId ?? "") != source.InstanceId || (location?.DeckId ?? "") != source.DeckId)
        { inventoryAttempts.Uncertain("Cargo transfer completed, but equip context or item access changed. Choose the carried item again when ready."); return; }
        if (rows.Inventory.Revision <= attempt.Expected.InventoryRevision) return;
        var operation = Guid.NewGuid().ToString("D");
        var equipExpected = new InventoryAttemptRevisions(rows.Inventory.Revision, item.ScopedRevision,
            rows.Inventory.Container(item.ContainerId)?.ScopedRevision);
        if (!inventoryAttempts.AdvanceCargoEquip(operation, equipExpected, Now)) return;
        try { Connection.Reducers.EquipInventoryItem(source.ItemId, rows.Inventory.Revision, operation); }
        catch { inventoryAttempts.Uncertain("The connection changed while sending equip. Review the accepted carried inventory before explicitly trying again."); }
    }
    private InventoryAttemptSource? CaptureInventorySource(string itemId, InventorySnapshot snapshot)
    {
        var item = snapshot.Item(itemId);
        if (item?.Definition is not { } definition) return null;
        var pin = Connection?.Db.OwnItemDefinitionPins.Iter().FirstOrDefault(row => row.ItemId == itemId);
        if (pin != null && (pin.DefinitionId != item.DefinitionId || pin.ItemRevision != definition.Revision)) return null;
        return new(item.Id, item.DefinitionId, definition.Revision, pin?.WeaponRevision > 0 ? item.DefinitionId : "", pin?.WeaponRevision ?? 0,
            item.ContainerId, item.EquipmentSlot, item.X, item.Y, item.Rotated, snapshot.Container(item.ContainerId)?.PlacementId ?? "",
            Location?.InstanceId ?? "", Location?.DeckId ?? "");
    }
    private bool InventoryIntent(InventoryAttemptKind kind, string itemId, ulong expectedRevision, InventoryAttemptTarget target,
        Action<DbConnection, string> send, InventoryCargoPlan? cargo = null, bool composeEquip = false, IEnumerable<string>? sourceItems = null)
    {
        var snapshot = Inventory;
        if (InventoryPending) return false;
        if (Connection == null || Character?.Connected != true || !snapshot.Available) { InventoryMessage = "Connect and enter the world to manage your inventory."; return false; }
        if (expectedRevision != snapshot.Revision) { InventoryMessage = "Inventory changed while you were choosing. Review the updated items and try again."; return false; }
        var source = itemId.Length == 0 ? null : CaptureInventorySource(itemId, snapshot);
        if (itemId.Length != 0 && source == null) { InventoryMessage = "The item or its exact definition pin is unavailable. Refresh inventory before trying again."; return false; }
        var sources = sourceItems?.Select(id => CaptureInventorySource(id, snapshot)).ToArray();
        if (sources?.Any(value => value == null) == true) { InventoryMessage = "Storage item definitions changed. Refresh storage before trying again."; return false; }
        var expected = new InventoryAttemptRevisions(expectedRevision, cargo?.ExpectedItemRevision ?? snapshot.Item(itemId)?.ScopedRevision,
            cargo?.ExpectedSourceRevision ?? snapshot.Container(source?.ContainerId ?? "")?.ScopedRevision,
            cargo?.ExpectedDestinationRevision ?? snapshot.Container(target.ContainerId)?.ScopedRevision, cargo != null);
        var stageTarget = cargo == null ? target : new InventoryAttemptTarget(cargo.DestinationContainerId, cargo.X, cargo.Y, cargo.Rotated, ItemId: cargo.ItemId);
        var operation = Guid.NewGuid().ToString("D");
        if (!inventoryAttempts.Begin(InventorySessionGeneration, Character.Id, kind, source, expected, target, operation, Now,
            composeEquip ? InventoryAttemptStage.CargoTransfer : InventoryAttemptStage.Single, stageTarget, sources?.Select(value => value!))) return false;
        RefreshInventoryMessage();
        try { send(Connection, operation); }
        catch { inventoryAttempts.Uncertain("The connection changed while sending. Reconnect and review the accepted inventory before explicitly trying again."); RefreshInventoryMessage(); return false; }
        return true;
    }
    public bool MoveItem(string item, string container, int x, int y, bool rotated, ulong revision) =>
        Inventory.UsesScopedCargo(item, container) ? TransferCargo(item, container, revision, (x, y, rotated)) :
            InventoryIntent(InventoryAttemptKind.Move, item, revision, new(container, x, y, rotated, ItemId: item),
                (connection, operation) => connection.Reducers.MoveInventoryItem(item, container, x, y, rotated, revision, operation));
    public bool EquipItem(string item, ulong revision)
    {
        var slot = Inventory.Item(item)?.Definition?.EquipSlot;
        if (string.IsNullOrEmpty(slot)) { InventoryMessage = "This item's exact definition does not support equipment."; return false; }
        var target = new InventoryAttemptTarget(EquipmentSlot: slot, ItemId: item, DestinationKind: InventoryDestinationKind.Equipment);
        return Inventory.UsesScopedCargo(item, "") ? TransferCargo(item, "", revision, composeEquip: true, finalTarget: target) :
            InventoryIntent(InventoryAttemptKind.Equip, item, revision, target, (connection, operation) => connection.Reducers.EquipInventoryItem(item, revision, operation));
    }
    public bool AssignHotbar(byte slot, string item, ulong revision) => slot <= 4 &&
        InventoryIntent(InventoryAttemptKind.AssignHotbar, item, revision, new(HotbarSlot: slot, ItemId: item, DestinationKind: InventoryDestinationKind.Binding),
            (connection, operation) => connection.Reducers.AssignInventoryHotbar(slot, item, revision, operation));
    public bool ActivateHotbar(byte slot, ulong revision)
    {
        if (slot > 4 || !Inventory.Hotbar.TryGetValue(slot, out var item) || Inventory.Item(item)?.Definition?.EquipSlot is not { Length: > 0 } equipment) return false;
        return InventoryIntent(InventoryAttemptKind.ActivateHotbar, item, revision, new(EquipmentSlot: equipment, HotbarSlot: slot, ItemId: item, DestinationKind: InventoryDestinationKind.Equipment),
            (connection, operation) => connection.Reducers.ActivateInventoryHotbar(slot, revision, operation));
    }
    public void ClaimStarterKit() => Connection?.Reducers.ClaimStarterKit();
    public void Enter(string name) => TryEnter(name);
    public bool TryEnter(string name)
    {
        if (enterAttempt != null) return false;
        if (Connection == null || active is not { Failed: false } session)
        { CompleteEnter("uncertain", "Reconnect before explicitly retrying character entry."); return false; }
        name = name.Trim();
        if (name.Length < 2 || name.Length > 40)
        { CompleteEnter("failed", "Use a character name between 2 and 40 characters."); return false; }
        enterAttempt = new(session, name, Now + 10); enterReceiptConfirmed = false; EnterPhase = "pending"; EnterMessage = "Waiting for the server to accept your character…"; EnterEpoch++;
        try { Connection.Reducers.EnterLab(name); }
        catch { Stall(session); CompleteEnter("uncertain", "Character confirmation was interrupted. Reconnect, then explicitly retry."); return false; }
        return true;
    }
    private void ObserveEnter()
    {
        if (enterAttempt is not { } attempt) return;
        if (enterReceiptConfirmed && Character?.Connected == true) { CompleteEnter("confirmed", "Character confirmed by the server."); return; }
        if (active != attempt.Session || attempt.Session.Failed || Connection == null)
        { CompleteEnter("uncertain", "Character confirmation was interrupted. Reconnect, then explicitly retry."); return; }
        if (Now <= attempt.Deadline) return;
        // EnterLab has no operation ID. Retire this socket before another explicit attempt
        // so a late receipt for the same name cannot confirm a newer request.
        Stall(attempt.Session);
        CompleteEnter("uncertain", "Character confirmation is delayed. Reconnect, then explicitly retry.");
    }
    private void CompleteEnter(string phase, string message)
    { enterAttempt = null; enterReceiptConfirmed = false; EnterPhase = phase; EnterMessage = message; EnterEpoch++; }
    public void Dispose()
    {
        ReleaseControls();
        active?.Dispose(); pending?.Dispose();
        active = pending = null; token = null; ResetGameplayContext();
        inventorySnapshot = null;
        inventoryAttempts.SessionChanged("Signed out. Sign in and review the accepted inventory before explicitly trying again.");
        RefreshInventoryMessage();
        Status = "Signed out.";
        CompleteEnter("idle", "Choose a character name and enter.");
    }
}
