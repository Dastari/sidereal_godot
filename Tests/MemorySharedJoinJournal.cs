using System.Collections.Generic;
using Sidereal.Native.Input;

internal sealed class MemorySharedJoinJournal : ISharedJoinJournal
{
    internal readonly Dictionary<string, string> Values = new();
    public string? Read(string key) => Values.GetValueOrDefault(key);
    public void Write(string key, string value) => Values[key] = value;
    public void Remove(string key) => Values.Remove(key);
}
