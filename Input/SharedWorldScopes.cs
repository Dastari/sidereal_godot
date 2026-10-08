using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Sidereal.Native.Input;

public interface IWorldScopeHandle { void End(Action ended); }
public sealed record WorldScopeAdmission(string CharacterId, string ShipId, string SystemId, ulong Revision);
public sealed record WorldScopeMotion(string ShipId, string SystemId, double X, double Y, long CellX, long CellY, ulong Tick);
public sealed record WorldScopeEva(string CharacterId, string SystemId, double X, double Y, ulong Tick, ulong Revision);

/// <summary>Socket-local coverage from accepted motion. At most two cell sets,
/// including pending apply and pending retirement; rapid crossings keep only the latest target.</summary>
public sealed class SharedWorldScopes : IDisposable
{
    private sealed class Scope
    {
        public string Kind = "", Key = "";
        public IWorldScopeHandle? Handle;
        public bool Applied, Retiring;
        public ulong Epoch;
    }
    private readonly Func<string[], Action, Action, IWorldScopeHandle> subscribe;
    private readonly Action changed, failed;
    private readonly List<Scope> scopes = new();
    private WorldScopeAdmission? admission;
    private WorldScopeMotion? motion;
    private WorldScopeEva? eva;
    private string? desired;
    private bool disposed, pumping;
    public ulong Epoch { get; private set; }
    public bool Ready => admission != null && scopes.Any(s => s.Kind == "own" && s.Applied && !s.Retiring && s.Epoch == Epoch) && scopes.Any(s => s.Kind == "cells" && s.Applied && !s.Retiring && s.Epoch == Epoch);
    public int CellSets => scopes.Count(s => s.Kind == "cells");
    public int Pending => scopes.Count(s => !s.Applied && !s.Retiring);
    public string? DesiredCell => desired;
    public SharedWorldScopes(Func<string[], Action, Action, IWorldScopeHandle> subscribe, Action changed, Action failed)
    { this.subscribe = subscribe; this.changed = changed; this.failed = failed; }
    private static bool Id(string value) => Guid.TryParseExact(value, "D", out _);
    private static string Quote(string value) => Id(value) ? "'" + value + "'" : throw new ArgumentException("Invalid world identifier.");
    private static (long X, long Y)? Cell(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return null;
        var cx = Math.Floor(x / 400); var cy = Math.Floor(y / 400);
        return Math.Abs(cx) < 9007199254740990d && Math.Abs(cy) < 9007199254740990d ? ((long)cx, (long)cy) : null;
    }
    public static string[] CellQueries(string systemId, double x, double y)
    {
        var cell = Cell(x, y) ?? throw new ArgumentException("Invalid observer position.");
        var system = Quote(systemId); var result = new List<string>();
        for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++)
            result.Add($"SELECT * FROM visible_ship_motion WHERE system_id = {system} AND cell_x = {(cell.X + dx).ToString(CultureInfo.InvariantCulture)} AND cell_y = {(cell.Y + dy).ToString(CultureInfo.InvariantCulture)}");
        return result.ToArray();
    }
    private static string Key(WorldScopeAdmission a) => $"{a.CharacterId}/{a.ShipId}/{a.SystemId}/{a.Revision}";
    public bool Accept(WorldScopeAdmission? next, WorldScopeMotion? acceptedMotion, WorldScopeEva? acceptedEva)
    {
        if (disposed) return false;
        if (next == null)
        {
            if (admission != null) Reset();
            return true;
        }
        if (!Id(next.CharacterId) || !Id(next.ShipId) || !Id(next.SystemId)) return false;
        if (admission != null && Key(admission) != Key(next))
        {
            if (next.CharacterId == admission.CharacterId && next.Revision <= admission.Revision) return false;
            Reset();
        }
        admission = next;
        if (acceptedMotion != null && acceptedMotion.ShipId == next.ShipId && acceptedMotion.SystemId == next.SystemId &&
            Cell(acceptedMotion.X, acceptedMotion.Y) is { } cell && cell.X == acceptedMotion.CellX && cell.Y == acceptedMotion.CellY &&
            (motion == null || acceptedMotion.Tick >= motion.Tick)) motion = acceptedMotion;
        if (acceptedEva == null) eva = null;
        else if (acceptedEva.CharacterId == next.CharacterId && acceptedEva.SystemId == next.SystemId && Cell(acceptedEva.X, acceptedEva.Y) != null &&
            (eva == null || acceptedEva.Tick >= eva.Tick && acceptedEva.Revision >= eva.Revision)) eva = acceptedEva;
        var observer = eva != null ? Cell(eva.X, eva.Y) : motion != null ? Cell(motion.X, motion.Y) : null;
        desired = observer is { } point ? $"{next.SystemId}/{point.X}/{point.Y}" : null;
        Pump(); return true;
    }
    private void Reset()
    {
        admission = null; motion = null; eva = null; desired = null; Epoch++;
        foreach (var scope in scopes.ToArray()) Retire(scope);
        changed();
    }
    private void Retire(Scope scope)
    {
        if (scope.Retiring) return;
        scope.Retiring = true;
        scope.Handle?.End(() => { scopes.Remove(scope); Pump(); });
    }
    private void Open(string kind, string key, string[] queries)
    {
        var scope = new Scope { Kind = kind, Key = key, Epoch = Epoch }; scopes.Add(scope);
        try
        {
            scope.Handle = subscribe(queries, () => {
                if (!scopes.Contains(scope) || disposed) return;
                if (scope.Retiring || scope.Epoch != Epoch) { Retire(scope); return; }
                scope.Applied = true;
                if (kind == "cells") foreach (var old in scopes.Where(s => s != scope && s.Kind == "cells" && s.Applied && !s.Retiring).ToArray()) Retire(old);
                changed(); Pump();
            }, () => { var retired = scope.Retiring; scopes.Remove(scope); if (!retired && !disposed) { Reset(); failed(); } else Pump(); });
            if (scope.Retiring) scope.Handle.End(() => { scopes.Remove(scope); Pump(); });
        }
        catch { scopes.Remove(scope); Reset(); failed(); }
    }
    private void Pump()
    {
        if (disposed || pumping || admission == null) return;
        pumping = true;
        try
        {
            var own = scopes.FirstOrDefault(s => s.Kind == "own");
            if (own == null)
            {
                Open("own", Key(admission), new[] {
                    $"SELECT * FROM visible_ship_motion WHERE ship_id = {Quote(admission.ShipId)} AND system_id = {Quote(admission.SystemId)}",
                    $"SELECT * FROM own_eva_body WHERE character_id = {Quote(admission.CharacterId)} AND system_id = {Quote(admission.SystemId)}" });
                own = scopes.FirstOrDefault(s => s.Kind == "own");
            }
            if (own is not { Applied: true, Retiring: false } || own.Epoch != Epoch || motion == null || desired == null || CellSets >= 2 ||
                scopes.Any(s => s.Kind == "cells" && (!s.Applied && !s.Retiring || s.Key == desired && !s.Retiring))) return;
            Open("cells", desired, CellQueries(admission.SystemId, eva?.X ?? motion.X, eva?.Y ?? motion.Y));
        }
        finally { pumping = false; }
    }
    public void Dispose() { if (disposed) return; disposed = true; Reset(); }
}
