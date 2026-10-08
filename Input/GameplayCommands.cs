using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidereal.Native.Input;

/// <summary>Receipts for legacy reducers that do not carry operation IDs. A timed-out
/// socket is retired before reuse, because an equal-argument late receipt is ambiguous.</summary>
public sealed class GameplayCallbackLedger<TSession> where TSession : class
{
    private sealed record Attempt(TSession Session, string Operation, string Kind, string[] Arguments, double Deadline);
    private static readonly IEqualityComparer<TSession> ByReference = ReferenceEqualityComparer.Instance;
    private readonly List<Attempt> attempts = new();
    private readonly HashSet<TSession> retired = new(ByReference);
    public int PendingCount => attempts.Count;
    public bool IsRetired(TSession session) => retired.Contains(session);
    public bool TryAdd(TSession session, string operation, string kind, IReadOnlyList<string> arguments, double deadline)
    {
        if (retired.Contains(session) || attempts.Count >= 32 || !double.IsFinite(deadline) ||
            attempts.Any(a => a.Operation == operation || ReferenceEquals(a.Session, session) && a.Kind == kind && a.Arguments.SequenceEqual(arguments, StringComparer.Ordinal))) return false;
        attempts.Add(new(session, operation, kind, arguments.ToArray(), deadline)); return true;
    }
    public string? Complete(TSession session, string kind, IReadOnlyList<string> arguments)
    {
        if (retired.Contains(session)) return null;
        var attempt = attempts.FirstOrDefault(a => ReferenceEquals(a.Session, session) && a.Kind == kind && a.Arguments.SequenceEqual(arguments, StringComparer.Ordinal));
        if (attempt == null) return null;
        attempts.Remove(attempt); return attempt.Operation;
    }
    public IReadOnlyList<TSession> Expire(double now)
    {
        var expired = attempts.Where(a => now > a.Deadline).Select(a => a.Session).Distinct(ByReference).ToArray();
        foreach (var session in expired) retired.Add(session);
        attempts.RemoveAll(a => retired.Contains(a.Session));
        return expired;
    }
    public void Forget(string operation) => attempts.RemoveAll(a => a.Operation == operation);
    public void Clear() { attempts.Clear(); retired.Clear(); }
}
