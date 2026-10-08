using System;
using System.IO;
using Sidereal.Native.Input;

internal sealed class FailingSharedJoinJournal : ISharedJoinJournal
{
    internal string? Saved;
    internal int RemoveCalls;
    internal bool DenyRemoval = true;
    internal bool Unauthorized;
    public string? Read(string key) => Saved;
    public void Write(string key, string value) => Saved = value;
    public void Remove(string key)
    {
        RemoveCalls++;
        if (DenyRemoval)
        {
            if (Unauthorized) throw new UnauthorizedAccessException("Fixture access denied");
            throw new IOException("Fixture storage unavailable");
        }
        Saved = null;
    }
}
