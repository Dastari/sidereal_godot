using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sidereal.Native.Input;

public sealed record SharedJoinActor(string Id, string ShipId, bool Connected);
public sealed record SharedJoinShip(string Id, ulong Revision);
public sealed record SharedJoinContext(bool Active, bool AdmissionReady, bool ConstructionReview, string Identity, string Database,
    IReadOnlyList<SharedJoinActor> Actors, IReadOnlyList<SharedJoinShip> Ships, IReadOnlyList<WorldScopeAdmission> Admissions);
public sealed record SharedJoinRequest(string CharacterId, string ShipId, ulong ExpectedShipRevision, ulong ExpectedAdmissionRevision, string OperationId);
public sealed record SharedJoinDecision(string Kind, string Message, SharedJoinRequest? Request = null);
public interface ISharedJoinJournal { string? Read(string key); void Write(string key, string value); void Remove(string key); }
public sealed class FileSharedJoinJournal : ISharedJoinJournal
{
    private readonly string directory;
    public FileSharedJoinJournal(string directory) => this.directory = directory;
    private string PathFor(string key) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");
    public string? Read(string key) => File.Exists(PathFor(key)) ? File.ReadAllText(PathFor(key)) : null;
    public void Write(string key, string value)
    {
        Directory.CreateDirectory(directory);
        var target = PathFor(key); var temporary = target + ".tmp";
        File.WriteAllText(temporary, value); File.Move(temporary, target, true);
    }
    public void Remove(string key) { var path = PathFor(key); if (File.Exists(path)) File.Delete(path); }
}

/// <summary>Explicit account/database-scoped entry. Persist operation and exact expected
/// revisions before send; uncertain receipts are retried identically, never on reconnect.</summary>
public sealed class SharedWorldJoin
{
    private ISharedJoinJournal journal;
    private readonly Action<SharedJoinRequest> send;
    private readonly Func<double> now;
    private string? key;
    private SharedJoinRequest? request;
    private double deadline;
    private string? cleanupKey;
    public bool JournalCleanupFailed { get; private set; }
    private const string CleanupMessage = "Saved join cleanup failed. Make local storage writable and retry cleanup explicitly.";
    public string Phase { get; private set; } = "idle";
    public string Message { get; private set; } = "";
    public bool ReviewRequired { get; private set; }
    public bool Pending { get; private set; }
    public SharedWorldJoin(ISharedJoinJournal journal, Action<SharedJoinRequest> send, Func<double> now)
    { this.journal = journal; this.send = send; this.now = now; }
    public void UseJournal(ISharedJoinJournal next) { if (Pending) throw new InvalidOperationException("A join is pending."); journal = next; key = cleanupKey = null; JournalCleanupFailed = false; }
    public static string AccountKey(string database, string identity)
    {
        if (string.IsNullOrWhiteSpace(database) || identity.Length != 64 || identity.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Verified account connection required.");
        return "sidereal.shared-join.v1:" + JsonSerializer.Serialize(new[] { database, identity.ToLowerInvariant() });
    }
    public static SharedJoinDecision Decide(SharedJoinContext context)
    {
        if (!context.Active || !context.AdmissionReady) return new("blocked", "Waiting for shared-world admission…");
        if (context.ConstructionReview) return new("blocked", "Return from construction review first.");
        var actors = context.Actors.Where(a => a.Connected).ToArray();
        if (actors.Length != 1) return new("blocked", "A single active character is required.");
        var actor = actors[0]; var ships = context.Ships.Where(s => s.Id == actor.ShipId).ToArray();
        if (ships.Length != 1) return new("blocked", "The active character’s ship is not available.");
        if (context.Admissions.Count > 1) return new("blocked", "Multiple admissions require explicit character selection.");
        if (context.Admissions.Count == 1)
            return context.Admissions[0].CharacterId == actor.Id && context.Admissions[0].ShipId == actor.ShipId
                ? new("admitted", "Your ship is in the shared system.")
                : new("blocked", "Existing admission belongs to another character or ship; explicit transfer is required.");
        return new("join", "", new(actor.Id, actor.ShipId, ships[0].Revision, 0, ""));
    }
    public static string Encode(SharedJoinRequest request) => JsonSerializer.Serialize(new {
        characterId = request.CharacterId, shipId = request.ShipId,
        expectedShipRevision = request.ExpectedShipRevision.ToString(CultureInfo.InvariantCulture),
        expectedAdmissionRevision = request.ExpectedAdmissionRevision.ToString(CultureInfo.InvariantCulture), operationId = request.OperationId });
    public static SharedJoinRequest? Decode(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text); var root = json.RootElement;
            var character = root.GetProperty("characterId").GetString()!; var ship = root.GetProperty("shipId").GetString()!; var operation = root.GetProperty("operationId").GetString()!;
            if (!Guid.TryParseExact(character, "D", out _) || !Guid.TryParseExact(ship, "D", out _) || !Guid.TryParseExact(operation, "D", out _)) return null;
            ulong Revision(string name)
            {
                var value = root.GetProperty(name).GetString()!;
                if (value.Length == 0 || value.Length > 20 || value.Length > 1 && value[0] == '0' || value.Any(c => c < '0' || c > '9')) throw new FormatException();
                return ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            return new(character, ship, Revision("expectedShipRevision"), Revision("expectedAdmissionRevision"), operation);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentNullException) { return null; }
    }
    private void Set(string phase, string message, bool review = false) { Phase = phase; Message = message; ReviewRequired = review; }
    private bool Select(SharedJoinContext context)
    {
        if (!context.Active || !context.AdmissionReady) return false;
        var next = AccountKey(context.Database, context.Identity);
        if (next != key)
        {
            key = next; Pending = false; request = null; Set("idle", "");
            if (cleanupKey != next) { cleanupKey = null; JournalCleanupFailed = false; }
        }
        return true;
    }
    private bool CleanupSavedRequest(bool retry = false)
    {
        if (key == null) return false;
        if (cleanupKey == key && !retry) return !JournalCleanupFailed;
        // An admitted frame must not repeatedly hit an unavailable filesystem. Explicit
        // Join/Discard clicks may retry, while accepted gameplay remains authoritative.
        cleanupKey = key;
        try { journal.Remove(key); JournalCleanupFailed = false; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { JournalCleanupFailed = true; return false; }
    }
    private void AcceptedAdmission(SharedJoinDecision decision, bool retryCleanup = false)
    {
        Pending = false; request = null;
        var cleaned = CleanupSavedRequest(retryCleanup);
        Set("admitted", cleaned ? decision.Message : decision.Message + " " + CleanupMessage);
    }
    public void Observe(SharedJoinContext context)
    {
        if (!Select(context)) { Pending = false; request = null; key = null; Set("idle", ""); return; }
        var decision = Decide(context);
        if (decision.Kind == "admitted") AcceptedAdmission(decision);
        else if (Pending && now() > deadline) { Pending = false; Set("error", "Join confirmation is delayed. Retry when connected to replay the same saved request."); }
    }
    public bool Join(SharedJoinContext context)
    {
        if (Pending || !Select(context)) return false;
        var decision = Decide(context);
        if (decision.Kind == "blocked") { Set("blocked", decision.Message); return false; }
        if (decision.Kind == "admitted") { AcceptedAdmission(decision, true); return false; }
        try
        {
            var saved = journal.Read(key!); var prior = saved == null ? null : Decode(saved);
            if (saved != null && prior == null) { Set("blocked", "The saved join request is invalid. Review it before trying again.", true); return false; }
            var target = decision.Request!;
            if (prior != null && (prior.CharacterId != target.CharacterId || prior.ShipId != target.ShipId || prior.ExpectedShipRevision != target.ExpectedShipRevision || prior.ExpectedAdmissionRevision != target.ExpectedAdmissionRevision))
            { Set("blocked", "Your ship changed since the saved request. Review the current ship before starting a new join.", true); return false; }
            request = prior ?? target with { OperationId = Guid.NewGuid().ToString("D") };
            if (Decode(Encode(request)) == null) throw new InvalidOperationException("Invalid join operation.");
            journal.Write(key!, Encode(request)); cleanupKey = null; JournalCleanupFailed = false;
            Pending = true; deadline = now() + 10; Set("pending", "Joining the shared system…");
            send(request); return true;
        }
        catch (Exception error) { Pending = false; Set("error", error.Message); return false; }
    }
    public void Result(string operation, bool accepted, string? error)
    {
        if (!Pending || request?.OperationId != operation) return;
        Pending = false; Set(accepted ? "submitted" : "error", accepted ? "Join sent. Waiting for the accepted shared-system view." : error ?? "Could not join. Retry when connected.");
    }
    public bool DiscardPending(SharedJoinContext context)
    {
        if (Pending || !Select(context)) return false;
        var decision = Decide(context);
        if (!CleanupSavedRequest(true))
        {
            Set(decision.Kind == "admitted" ? "admitted" : "error", (decision.Kind == "admitted" ? decision.Message + " " : "") + CleanupMessage, decision.Kind != "admitted");
            return false;
        }
        request = null;
        Set(decision.Kind == "admitted" ? "admitted" : "idle", decision.Kind == "admitted" ? decision.Message : "Review your ship, then choose Join shared system again."); return true;
    }
}
