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

public sealed class ClientCore : IDisposable
{
    private sealed class SessionRejectedException : Exception { }
    private sealed class Session : IDisposable
    {
        public DbConnection? Connection;
        public SubscriptionHandle? Subscription;
        public readonly CancellationTokenSource Cancellation = new();
        public Task? Proof;
        public bool Subscribing, Applied, Failed;
        public ulong Sequence;
        public DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        public void Dispose()
        {
            Cancellation.Cancel();
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
    public string Status { get; private set; } = "Sign in to connect your character.";
    public DbConnection? Connection => active?.Applied == true ? active.Connection : null;
    public Character? Character => Connection?.Db.OwnCharacters.Iter().FirstOrDefault();
    public OwnedShip? Ship => Connection?.Db.OwnShips.Iter().FirstOrDefault(s => s.Id == Character?.ShipId);
    public ConstructionLocationStatus? Location => Connection?.Db.OwnConstructionLocation.Iter().FirstOrDefault();
    public ConstructionInstanceStatus? Instance => Connection?.Db.OwnConstructionInstances.Iter().FirstOrDefault(i => i.Id == Location?.InstanceId);
    public AuthoredFlightStatus? Flight => Connection?.Db.OwnAuthoredFlights.Iter().FirstOrDefault(f => f.ShipId == Character?.ShipId);
    public OwnConstructionSeatStatus? Seat => Connection?.Db.OwnConstructionSeat.Iter().FirstOrDefault();
    public Station? Station => Connection?.Db.OwnStations.Iter().FirstOrDefault(s => s.ShipId == Character?.ShipId);
    public CharacterVitalsStatus? Vitals => Connection?.Db.OwnCharacterVitals.Iter().FirstOrDefault(v => v.CharacterId == Character?.Id);
    public ShipPowerSummary? Power => Connection?.Db.OwnShipPower.Iter().FirstOrDefault(p => p.ShipId == Character?.ShipId);
    public CombatStatus? Combat => Connection?.Db.OwnCombat.Iter().FirstOrDefault(c => c.CharacterId == Character?.Id);
    public bool IsPiloting => Character != null && Station?.OccupantId == Character.Id;
    public bool ControlsClaimed => controls;
    public string? DevelopmentToken { get; private set; }
    public event Action<string>? Error;
    private InventorySnapshot? inventorySnapshot;
    public InventorySnapshot Inventory => inventorySnapshot ??= InventoryCatalog.Read(Connection);
    public bool InventoryPending { get; private set; }
    public string InventoryMessage { get; private set; } = "Drag an item to move it. R rotates while dragging.";
    private string? inventoryOperation;
    private DateTimeOffset inventoryDeadline;

    public ClientCore(ClientSettings settings) => this.settings = settings;

    public void Connect(string credential, bool provider = true)
    {
        token = string.IsNullOrEmpty(credential) ? null : credential;
        oidc = provider;
        Open();
    }

    private void Open()
    {
        pending?.Dispose();
        var session = new Session();
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
        session.Connection.OnUnhandledReducerError += (_, _) =>
            Report("The server rejected that action. Check your position and control access.");
        session.Connection.Reducers.OnMoveInventoryItem += (ctx, _, _, _, _, _, _, operation) => InventoryResult(ctx, operation);
        session.Connection.Reducers.OnEquipInventoryItem += (ctx, _, _, operation) => InventoryResult(ctx, operation);
        session.Connection.Reducers.OnAssignInventoryHotbar += (ctx, _, _, _, operation) => InventoryResult(ctx, operation);
        session.Connection.Reducers.OnActivateInventoryHotbar += (ctx, _, _, operation) => InventoryResult(ctx, operation);
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
                    .Subscribe(new[] {
                        "SELECT * FROM own_characters", "SELECT * FROM own_ships", "SELECT * FROM own_stations",
                        "SELECT * FROM own_construction_instances", "SELECT * FROM own_construction_location",
                        "SELECT * FROM own_construction_seat", "SELECT * FROM own_authored_flights",
                        "SELECT * FROM own_interactions", "SELECT * FROM own_inventory_state",
                        "SELECT * FROM own_character_vitals", "SELECT * FROM own_ship_power", "SELECT * FROM own_combat",
                        "SELECT * FROM own_inventory_items", "SELECT * FROM own_inventory_containers",
                        "SELECT * FROM own_inventory_hotbar", "SELECT * FROM own_item_definition_pins",
                        "SELECT * FROM published_item_definitions"
                    });
            }
            if (pending == next && next.Applied && !next.Failed)
            {
                var wantedControl = controls;
                ReleaseControls();
                active = next;
                pending = null;
                replacementNeeded = false;
                old?.Dispose();
                if (InventoryPending) FinishInventory("Session changed. Check the current inventory before trying again.");
                Status = "Connected. Server authority active.";
                if (wantedControl) ClaimControls();
            }
        }
        if (active?.Failed == true)
        {
            active.Dispose(); active = null; controls = false;
            retryAt = DateTimeOffset.UtcNow.AddSeconds(3);
        }
        if ((active == null || replacementNeeded) && pending == null && token != null && DateTimeOffset.UtcNow >= retryAt) Open();
        if (InventoryPending && (Connection == null || DateTimeOffset.UtcNow > inventoryDeadline))
            FinishInventory("Confirmation is delayed. Check the current inventory and reconnect before retrying.");
        inventorySnapshot = null;
    }

    private void Report(string message) { Status = message; Error?.Invoke(message); }
    private void FinishInventory(string message)
    { InventoryPending = false; inventoryOperation = null; InventoryMessage = message; }
    private void InventoryResult(ReducerEventContext context, string operation)
    {
        if (operation != inventoryOperation) return;
        switch (context.Event.Status)
        {
            case SpacetimeDB.Status.Committed: FinishInventory("Inventory confirmed by the server."); break;
            case SpacetimeDB.Status.Failed(var reason):
                FinishInventory("Rejected: " + reason.Replace('\n', ' ').Replace('\r', ' ')[..Math.Min(reason.Length, 240)] + " Review the updated inventory and try again."); break;
            default: FinishInventory("The server could not apply that change. Review the updated inventory and try again."); break;
        }
    }
    private bool InventoryIntent(ulong expectedRevision, Action<DbConnection, string> send)
    {
        var snapshot = Inventory;
        if (InventoryPending) return false;
        if (Connection == null || !snapshot.Available) { InventoryMessage = "Connect and enter the world to manage your inventory."; return false; }
        if (expectedRevision != snapshot.Revision) { InventoryMessage = "Inventory changed while you were choosing. Review the updated items and try again."; return false; }
        inventoryOperation = "godot:" + Guid.NewGuid().ToString("N");
        InventoryPending = true; inventoryDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        InventoryMessage = "Waiting for server confirmation…";
        try { send(Connection, inventoryOperation); }
        catch { FinishInventory("The connection changed. Reconnect and review your inventory before retrying."); return false; }
        return true;
    }
    public bool MoveItem(string item, string container, int x, int y, bool rotated, ulong revision) =>
        InventoryIntent(revision, (connection, operation) => connection.Reducers.MoveInventoryItem(item, container, x, y, rotated, revision, operation));
    public bool EquipItem(string item, ulong revision) =>
        InventoryIntent(revision, (connection, operation) => connection.Reducers.EquipInventoryItem(item, revision, operation));
    public bool AssignHotbar(byte slot, string item, ulong revision) => slot <= 4 &&
        InventoryIntent(revision, (connection, operation) => connection.Reducers.AssignInventoryHotbar(slot, item, revision, operation));
    public bool ActivateHotbar(byte slot, ulong revision) => slot <= 4 &&
        InventoryIntent(revision, (connection, operation) => connection.Reducers.ActivateInventoryHotbar(slot, revision, operation));
    public void ClaimStarterKit() => Connection?.Reducers.ClaimStarterKit();
    public void Enter(string name) => Connection?.Reducers.EnterLab(name);
    public void ClaimControls()
    {
        if (Connection == null || Character?.Connected != true) return;
        Connection.Reducers.ClaimInputControl();
        controls = true;
    }
    public void ReleaseControls()
    {
        if (controls && Connection != null)
        {
            SendIntent(0, 0, 0, 0, false);
            Connection.Reducers.ReleaseInputControl();
        }
        controls = false;
    }
    public void SendIntent(double throttle, double turn, double dx, double dy, bool sprint)
    {
        if (!controls || active == null || Connection == null) return;
        Connection.Reducers.SetIntent(++active.Sequence, throttle, turn, dx, dy, sprint);
    }
    public void ToggleSeat()
    {
        if (Connection == null) return;
        if (Flight is { } flight)
        {
            if (flight.SeatState != "none") Connection.Reducers.LeaveAuthoredPilot();
            else Connection.Reducers.EnterAuthoredPilot(flight.StationId, flight.StationRevision, "godot:" + Guid.NewGuid().ToString("N"));
        }
        else Connection.Reducers.UseStation();
    }
    public void Dispose()
    {
        ReleaseControls();
        active?.Dispose(); pending?.Dispose();
        active = pending = null; token = null;
        inventorySnapshot = null;
        FinishInventory("Sign in to manage your inventory.");
        Status = "Signed out.";
    }
}
