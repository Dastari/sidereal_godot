using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sidereal.Native;

// Provider credentials stay in memory and are never replaced by an SDK ticket.
public sealed record Credential(string IdToken, string RefreshToken, string Nonce, string Subject, DateTimeOffset ExpiresAt);

public sealed class NativeAuth
{
    private readonly ClientSettings settings;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
    public NativeAuth(ClientSettings settings) => this.settings = settings;
    public static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight((value.Length + 3) / 4 * 4, '='));
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string ValidateCallback(string target, string state)
    {
        var uri = new Uri("http://127.0.0.1" + target);
        if (uri.AbsolutePath != "/callback") throw new InvalidOperationException("Unexpected sign-in callback.");
        var args = new Dictionary<string, string>();
        foreach (var field in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = field.Split('=', 2);
            if (pair.Length != 2 || !args.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1])))
                throw new InvalidOperationException("Invalid sign-in callback.");
        }
        if (!args.TryGetValue("state", out var actual) || actual.Length != state.Length ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(state)))
            throw new InvalidOperationException("Sign-in state did not match.");
        if (args.ContainsKey("error")) throw new InvalidOperationException("Sign-in was cancelled by the provider.");
        if (!args.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("No sign-in code was returned.");
        return code;
    }

    private async Task<JsonElement> Discovery(CancellationToken cancellation)
    {
        var issuer = settings.Issuer.TrimEnd('/');
        using var response = await http.GetAsync(issuer + "/.well-known/openid-configuration", cancellation);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var discovery = doc.RootElement.Clone();
        if (discovery.GetProperty("issuer").GetString() != issuer) throw new InvalidOperationException("Provider issuer did not match.");
        foreach (var name in new[] { "authorization_endpoint", "token_endpoint", "jwks_uri" })
        {
            var endpoint = new Uri(discovery.GetProperty(name).GetString()!);
            if (endpoint.Scheme != "https" || endpoint.Host != new Uri(issuer).Host)
                throw new InvalidOperationException("Invalid provider endpoint.");
        }
        return discovery;
    }

    public async Task<Credential> SignIn(Action<string> openBrowser, CancellationToken cancellation)
    {
        var discovery = await Discovery(cancellation);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = $"http://127.0.0.1:{settings.CallbackPort}/callback";
        var fields = new Dictionary<string, string> {
            ["client_id"] = settings.ClientId, ["redirect_uri"] = redirect,
            ["response_type"] = "code", ["scope"] = "openid profile email",
            ["code_challenge_method"] = "S256", ["code_challenge"] = Challenge(verifier),
            ["state"] = state, ["nonce"] = nonce
        };
        // TcpListener avoids the administrator URL ACL required by HttpListener on Windows.
        var listener = new TcpListener(IPAddress.Loopback, settings.CallbackPort);
        listener.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var query = string.Join('&', fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            openBrowser(discovery.GetProperty("authorization_endpoint").GetString() + "?" + query);
            while (true)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                string? acceptedCode = null;
                try
                {
                    using var stream = socket.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var request = new StringBuilder();
                    // Bound each request before parsing; an unrelated tab cannot hold login open.
                    while (request.Length < 8192)
                    {
                        var single = new char[1];
                        if (await reader.ReadAsync(single.AsMemory(), requestTimeout.Token) == 0) break;
                        request.Append(single[0]);
                        if (request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                    }
                    if (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) continue;
                    var line = request.ToString().Split("\r\n")[0].Split(' ');
                    string? code = null;
                    try
                    {
                        if (line.Length != 3 || line[0] != "GET") throw new InvalidOperationException();
                        code = ValidateCallback(line[1], state);
                    }
                    catch (Exception) { /* Unsolicited callbacks cannot consume the real flow. */ }
                    acceptedCode = code;
                    var message = code == null ? "Sign-in callback rejected. Return to the original sign-in tab." : "Sign-in received. You can return to Sidereal.";
                    var payload = Encoding.UTF8.GetBytes("<!doctype html><title>Sidereal sign-in</title><p>" + message + "</p>");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(code == null ? "400 Bad Request" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, timeout.Token);
                    await stream.WriteAsync(payload, timeout.Token);
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                catch (IOException) { }
                catch (SocketException) { }
                // Provider request failures end this flow. A consumed code cannot be reused.
                if (acceptedCode != null)
                    return await Exchange(discovery, new Dictionary<string, string> {
                        ["client_id"] = settings.ClientId, ["grant_type"] = "authorization_code",
                        ["code"] = acceptedCode, ["redirect_uri"] = redirect, ["code_verifier"] = verifier
                    }, nonce, timeout.Token);
            }
        }
        finally { listener.Stop(); }
    }

    public async Task<Credential> Refresh(Credential previous, CancellationToken cancellation)
    {
        var discovery = await Discovery(cancellation);
        return await Exchange(discovery, new Dictionary<string, string> {
            ["client_id"] = settings.ClientId, ["grant_type"] = "refresh_token",
            ["refresh_token"] = previous.RefreshToken
        }, previous.Nonce, cancellation, previous.Subject);
    }

    private async Task<Credential> Exchange(JsonElement discovery, Dictionary<string, string> fields, string nonce, CancellationToken cancellation, string? expectedSubject = null)
    {
        using var response = await http.PostAsync(discovery.GetProperty("token_endpoint").GetString(), new FormUrlEncodedContent(fields), cancellation);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Sign-in expired or was rejected. Sign in again.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var token = doc.RootElement.GetProperty("id_token").GetString()!;
        var parts = token.Split('.');
        if (parts.Length != 3) throw new InvalidOperationException("Invalid provider token.");
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        var payload = claims.RootElement;
        if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw new InvalidOperationException("Unsupported provider signature.");
        using var keysResponse = await http.GetAsync(discovery.GetProperty("jwks_uri").GetString(), cancellation);
        keysResponse.EnsureSuccessStatusCode();
        using var keys = JsonDocument.Parse(await keysResponse.Content.ReadAsStringAsync(cancellation));
        var key = keys.RootElement.GetProperty("keys").EnumerateArray().Single(k => k.GetProperty("kid").GetString() == header.RootElement.GetProperty("kid").GetString());
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Decode(key.GetProperty("n").GetString()!), Exponent = Decode(key.GetProperty("e").GetString()!) });
        if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidOperationException("Provider signature did not verify.");
        var aud = payload.GetProperty("aud");
        var audienceMatches = aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Any(v => v.GetString() == settings.ClientId) : aud.GetString() == settings.ClientId;
        var expiry = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("exp").GetInt64());
        var subject = payload.GetProperty("sub").GetString();
        // A refreshed ID token may omit nonce; when present it must remain unchanged.
        var nonceMatches = payload.TryGetProperty("nonce", out var actualNonce)
            ? actualNonce.GetString() == nonce : expectedSubject != null;
        if (payload.GetProperty("iss").GetString() != settings.Issuer.TrimEnd('/') || !audienceMatches ||
            !nonceMatches || string.IsNullOrEmpty(subject) || (expectedSubject != null && subject != expectedSubject) ||
            (payload.TryGetProperty("azp", out var party) && party.GetString() != settings.ClientId) ||
            expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Provider identity claims did not match.");
        return new Credential(token, doc.RootElement.GetProperty("refresh_token").GetString()!, nonce, subject, expiry);
    }
}
