// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;

namespace McpSharp;

/// <summary>
/// Lets a tool handler emit meaningful, operation-specific progress during a long
/// <c>tools/call</c>. Two complementary mechanisms, both routed through the same
/// per-call progress session (shared monotonic counter + the keepalive timer):
///
/// <list type="bullet">
///   <item><description><b>Pull</b> — register a status provider via
///     <see cref="SetStatusProvider"/>. The keepalive timer polls it on each tick
///     and emits its message instead of the static heartbeat. One emission per
///     tick: no extra notifications between updates.</description></item>
///   <item><description><b>Push</b> — call <see cref="Report"/> to emit a status
///     message immediately (event-driven). Pushing also resets the keepalive
///     timer so a heartbeat never piggybacks right after a real update.</description></item>
/// </list>
///
/// Obtain the ambient reporter for the current call via <see cref="McpProgress.Current"/>.
/// When the call has no client progressToken, the ambient reporter is a no-op, so
/// handlers can call these methods unconditionally.
/// </summary>
public interface IProgressReporter
{
    /// <summary>Emit a progress message immediately (push).</summary>
    void Report(string message);

    /// <summary>
    /// Register a callback the keepalive timer polls on each tick (pull). The
    /// callback returns the current status message, or null to fall back to the
    /// static heartbeat text. Invoked on a background timer thread, so it must
    /// only read thread-safe state.
    /// </summary>
    void SetStatusProvider(Func<string?> provider);
}

/// <summary>
/// No-op reporter returned by <see cref="McpProgress.Current"/> when the current
/// call carries no progressToken. Lets handlers report unconditionally.
/// </summary>
internal sealed class NoOpProgressReporter : IProgressReporter
{
    public static readonly NoOpProgressReporter Instance = new();
    private NoOpProgressReporter() { }
    public void Report(string message) { }
    public void SetStatusProvider(Func<string?> provider) { }
}

/// <summary>
/// Ambient accessor for the progress reporter of the in-flight <c>tools/call</c>.
/// Set by the transport around the handler invocation and flowed to the handler
/// via <see cref="AsyncLocal{T}"/>. Never null — defaults to a no-op reporter.
/// </summary>
public static class McpProgress
{
    private static readonly AsyncLocal<IProgressReporter?> _current = new();

    /// <summary>The reporter for the current call, or a no-op reporter if none.</summary>
    public static IProgressReporter Current => _current.Value ?? NoOpProgressReporter.Instance;

    /// <summary>Set (or clear, with null) the ambient reporter. Transport-internal.</summary>
    internal static void SetCurrent(IProgressReporter? reporter) => _current.Value = reporter;
}

/// <summary>
/// Per-<c>tools/call</c> progress session. Owns a single monotonic notification
/// counter and the poll timer, and unifies the keepalive heartbeat with explicit
/// handler progress so the two never race or regress:
///
/// <list type="bullet">
///   <item><description>The timer ticks on a fast cadence and emits the status
///     provider's message (or the static heartbeat when no provider is set), but
///     only when the message text <b>changed</b> since the last emission (pull +
///     dedupe). If nothing changed for the idle-backstop window, it emits anyway
///     to keep the client's timeout clock alive.</description></item>
///   <item><description><see cref="Report"/> emits immediately regardless of
///     dedupe and resets the tick cadence (push).</description></item>
/// </list>
///
/// All emissions share one <see cref="Interlocked"/> counter (monotonic) and the
/// transport's thread-safe <see cref="McpTransport.SendProgress"/>. Wire payload is
/// message-only; the <c>progress</c> field carries the counter.
/// </summary>
internal sealed class ProgressSession : IProgressReporter
{
    // Fast adaptive cadence: prompt first sample, fine-grained during change.
    // Dedupe keeps steady/idle traffic minimal; the backstop preserves the
    // keepalive's timeout-reset purpose during genuinely idle waits.
    private const int InitialDelayMs = 1_000;
    private const int IntervalMs = 2_000;
    private const int IdleBackstopMs = 15_000;
    private const string HeartbeatMessage = "Still working…";

    private readonly McpTransport _transport;
    private readonly JsonNode _token;

    // Serializes Emit with Stop so no notification can be written after Stop
    // returns — including an explicit push from a background thread that outlives
    // the handler. Stop drains the timer, then acquires this gate to quiesce any
    // in-flight push.
    private readonly object _emitGate = new();

    private long _counter;
    private int _stopped;
    private Func<string?>? _provider;
    private Timer? _timer;

    // Dedupe/backstop state — guarded by _emitGate.
    private string? _lastEmitted;
    private long _lastEmitTicks;

    public ProgressSession(McpTransport transport, JsonNode token)
    {
        _transport = transport;
        _token = token;
        _lastEmitTicks = Environment.TickCount64;
        _timer = new Timer(OnTick, null, InitialDelayMs, IntervalMs);
    }

    /// <summary>Number of progress notifications emitted so far.</summary>
    public long NotificationsSent => Volatile.Read(ref _counter);

    public void SetStatusProvider(Func<string?> provider)
        => Volatile.Write(ref _provider, provider);

    public void Report(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (Volatile.Read(ref _stopped) != 0) return;
        Emit(message, force: true);
        // Reset the cadence so a pull tick doesn't fire right after a push.
        try { _timer?.Change(IntervalMs, IntervalMs); }
        catch (ObjectDisposedException) { /* stopped concurrently */ }
    }

    private void OnTick(object? _)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        Emit(Volatile.Read(ref _provider)?.Invoke() ?? HeartbeatMessage, force: false);
    }

    /// <summary>
    /// Decide whether a candidate message should be emitted. A push is always
    /// emitted; a pull tick is emitted only when it is the first message, the
    /// text changed, or the idle-backstop window has elapsed since the last
    /// emission (so an unchanged status still refreshes the client timeout).
    /// </summary>
    internal static bool ShouldEmit(string message, string? lastEmitted,
        long msSinceLastEmit, long backstopMs, bool force)
    {
        if (force) return true;
        if (lastEmitted == null) return true;
        if (!string.Equals(message, lastEmitted, StringComparison.Ordinal)) return true;
        return msSinceLastEmit >= backstopMs;
    }

    private void Emit(string message, bool force)
    {
        lock (_emitGate)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            var now = Environment.TickCount64;
            if (!ShouldEmit(message, _lastEmitted, now - _lastEmitTicks, IdleBackstopMs, force))
                return;
            _lastEmitted = message;
            _lastEmitTicks = now;
            var count = Interlocked.Increment(ref _counter);
            try { _transport.SendProgress(_token, count, message: message); }
            catch { /* transport may be closed */ }
        }
    }

    /// <summary>
    /// Stop emitting and drain any in-flight callback, so no progress
    /// notification can arrive after the tool response is written. Drains the
    /// timer (pull) and then quiesces any in-flight push via the emit gate.
    /// </summary>
    public void Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        var timer = _timer;
        _timer = null;
        if (timer != null)
        {
            using var drained = new ManualResetEvent(false);
            timer.Dispose(drained);
            drained.WaitOne();
        }
        // After this gate acquisition, any in-flight Emit has completed and no
        // new Emit can proceed (it observes _stopped inside the gate).
        lock (_emitGate) { }
    }
}
