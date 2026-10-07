using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BooBoopBridge;

// Transport does not choose actions. The caller owns its initialization and control policy.
internal sealed class NativeHost : IAsyncDisposable
{
    private readonly string _hostPath;
    private readonly string _hostDirectory;

    public NativeHost(string hostPath)
    {
        _hostPath = hostPath;
        _hostDirectory = Path.GetDirectoryName(hostPath)!;
    }
    private const string Origin = "chrome-extension://adelilgjkmnnpdgnjaadonibjkikgeaa/";
    private const int MaximumFrameBytes = 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly JsonSerializerOptions WriteOptions = new() { MaxDepth = 16 };
    private static readonly HashSet<string> AllowedCommands = new(
        new[] { "get_state", "search", "connect", "disconnect" }.Concat(
            Enumerable.Range(0, 10).SelectMany(n => new[] { $"aa01{n:x2}", $"bb01{n:x2}" })),
        StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Session? _session;
    private Task? _stopTask;
    private bool _disposed;

    public event Action<JsonElement>? MessageReceived;
    public event Action<string>? Diagnostic;

    private sealed class Session
    {
        public readonly Process Process;
        public Session(Process process) { Process = process; }
        public readonly CancellationTokenSource Lifetime = new();
        public readonly SemaphoreSlim Writes = new(1, 1);
        public int Usable = 1;
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                if (_session is not { } session || Volatile.Read(ref session.Usable) == 0) return false;
                try { return !session.Process.HasExited; }
                catch { return false; }
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeHost));
            if (_session is not null) throw new InvalidOperationException("Stop the existing session before starting again.");
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
            if (!File.Exists(_hostPath)) throw new FileNotFoundException("Vendor host executable was not found. Configure [Host] ExecutablePath to its absolute path.");
            var info = new ProcessStartInfo
            {
                FileName = _hostPath, WorkingDirectory = _hostDirectory, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), CreateNoWindow = true
            };
            info.ArgumentList.Add(Origin);
            info.ArgumentList.Add("--parent-window=0");
            var process = new Process { StartInfo = info };
            try { if (!process.Start()) throw new InvalidOperationException("Host did not start."); }
            catch { process.Dispose(); throw; }
            var session = new Session(process);
            _session = session;
            _stopTask = null;
            Observe(Task.Run(() => ReadMessagesAsync(session)));
            Observe(Task.Run(() => DrainStderrAsync(session)));
        }
    }

    public async Task SendAsync(object message, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, WriteOptions);
        if (body.Length == 0 || body.Length > MaximumFrameBytes) throw new InvalidDataException("Outgoing frame size is invalid.");
        using (JsonDocument document = JsonDocument.Parse(body, JsonOptions))
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A command object is required.");
            int commandCount = 0;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name != "command") continue;
                commandCount++;
                if (property.Value.ValueKind != JsonValueKind.String ||
                    !AllowedCommands.Contains(property.Value.GetString() ?? ""))
                    throw new InvalidDataException("Command is not allowed.");
            }
            if (commandCount != 1) throw new InvalidDataException("Exactly one command property is required.");
        }
        Session session;
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NativeHost));
            session = _session ?? throw new InvalidOperationException("Start the host first.");
            if (Volatile.Read(ref session.Usable) == 0) throw new InvalidOperationException("Stop and manually restart the host.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        deadline.CancelAfter(timeout);
        // Outer deadline includes lock, write and flush, even if a pipe ignores cancellation.
        Task sending = Task.Run(async () =>
        {
            await session.Writes.WaitAsync(deadline.Token);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref session.Usable) == 0) throw new InvalidOperationException("Session is unusable.");
                byte[] frame = new byte[4 + body.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)body.Length);
                body.CopyTo(frame, 4);
                Stream input = session.Process.StandardInput.BaseStream;
                await input.WriteAsync(frame.AsMemory(), deadline.Token);
                await input.FlushAsync(deadline.Token);
            }
            finally { session.Writes.Release(); }
        });
        Observe(sending);
        try { await sending.WaitAsync(deadline.Token); }
        catch
        {
            Fault(session, "Send failed or was interrupted; delivery is uncertain. Stop and manually restart the host.");
            throw;
        }
    }

    private async Task ReadMessagesAsync(Session session)
    {
        try
        {
            Stream output = session.Process.StandardOutput.BaseStream;
            byte[] header = new byte[4];
            while (!session.Lifetime.IsCancellationRequested)
            {
                await ReadExactlyAsync(output, header.AsMemory(), session.Lifetime.Token);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length == 0 || length > MaximumFrameBytes) throw new InvalidDataException();
                byte[] body = new byte[(int)length];
                await ReadExactlyAsync(output, body.AsMemory(), session.Lifetime.Token);
                using JsonDocument outer = JsonDocument.Parse(body, JsonOptions);
                JsonElement root = outer.RootElement.Clone();
                for (int depth = 0; root.ValueKind == JsonValueKind.String; depth++)
                {
                    if (depth >= 2) throw new InvalidDataException();
                    string nested = root.GetString() ?? "";
                    if (Encoding.UTF8.GetByteCount(nested) > MaximumFrameBytes) throw new InvalidDataException();
                    using JsonDocument inner = JsonDocument.Parse(nested, JsonOptions);
                    root = inner.RootElement.Clone();
                }
                if (Volatile.Read(ref session.Usable) == 0) return;
                foreach (Delegate callback in MessageReceived?.GetInvocationList() ?? Array.Empty<Delegate>())
                {
                    try { ((Action<JsonElement>)callback)(root); }
                    catch { Report("A message callback failed; its exception content was suppressed."); }
                }
            }
        }
        catch
        {
            if (!session.Lifetime.IsCancellationRequested)
                Fault(session, "Reader stopped after an invalid frame, closed pipe or read failure. Stop and manually restart the host.");
        }
    }

    // Stream.ReadExactlyAsync is not part of the .NET 6 runtime used by this BepInEx build.
    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Incomplete native frame.");
            total += read;
        }
    }

    private static async Task DrainStderrAsync(Session session)
    {
        byte[] buffer = new byte[8192];
        try
        {
            Stream error = session.Process.StandardError.BaseStream;
            while (await error.ReadAsync(buffer.AsMemory(), session.Lifetime.Token) > 0) { }
        }
        catch { /* Never retain or display stderr. */ }
        finally { Array.Clear(buffer, 0, buffer.Length); }
    }

    public Task StopAsync()
    {
        lock (_gate) return _session is null ? Task.CompletedTask : _stopTask ??= StopCoreAsync(_session);
    }

    private async Task StopCoreAsync(Session session)
    {
        Interlocked.Exchange(ref session.Usable, 0);
        session.Lifetime.Cancel();
        Task close = Task.Run(() => { try { session.Process.StandardInput.BaseStream.Close(); } catch { } });
        Observe(close);
        try { await close.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch { }
        bool exited = await WaitForExitAsync(session.Process, 700);
        if (!exited)
        {
            try { session.Process.Kill(entireProcessTree: false); }
            catch { Report("Could not request termination of the owned child."); }
            exited = await WaitForExitAsync(session.Process, 1000);
        }
        if (!exited)
        {
            Report("Owned child exit is unconfirmed; stop again or check that child manually before restarting.");
            lock (_gate) _stopTask = null; // Retain ownership; allow another explicit stop attempt.
            throw new InvalidOperationException("Owned child exit could not be confirmed.");
        }
        // Background operations may still unwind: do not dispose their CTS or semaphore.
        session.Process.Dispose();
        lock (_gate) { if (ReferenceEquals(_session, session)) _session = null; }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int milliseconds)
    {
        try
        {
            Task waiting = process.WaitForExitAsync();
            Observe(waiting);
            await waiting.WaitAsync(TimeSpan.FromMilliseconds(milliseconds));
            return true;
        }
        catch { return false; }
    }

    private void Fault(Session session, string description)
    {
        if (Interlocked.Exchange(ref session.Usable, 0) != 0) Report(description);
    }

    private void Report(string description)
    {
        foreach (Delegate callback in Diagnostic?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action<string>)callback)(description); } catch { }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync();
    }
}
