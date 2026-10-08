using System;

namespace Sidereal.Native.Input;

/// <summary>One socket's acknowledged input lease. Reducer events, not local intent, grant Held.</summary>
public sealed class ControlLease
{
    private readonly Action claim, release, stalled;
    private readonly Action<string> error;
    private readonly Func<double> now;
    private bool claimPending, releasePending, disposed;
    private double deadline, retryAt;
    public bool Wanted { get; private set; }
    public bool Held { get; private set; }
    public bool CanSend => !disposed && Wanted && Held && !releasePending;

    public ControlLease(Action claim, Action release, Action stalled, Action<string> error, Func<double> now)
    { this.claim = claim; this.release = release; this.stalled = stalled; this.error = error; this.now = now; }

    public void Activate(bool wanted)
    {
        if (disposed) return;
        Wanted = wanted;
        if (!wanted && (Held || claimPending) && !releasePending)
        {
            Held = false; releasePending = true; deadline = now() + .5;
            try { release(); } catch { FailSocket(); }
        }
        Reconcile();
    }
    public void Tick()
    {
        if (disposed) return;
        if ((claimPending || releasePending) && now() > deadline)
        {
            // A claim without operation arguments cannot disambiguate a late ACK after another
            // claim on this socket. Disconnecting clears it rather than risking resurrection.
            error(claimPending ? "Movement control confirmation timed out. Reconnecting." : "Movement control release timed out. Reconnecting.");
            FailSocket(); return;
        }
        Reconcile();
    }
    // Input/focus and reducer callbacks may run before the owner's SDK pump.
    // Demand transitions send immediately; only Tick after FrameTick expires
    // unacknowledged operations, preserving the existing retirement deadlines.
    private void Reconcile()
    {
        if (disposed || !Wanted || Held || claimPending || releasePending || now() < retryAt) return;
        claimPending = true; deadline = now() + 1.5;
        try { claim(); } catch { FailSocket(); }
    }
    public void ClaimResult(bool accepted, string? reason = null)
    {
        if (disposed || !claimPending) return;
        claimPending = false;
        if (!accepted)
        {
            Held = false; Wanted = false; retryAt = now() + 1;
            error(reason ?? "Movement control was refused.");
        }
        else if (Wanted && !releasePending) Held = true;
        else if (!releasePending)
        {
            releasePending = true; deadline = now() + .5;
            try { release(); } catch { FailSocket(); }
        }
    }
    public void ReleaseResult()
    {
        if (disposed || !releasePending) return;
        releasePending = false; Held = false;
        Reconcile();
    }
    private void FailSocket() { Dispose(); stalled(); }
    public void Dispose() { Wanted = Held = claimPending = releasePending = false; disposed = true; }
}
