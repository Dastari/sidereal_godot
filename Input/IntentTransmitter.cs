using System;
using System.Collections.Generic;

namespace Sidereal.Native.Input;

/// <summary>Immediate changed demands, bounded outstanding requests and independent zero-input braking heartbeats.</summary>
public sealed class IntentTransmitter
{
    private readonly Action<ulong, MovementIntent> send;
    private readonly Action<string> error;
    private readonly Action stalled;
    private readonly Func<double> now;
    private readonly Dictionary<ulong, MovementIntent> pending = new();
    private MovementIntent? previous;
    private ulong sequence, lastSequence;
    private double previousAt, failedUntil;
    private bool disposed;
    public int PendingCount => pending.Count;
    public IntentTransmitter(Action<ulong, MovementIntent> send, Action<string> error, Action stalled, Func<double> now)
    { this.send = send; this.error = error; this.stalled = stalled; this.now = now; }
    public bool Offer(MovementIntent intent, bool piloting = false, bool force = false)
    {
        if (disposed || !intent.Finite || (!force && now() < failedUntil)) return false;
        var interval = piloting || intent.Moving ? .1 : 1;
        if (!force && previous == intent && now() - previousAt < interval) return false;
        if (pending.Count >= 32) { disposed = true; stalled(); return false; }
        var serial = ++sequence; lastSequence = serial;
        pending[serial] = intent; previous = intent; previousAt = now();
        try { send(serial, intent); }
        catch { pending.Remove(serial); disposed = true; stalled(); return false; }
        return true;
    }
    public void Result(ulong serial, bool accepted, string? reason = null)
    {
        if (disposed || !pending.Remove(serial) || accepted || serial != lastSequence) return;
        previous = null; failedUntil = now() + 1;
        error(reason ?? "The server rejected movement input.");
    }
    public void ResetDemand() { previous = null; }
    public void Dispose() { disposed = true; pending.Clear(); previous = null; }
}
