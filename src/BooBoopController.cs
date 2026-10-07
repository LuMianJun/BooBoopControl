using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BooBoopBridge;

/// <summary>
/// Explicit, one-session FN010-RX controller. Levels are protocol indices: 0 stops,
/// 1 through 9 are passed through unchanged. Successful writes do not prove motion
/// or physical stopping. Callers must observe returned tasks and retain a physical stop option.
/// Diagnostic handlers run on worker threads and must not call Unity APIs directly.
/// </summary>
public enum BooBoopState { Created, Initializing, Ready, Disconnected, Faulted, Disposing, Disposed }

public sealed class BooBoopController : IBooBoopController
{
    public static int ApiMajorVersion => 1;
    private const string SupportedModel = "FN010-RX";
    private sealed record Device(string Name, string Address, string Code,
        string Serial, string ProductName, string ProductId);

    private readonly NativeHost _host;
    private readonly string _productCachePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Device> _found = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _preferredAddress;
    private Task? _initializeTask, _disposeTask;
    private Device? _target;
    private string _connectTrace = "";
    private bool _collecting, _acceptConnection, _everConnected, _ready, _sessionLost, _disposing;
    private readonly MotionEpochs _motionEpochs = new();
    private BooBoopState _state = BooBoopState.Created;
    private readonly object _notificationGate = new();
    private readonly Queue<Action> _notifications = new();
    private bool _notificationWorker;

    public BooBoopController(string preferredDeviceAddress = "")
        : this(preferredDeviceAddress, new BooBoopOptions()) { }

    public BooBoopController(string preferredDeviceAddress, BooBoopOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _host = new NativeHost(options.GetHostExecutablePath());
        _productCachePath = options.GetProductCachePath();
        _preferredAddress = (preferredDeviceAddress ?? "").Trim();
        _host.MessageReceived += Receive;
        _host.Diagnostic += HostDiagnostic;
    }

    public event Action<string>? Diagnostic;
    public event Action<string>? Error;
    public event Action<BooBoopState>? StateChanged;
    public BooBoopState State { get { lock (_gate) return _state; } }

    public bool IsReady
    {
        get { bool ready; lock (_gate) ready = !_disposing && _ready && !_sessionLost; return ready && _host.IsRunning; }
    }

    /// <summary>Starts initialization on a worker thread. Repeated calls return the same attempt; failures are never retried.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposing();
            if (_initializeTask is null)
            {
                ChangeState(BooBoopState.Initializing);
                _initializeTask = Task.Run(() => InitializeCoreAsync(cancellationToken));
            }
            return _initializeTask;
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(25));
        await _operations.WaitAsync(limit.Token).ConfigureAwait(false);
        try
        {
            limit.Token.ThrowIfCancellationRequested();
            // Serialized against disposal: cancellation never allows a later orphaned startup.
            _host.Start();
            lock (_gate) _collecting = true;
            try
            {
                await _host.SendAsync(new { command = "search", name = "", uuid = "", port = "" },
                    TimeSpan.FromSeconds(3), limit.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), limit.Token).ConfigureAwait(false);
            }
            finally { lock (_gate) _collecting = false; }
            Report("Discovery observation ended; this does not confirm native scan cancellation.");
            Device target;
            lock (_gate)
            {
                Device[] candidates = _found.Values.Where(d => _preferredAddress.Length == 0 ||
                    SameAddress(d.Address, _preferredAddress)).ToArray();
                if (candidates.Length == 0)
                    throw new InvalidOperationException("No matching verified FN010-RX was discovered.");
                if (candidates.Length != 1)
                    throw new InvalidOperationException("More than one eligible device was discovered; specify its exact address.");
                target = candidates[0];
            }
            if (string.IsNullOrWhiteSpace(target.Name) || string.IsNullOrWhiteSpace(target.Code))
                throw new InvalidDataException("The discovery record lacks the verified name/service_data connection fields.");
            target = await ResolveModelMetadataAsync(target, limit.Token).ConfigureAwait(false);
            limit.Token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposing();
                _target = target; // Immutable and never replaced, including after a disconnect.
                _connectTrace = "bridge-" + Guid.NewGuid().ToString("N");
                _acceptConnection = true;
            }
            var connect = new Dictionary<string, object>
            {
                ["command"] = "connect", ["trace_id"] = _connectTrace,
                ["name"] = target.Name, ["product_name"] = target.ProductName,
                ["device_address"] = target.Address, ["uuid"] = target.Serial,
                ["flag"] = "2", ["port"] = ""
            };
            if (target.Code.Length > 0) connect["service_data"] = target.Code;
            if (target.ProductId.Length > 0) connect["product_id"] = target.ProductId;
            using var connectionLimit = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            connectionLimit.CancelAfter(TimeSpan.FromSeconds(12));
            await _host.SendAsync(connect, TimeSpan.FromSeconds(3), connectionLimit.Token).ConfigureAwait(false);
            bool connected = await _connection.Task.WaitAsync(connectionLimit.Token).ConfigureAwait(false);
            if (!connected) throw new InvalidOperationException("The selected device did not connect successfully.");
            lock (_gate)
            {
                ThrowIfDisposing();
                limit.Token.ThrowIfCancellationRequested();
                if (_sessionLost || !_host.IsRunning)
                    throw new InvalidOperationException("The selected session became unavailable during initialization.");
                _ready = true;
                ChangeState(BooBoopState.Ready);
            }
            Report("The selected address reported connection success. No motion was requested.");
        }
        catch
        {
            lock (_gate) { _ready = false; _sessionLost = true; _motionEpochs.StopAll(); }
            ChangeState(BooBoopState.Faulted);
            PublishError("Initialization failed or was canceled. No retry or automatic reconnection will occur.");
            Report("Initialization failed or was canceled. Dispose this controller to clean up its owned host.");
            throw;
        }
        finally
        {
            lock (_gate) { _collecting = false; _acceptConnection = false; }
            _operations.Release();
        }
    }

    public Task SetStretchAsync(int level, CancellationToken cancellationToken = default) =>
        SetModeAsync("aa01", level, cancellationToken);

    public Task SetVibrationAsync(int level, CancellationToken cancellationToken = default) =>
        SetModeAsync("bb01", level, cancellationToken);

    private Task SetModeAsync(string prefix, int level, CancellationToken cancellationToken)
    {
        if (level < 0 || level > 9) throw new ArgumentOutOfRangeException(nameof(level), "Use 0 through 9; 0 stops that mode.");
        MotionTicket ticket;
        lock (_gate)
        {
            ThrowIfDisposing();
            if (!_ready || _sessionLost || _target is null)
                throw new InvalidOperationException("Await successful InitializeAsync before sending any control command.");
            ticket = _motionEpochs.Capture(prefix, level == 0); // A channel stop never cancels the other channel.
        }
        return SetModeCoreAsync(prefix + level.ToString("x2", CultureInfo.InvariantCulture), ticket, level == 0, cancellationToken);
    }

    private async Task SetModeCoreAsync(string command, MotionTicket ticket, bool isStop, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(6));
        await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Device target;
            lock (_gate)
            {
                ThrowIfDisposing();
                linked.Token.ThrowIfCancellationRequested();
                if (!isStop && !_motionEpochs.IsCurrent(ticket))
                    throw new OperationCanceledException("This queued operation was invalidated by a stop or session loss.");
                if (!_ready || _sessionLost || _target is null || !_host.IsRunning)
                    throw new InvalidOperationException("The selected session is not ready; automatic reconnect is disabled.");
                target = _target;
            }
            await _host.SendAsync(ModeCommand(target, command), TimeSpan.FromSeconds(3), linked.Token).ConfigureAwait(false);
            Report("The requested mode command was written to the host; physical execution is unconfirmed.");
        }
        finally { _operations.Release(); }
    }

    /// <summary>Invalidates previously queued mode commands immediately, then independently attempts both mode stops.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { ThrowIfDisposing(); _motionEpochs.StopAll(); }
        return StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(10));
        await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await StopModesAsync(linked.Token, reportFailure: true).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task StopModesAsync(CancellationToken cancellationToken, bool reportFailure)
    {
        Device? target;
        lock (_gate) target = _everConnected ? _target : null;
        if (target is null) return;
        var failures = new List<Exception>();
        foreach (string command in new[] { "aa0100", "bb0100" })
        {
            // Independent deadline and try block: the first failure never skips the second stop.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await _host.SendAsync(ModeCommand(target, command), TimeSpan.FromSeconds(2), limit.Token).ConfigureAwait(false);
                Report("A mode stop request was written; physical stopping is unconfirmed.");
            }
            catch
            {
                PublishError("A mode stop write could not be confirmed. Check the physical device and disconnect its power if needed.");
                failures.Add(new IOException("A mode stop write could not be confirmed."));
            }
        }
        if (reportFailure && failures.Count > 0) throw new AggregateException("One or both mode stops could not be confirmed.", failures);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposing = true;
            ChangeState(BooBoopState.Disposing);
            _ready = false;
            _motionEpochs.StopAll();
            _lifetime.Cancel();
            _disposeTask = Task.Run(DisposeCoreAsync);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Initialization and active writes use bounded tokens. Keep their serialization lock
        // until they unwind, so a delayed startup can never occur after host disposal.
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            using var stopLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopModesAsync(stopLimit.Token, reportFailure: false).ConfigureAwait(false);
            await _host.DisposeAsync().ConfigureAwait(false); // Only the process this instance owns.
            ChangeState(BooBoopState.Disposed);
        }
        finally
        {
            _host.MessageReceived -= Receive;
            _host.Diagnostic -= HostDiagnostic;
            _operations.Release();
            // Waiting callers may still unwind; do not dispose their CTS or semaphore underneath them.
        }
    }

    private static object ModeCommand(Device d, string command)
    {
        var message = new Dictionary<string, object>
        {
            ["command"] = command, ["name"] = d.Name,
            ["mac_address"] = d.Address, ["device_address"] = d.Address, ["uuid"] = d.Serial
        };
        if (d.Code.Length > 0) message["service_data"] = d.Code;
        return message;
    }

    private void Receive(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        string command = Text(root, "command");
        lock (_gate)
        {
            if (command == "searching" && _collecting && !_disposing)
            {
                foreach (JsonElement row in Rows(root))
                {
                    Device d = Parse(row);
                    if (!d.Name.Trim().Equals(SupportedModel, StringComparison.OrdinalIgnoreCase) || d.Address.Length == 0) continue;
                    string key = NormalizeAddress(d.Address);
                    if (key.Length > 0 && (_found.Count < 128 || _found.ContainsKey(key))) _found[key] = d;
                }
                return;
            }
            if (_target is not { } target) return;
            JsonElement[] matching = Rows(root).Where(row => SameAddress(Address(row), target.Address)).ToArray();
            bool matches = matching.Length > 0;
            bool traceMatches = _connectTrace.Length > 0 && Text(root, "trace_id") == _connectTrace;
            if (command is "device_failed" or "device_not_found")
            {
                if (matches || traceMatches) { LoseSession(); _connection.TrySetResult(false); }
                return;
            }
            if (command is "connect" or "device_connected" or "linkBlueOn")
            {
                if (!_acceptConnection || _sessionLost || _disposing) return;
                foreach (JsonElement row in matching)
                {
                    string status = First(row, "code");
                    if (status.Length == 0) status = Text(root, "code");
                    int? code = int.TryParse(status, out int number) ? number : null;
                    bool success = code == 200 || (status.Length == 0 && command != "connect");
                    if (success) { _everConnected = true; _connection.TrySetResult(true); return; }
                    if (status.Length > 0) { LoseSession(); _connection.TrySetResult(false); return; }
                }
                if (traceMatches && Text(root, "code").Length > 0 && Text(root, "code") != "200")
                { LoseSession(); _connection.TrySetResult(false); }
            }
            else if ((command is "disconnect" or "device_disconnected" or "linkBlueOff") && matches)
            { LoseSession(); _connection.TrySetResult(false); }
            else if (command is "state_snapshot" or "query")
            {
                if (_everConnected && root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array &&
                    !data.EnumerateArray().Any(row => SameAddress(Address(row), target.Address)))
                { LoseSession(); _connection.TrySetResult(false); }
            }
        }
    }

    private void LoseSession(BooBoopState state = BooBoopState.Disconnected)
    { _sessionLost = true; _ready = false; _motionEpochs.StopAll(); ChangeState(state); }

    private void HostDiagnostic(string ignored)
    {
        lock (_gate) { LoseSession(BooBoopState.Faulted); _connection.TrySetResult(false); }
        PublishError("The native transport reported a problem; this session is disabled. No reconnect or motion replay will occur.");
    }

    private void Report(string message)
    {
        Delegate[] listeners = Diagnostic?.GetInvocationList() ?? Array.Empty<Delegate>();
        if (listeners.Length > 0) QueueNotification(() =>
        { foreach (Delegate callback in listeners) try { ((Action<string>)callback)(message); } catch { } });
    }

    private void PublishError(string message)
    {
        Delegate[] listeners = Error?.GetInvocationList() ?? Array.Empty<Delegate>();
        if (listeners.Length > 0) QueueNotification(() =>
        { foreach (Delegate callback in listeners) try { ((Action<string>)callback)(message); } catch { } });
    }

    private void ChangeState(BooBoopState state)
    {
        lock (_gate)
        {
            if (_disposing && state is not (BooBoopState.Disposing or BooBoopState.Disposed)) return;
            if (_state == state) return;
            _state = state;
        }
        Delegate[] listeners = StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>();
        if (listeners.Length > 0) QueueNotification(() =>
        { foreach (Delegate callback in listeners) try { ((Action<BooBoopState>)callback)(state); } catch { } });
    }

    // Public callbacks are queued off the transport/calling stack; the publisher never waits on them.
    // A slow/broken callback cannot grow an unbounded notification backlog or block control I/O.
    private void QueueNotification(Action notification)
    {
        lock (_notificationGate)
        {
            if (_notifications.Count == 64) _notifications.Dequeue();
            _notifications.Enqueue(notification);
            if (_notificationWorker) return;
            _notificationWorker = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                Action next;
                lock (_notificationGate)
                {
                    if (_notifications.Count == 0) { _notificationWorker = false; return; }
                    next = _notifications.Dequeue();
                }
                try { next(); } catch { }
            }
        });
    }

    private void ThrowIfDisposing()
    {
        if (_disposing) throw new ObjectDisposedException(nameof(BooBoopController));
    }

    private async Task<Device> ResolveModelMetadataAsync(Device target, CancellationToken cancellationToken)
    {
        const int maximumBytes = 1024 * 1024;
        if (!target.Name.Trim().Equals(SupportedModel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only FN010-RX is supported by the verified command map.");
        string path = _productCachePath;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Product cache is empty or exceeds the 1 MiB limit.");
        byte[] bytes = new byte[maximumBytes + 1];
        int length = 0;
        while (length < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(length), timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length == 0 || length > maximumBytes) throw new InvalidDataException("Product cache size changed outside the accepted limit.");
        int bom = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(bom, length - bom), new JsonDocumentOptions { MaxDepth = 16 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Text(root, "code") != "200" ||
            !root.TryGetProperty("success", out JsonElement success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("data", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 10000)
            throw new InvalidDataException("Product cache does not have the verified successful-response structure.");
        var matches = new Dictionary<string, (string Product, Guid Uuid)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (!Text(row, "blueName").Trim().Equals(target.Name.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            string product = Text(row, "productName").Trim();
            if (target.ProductName.Length > 0 && !product.Equals(target.ProductName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (product.Length == 0 || !Guid.TryParse(Text(row, "serialNumber").Trim(), out Guid uuid) || uuid == Guid.Empty)
                throw new InvalidDataException("The matching serialNumber UUID is missing, invalid or nil. No fallback is allowed.");
            matches[product + "|" + uuid.ToString("D")] = (product, uuid);
        }
        if (matches.Count != 1) throw new InvalidDataException("Product cache must contain exactly one distinct matching configuration.");
        var match = matches.Values.Single();
        if (!match.Product.Equals(SupportedModel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The matched product is outside the verified model scope.");
        if (!string.IsNullOrWhiteSpace(target.Serial) &&
            (!Guid.TryParse(target.Serial, out Guid supplied) || (supplied != Guid.Empty && supplied != match.Uuid)))
            throw new InvalidDataException("The discovered non-nil UUID conflicts with the product cache.");
        return target with { Serial = match.Uuid.ToString("D"), ProductName = match.Product };
    }

    private static IEnumerable<JsonElement> Rows(JsonElement root)
    {
        if (root.TryGetProperty("data", out JsonElement data))
        {
            if (data.ValueKind == JsonValueKind.Object) yield return data;
            else if (data.ValueKind == JsonValueKind.Array)
                foreach (JsonElement row in data.EnumerateArray().Take(128))
                    if (row.ValueKind == JsonValueKind.Object) yield return row;
        }
        yield return root;
    }

    private static Device Parse(JsonElement row) => new(First(row, "name", "bluename", "blueName"), Address(row),
        First(row, "service_data", "activationCode", "activationcode"), First(row, "uuid", "serialNumber"),
        First(row, "product_name", "productName"), First(row, "product_id", "productId"));
    private static string Text(JsonElement row, string key) => row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty(key, out JsonElement value) && (value.ValueKind is JsonValueKind.String or JsonValueKind.Number) &&
        value.ToString().Length <= 256 ? value.ToString() : "";
    private static string First(JsonElement row, params string[] keys) => keys.Select(key => Text(row, key)).FirstOrDefault(s => s.Length > 0) ?? "";
    private static string Address(JsonElement row) => First(row, "device_address", "mac_address", "macAddress");
    private static string NormalizeAddress(string value) => value.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();
    private static bool SameAddress(string a, string b) => NormalizeAddress(a).Length > 0 && NormalizeAddress(a) == NormalizeAddress(b);
}
