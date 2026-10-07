using System;
using Sidereal.Native.Input;

internal sealed class ControlledWorldScope : IWorldScopeHandle
{
    internal readonly string[] Queries;
    private readonly Action applied;
    private Action? ended;
    internal bool Retiring => ended != null;
    internal ControlledWorldScope(string[] queries, Action applied) { Queries = queries; this.applied = applied; }
    internal void Apply() => applied();
    public void End(Action callback) => ended = callback;
    internal void Retired() { var callback = ended; ended = null; callback?.Invoke(); }
}
