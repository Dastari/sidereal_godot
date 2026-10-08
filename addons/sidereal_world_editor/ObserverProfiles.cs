using System;
using System.Collections.Generic;
using System.IO;

namespace Sidereal.Native.Editor;

public sealed record ObserverProfile(string Name, ClientSettings Settings)
{
    public bool IsFixture => Settings.IsIsolatedFixture;
    public bool IsProduction => !IsFixture;
    public string Authentication => IsFixture ? "Isolated anonymous observer" : "Dastari browser sign-in";
}

public sealed record ObserverProfiles(IReadOnlyList<ObserverProfile> Items, string Notice)
{
    // Avoid reflection-generated serializers retaining the project's collectible assembly context.
    // Validation remains the same single policy used by the runtime client.
    private static ClientSettings ReadValidated(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new System.Text.Json.JsonException("Expected project profile object.");
        var fields = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject()) fields[property.Name] = property.Value;
        string Text(string key) => fields.TryGetValue(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()! : throw new System.Text.Json.JsonException("Required project profile field is invalid.");
        if (!fields.TryGetValue("callbackPort", out var callback) || callback.ValueKind != System.Text.Json.JsonValueKind.Number ||
            !callback.TryGetInt32(out var port)) throw new System.Text.Json.JsonException("Project callback field is invalid.");
        return new ClientSettings(Text("gameOrigin"), Text("database"), Text("issuer"), Text("clientId"), port).Validate();
    }

    // Reuse public configuration. No token, identity, URL or database is authored in the addon.
    public static ObserverProfiles Load(string projectDirectory)
    {
        var profiles = new List<ObserverProfile>();
        var notice = "";
        ClientSettings? normal = null;
        try
        {
            normal = ReadValidated(File.ReadAllText(Path.Combine(projectDirectory, "client-settings.json")));
            profiles.Add(new(normal.IsIsolatedFixture ? "Isolated test fixture" : "Live world", normal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or System.Text.Json.JsonException or UriFormatException)
        { notice = "Public client settings are unavailable or invalid."; }

        var fixturePath = Path.Combine(projectDirectory, "client-settings.network-test.json");
        if (File.Exists(fixturePath))
        {
            try
            {
                var fixture = ReadValidated(File.ReadAllText(fixturePath));
                if (!fixture.IsIsolatedFixture || normal == null || fixture.Issuer != normal.Issuer ||
                    fixture.ClientId != normal.ClientId || fixture.CallbackPort != normal.CallbackPort)
                    throw new InvalidOperationException();
                if (fixture != normal) profiles.Insert(0, new("Isolated test fixture", fixture));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or System.Text.Json.JsonException or UriFormatException)
            { notice = "Ignored fixture settings were rejected. Select valid existing project settings."; }
        }
        return new(profiles, notice);
    }
}
