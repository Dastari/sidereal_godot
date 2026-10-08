using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidereal.Bindings;
using SpacetimeDB.ClientApi;

namespace Sidereal.Native.Editor;

public enum ObserverStatus { Disconnected, Connecting, Connected, Subscribing, Live, Error }
public enum ObserverFailure { Connection, Subscription }
public sealed record ObserverSnapshot(ObserverStatus Status, long RowCount, string Message, bool Busy);

// This boundary has no reducer or game-session admission API.
public interface IObserverSocket
{
    long RowCount { get; }
    void Tick();
    void Close();
}

public interface IObserverSocketFactory
{
    IObserverSocket Open(ClientSettings settings, string? originalProviderToken, Action connected,
        Action subscribing, Action applied, Action<ObserverFailure> failed, Action disconnected);
}

public sealed class SdkObserverSocketFactory : IObserverSocketFactory
{
    public const string Query = "SELECT * FROM own_characters";
    private sealed class Socket : IObserverSocket
    {
        private DbConnection? connection;
        private SubscriptionHandle? subscription;
        private bool closed;
        public long RowCount => connection?.Db.OwnCharacters.Iter().LongCount() ?? 0;
        public Socket(ClientSettings settings, string? token, Action connected, Action subscribing,
            Action applied, Action<ObserverFailure> failed, Action disconnected)
        {
            connection = DbConnection.Builder().WithUri(settings.GameOrigin).WithDatabaseName(settings.Database)
                .WithToken(token)
                .OnConnect((conn, _, _) => {
                    if (closed) return;
                    connected();
                    subscribing();
                    subscription = conn.SubscriptionBuilder()
                        .OnApplied(_ => { if (!closed) applied(); })
                        .OnError((_, _) => { if (!closed) failed(ObserverFailure.Subscription); })
                        .Subscribe(Query);
                })
                .OnConnectError(_ => { if (!closed) failed(ObserverFailure.Connection); })
                .OnDisconnect((_, _) => { if (!closed) disconnected(); })
                .Build();
        }
        public void Tick() { if (!closed) connection?.FrameTick(); }
        public void Close()
        {
            if (closed) return;
            closed = true;
            // Socket close atomically releases every subscription; no reducer or actor cleanup intent.
            var old = connection; connection = null; subscription = null;
            old?.Disconnect();
        }
    }
    public IObserverSocket Open(ClientSettings settings, string? originalProviderToken, Action connected,
        Action subscribing, Action applied, Action<ObserverFailure> failed, Action disconnected) =>
        new Socket(settings, originalProviderToken, connected, subscribing, applied, failed, disconnected);
}

/// <summary>One editor-thread owner, independent of ClientCore and all gameplay lifecycle.</summary>
public sealed class WorldObserver : IDisposable
{
    private readonly IObserverSocketFactory sockets;
    private readonly Func<ClientSettings, Action<string>, CancellationToken, Task<Credential>> signIn;
    private readonly Func<double> now;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<(long Generation, Action Apply)> events = new();
    private readonly ConcurrentQueue<(long Generation, string Url)> browserUrls = new();
    private CancellationTokenSource? cancellation;
    private Task<Credential>? authentication;
    private IObserverSocket? socket;
    private ObserverProfile? profile;
    private long generation;
    private double deadline;
    private bool disposed;
    public ObserverSnapshot Snapshot { get; private set; } = new(ObserverStatus.Disconnected, 0, "Choose an environment, then Connect.", false);

    public WorldObserver(IObserverSocketFactory? sockets = null,
        Func<ClientSettings, Action<string>, CancellationToken, Task<Credential>>? signIn = null,
        Func<double>? now = null)
    {
        this.sockets = sockets ?? new SdkObserverSocketFactory();
        this.signIn = signIn ?? ((settings, openBrowser, cancel) => new NativeAuth(settings).SignIn(openBrowser, cancel));
        this.now = now ?? (() => Environment.TickCount64 / 1000d);
    }

    private void OnOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("The world observer requires its editor thread.");
    }
    public void Connect(ObserverProfile selected)
    {
        OnOwnerThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        Disconnect();
        profile = selected;
        Snapshot = new(ObserverStatus.Connecting, 0, selected.IsFixture
            ? "Connecting without creating a fixture character…" : "Complete Dastari sign-in in your browser…", true);
        var attempt = generation;
        if (selected.IsFixture) { Open(null, attempt); return; }
        cancellation = new CancellationTokenSource();
        deadline = now() + 190;
        try { authentication = signIn(selected.Settings, url => browserUrls.Enqueue((attempt, url)), cancellation.Token); }
        catch { Fail("Sign-in could not start. Check the callback port and retry."); }
    }
    private void Open(string? originalProviderToken, long attempt)
    {
        deadline = now() + 20;
        Snapshot = new(ObserverStatus.Connecting, 0, "Connecting to the selected database…", true);
        void Queue(Action change) => events.Enqueue((attempt, change));
        try
        {
            socket = sockets.Open(profile!.Settings, originalProviderToken,
                () => Queue(() => Snapshot = new(ObserverStatus.Connected, 0, "Transport connected.", true)),
                () => Queue(() => Snapshot = new(ObserverStatus.Subscribing, 0, "Applying actor-filtered subscription…", true)),
                () => Queue(() => Snapshot = new(ObserverStatus.Live, 0, "Read-only subscription applied.", true)),
                failure => Queue(() => Fail(failure == ObserverFailure.Subscription
                    ? "Subscription failed. Check that the native bindings match the server." : "Connection failed. Check the selected environment and Tailscale.")),
                () => Queue(() => Fail("Connection closed. Connect explicitly to retry.")));
        }
        catch { Fail("Connection could not start. Check the selected environment and retry."); }
    }
    public bool TryTakeBrowserUrl(out string url)
    {
        OnOwnerThread();
        while (browserUrls.TryDequeue(out var entry))
            if (!disposed && entry.Generation == generation && authentication != null)
            { url = entry.Url; return true; }
        url = ""; return false;
    }
    public void Tick()
    {
        OnOwnerThread();
        if (disposed) return;
        if (authentication is { IsCompleted: true } auth)
        {
            authentication = null;
            if (auth.IsCompletedSuccessfully) Open(auth.Result.IdToken, generation);
            else { _ = auth.Exception; Fail("Sign-in cancelled, expired or rejected. Connect explicitly to retry."); }
        }
        try { socket?.Tick(); }
        catch { Fail("Connection processing failed. Check the selected environment and retry."); }
        while (events.TryDequeue(out var entry))
            if (entry.Generation == generation) entry.Apply();
        if (Snapshot.Busy && Snapshot.Status != ObserverStatus.Live && now() > deadline)
            Fail(authentication != null ? "Sign-in timed out. Connect explicitly to retry." : "Connection or subscription timed out. Connect explicitly to retry.");
        if (Snapshot.Status == ObserverStatus.Live)
        {
            try { Snapshot = Snapshot with { RowCount = socket?.RowCount ?? 0 }; }
            catch { Fail("Subscribed rows could not be read. Check native bindings and reconnect."); }
        }
    }
    private void Stop()
    {
        generation++;
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        var oldAuth = authentication; authentication = null;
        if (oldAuth != null) _ = oldAuth.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var oldSocket = socket; socket = null;
        try { oldSocket?.Close(); } catch { /* Teardown cannot resurrect a session. */ }
        while (events.TryDequeue(out _)) { }
        while (browserUrls.TryDequeue(out _)) { }
        profile = null;
    }
    private void Fail(string message)
    {
        Stop();
        Snapshot = new(ObserverStatus.Error, 0, message, false);
    }
    public void Disconnect()
    {
        OnOwnerThread();
        Stop();
        Snapshot = new(ObserverStatus.Disconnected, 0, "Disconnected. Cached rows cleared.", false);
    }
    public void Dispose()
    {
        OnOwnerThread();
        if (disposed) return;
        Disconnect(); disposed = true;
    }
}
