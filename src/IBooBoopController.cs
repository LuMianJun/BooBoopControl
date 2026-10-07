using System;
using System.Threading;
using System.Threading.Tasks;

namespace BooBoopBridge;

/// <summary>A Boo Boop-specific boundary, independent of all game and Unity types.</summary>
public interface IBooBoopController : IAsyncDisposable
{
    bool IsReady { get; }
    event Action<string>? Diagnostic;
    event Action<string>? Error;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SetStretchAsync(int level, CancellationToken cancellationToken = default);
    Task SetVibrationAsync(int level, CancellationToken cancellationToken = default);
    // Invalidates queued positive commands and independently attempts both outputs at zero.
    Task StopAsync(CancellationToken cancellationToken = default);
}
