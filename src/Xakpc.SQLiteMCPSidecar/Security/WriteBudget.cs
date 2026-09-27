using Xakpc.SQLiteMCPSidecar.Configuration;

namespace Xakpc.SQLiteMCPSidecar.Security;

/// <summary>
/// The rolling per-minute cap on written rows. A broad filter is one failure mode, and many small
/// valid writes are another: this control covers the second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Invariant.</b> The budget belongs to the sidecar <b>process</b>. It resets at restart, and two
/// processes against one database have two independent budgets. The deployment documentation must
/// state that one sidecar serves one database. Do not confuse it with the request budget, which the
/// rate limiter in <c>Program.cs</c> owns.
/// </para>
/// <para>
/// <b>Invariant.</b> Count committed rows and never attempted rows. A write that rolled back changed
/// nothing, thus it must not consume the budget of a write that would succeed.
/// </para>
/// <para>
/// The timestamps come from <see cref="TimeProvider.GetTimestamp"/> and not from the wall clock. The
/// window must not open or close because an operator corrected the system time.
/// </para>
/// </remarks>
public sealed class WriteBudget
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly SidecarOptions _options;
    private readonly Queue<Entry> _entries = new();
    private readonly Lock _gate = new();
    private int _rowsInWindow;

    public WriteBudget(SidecarOptions options) => _options = options;

    /// <summary>
    /// True while the window holds less than the configured cap.
    /// </summary>
    /// <remarks>
    /// The check is a gate and not a reservation. One write can therefore cross the cap, because the
    /// affected row count is not known before execution. <c>SQLITE_SIDECAR_MAX_WRITE_ROWS</c> already
    /// bounds one write, thus the overshoot is bounded too, and the budget throttles the next
    /// operation. <c>SECURITY.md</c> must state the behaviour in these words.
    /// </remarks>
    public bool HasCapacity()
    {
        lock (_gate)
        {
            Trim();
            return _rowsInWindow < _options.MaxWriteRowsPerMinute;
        }
    }

    /// <summary>Adds the committed row count of one write to the window.</summary>
    public void Record(int rows)
    {
        if (rows <= 0)
        {
            return;
        }

        lock (_gate)
        {
            Trim();
            _entries.Enqueue(new Entry(TimeProvider.System.GetTimestamp(), rows));
            _rowsInWindow += rows;
        }
    }

    /// <summary>Drops the entries that left the window. The caller holds the lock.</summary>
    private void Trim()
    {
        var now = TimeProvider.System.GetTimestamp();
        while (_entries.TryPeek(out var oldest)
            && TimeProvider.System.GetElapsedTime(oldest.At, now) >= Window)
        {
            _rowsInWindow -= _entries.Dequeue().Rows;
        }
    }

    private readonly record struct Entry(long At, int Rows);
}
