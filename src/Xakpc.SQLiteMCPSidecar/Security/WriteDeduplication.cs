using System.Security.Cryptography;
using System.Text;

namespace Xakpc.SQLiteMCPSidecar.Security;

/// <summary>The outcome of the deduplication check that runs before a write.</summary>
public enum DeduplicationOutcome
{
    /// <summary>The identifier is new. Execute the write.</summary>
    NotFound,

    /// <summary>The identifier and the payload match a committed write. Return the stored response.</summary>
    Replay,

    /// <summary>The identifier matches, the payload does not. The caller asked for different work.</summary>
    PayloadConflict,

    /// <summary>
    /// The same request is running on another call right now. Do not execute: answer
    /// <c>DatabaseBusy</c> so that the caller retries with the same identifier.
    /// </summary>
    /// <remarks>
    /// Without this outcome two concurrent calls that carry one identifier both execute, which is the
    /// exact failure the identifier exists to prevent. <c>maxRows</c> bounds one call; it does not
    /// bound the same call two times.
    /// </remarks>
    InFlight,
}

/// <summary>
/// The idempotency cache of the write tools. The sidecar applies a write one time, also when the
/// caller sends the request two times.
/// </summary>
/// <remarks>
/// <para>
/// The MCP HTTP transport is stateless and the query timeout is finite. A write can commit and then
/// lose its response, and an MCP client retries such a call. Without this control the retry applies
/// the write a second time, which is the most frequent way that a correct agent damages a database.
/// <c>maxRows</c> bounds one call; it does not bound the same call two times. See
/// <c>.lode/decisions/0003-mandatory-idempotency-key.md</c>.
/// </para>
/// <para>
/// <b>Invariant.</b> Store a committed outcome only. A cached failure would make the instruction
/// "retry later" of <c>DatabaseBusy</c> and <c>WriteBudgetExceeded</c> impossible to follow for the
/// whole window.
/// </para>
/// <para>
/// <b>Invariant.</b> The cache is bounded in three ways. A dictionary with caller-supplied keys is a
/// memory-exhaustion primitive.
/// </para>
/// <para>
/// The cache is process memory. It resets at restart and two processes do not share it, which is the
/// same limit as the write budget and the same reason that one sidecar serves one database.
/// </para>
/// </remarks>
public sealed class WriteDeduplication
{
    /// <summary>The retry window. It is longer than any single write can take.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>The entry cap. Eviction removes the oldest entry first.</summary>
    private const int MaxEntries = 1000;

    /// <summary>The key length cap. A longer identifier is <c>InvalidWrite</c>.</summary>
    public const int MaxKeyLength = 128;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Tests one request identifier against the cache, and reserves it when it is new.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A repeated identifier with a different payload never returns the stored response. The caller
    /// asked for different work, thus the honest answer is a rejection and not a stale success.
    /// </para>
    /// <para>
    /// <b>Invariant.</b> <see cref="DeduplicationOutcome.NotFound"/> also reserves the identifier, in
    /// the same lock that read it. The caller that receives it owns the reservation and MUST end it
    /// with <see cref="Store"/> on a commit or <see cref="Release"/> on every other path. A test and
    /// a separate reserve would leave the window this closes.
    /// </para>
    /// <para>
    /// <b>Lesson.</b> Before the reservation existed, <c>Check</c> and <c>Store</c> took the lock
    /// separately and <c>Store</c> ran only after the commit, thus every caller that arrived before
    /// that commit was told to execute. Two concurrent calls with one identifier both wrote the row.
    /// The window was the whole duration of the write, and an MCP client retrying a call that timed
    /// out lands in it by construction.
    /// </para>
    /// </remarks>
    public DeduplicationOutcome Check(string requestId, string payloadHash, out string? response)
    {
        response = null;

        lock (_gate)
        {
            Trim();

            if (!_entries.TryGetValue(requestId, out var entry))
            {
                _insertionOrder.Enqueue(requestId);
                Reserve(requestId, payloadHash);
                return DeduplicationOutcome.NotFound;
            }

            // A released reservation means the earlier attempt did not commit. The identifier is free
            // again, thus the retry executes, and the entry is reused in place: the key is already in
            // the order queue and a second queue entry for it would let eviction drop a live one.
            if (entry is { Response: null, Reserved: false })
            {
                Reserve(requestId, payloadHash);
                return DeduplicationOutcome.NotFound;
            }

            if (!string.Equals(entry.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return DeduplicationOutcome.PayloadConflict;
            }

            // The payload matches and no response exists yet, thus the first call is still running.
            // Executing here would apply the same write a second time.
            if (entry.Response is null)
            {
                return DeduplicationOutcome.InFlight;
            }

            response = entry.Response;
            return DeduplicationOutcome.Replay;
        }
    }

    /// <summary>
    /// Ends a reservation that produced no committed write.
    /// </summary>
    /// <remarks>
    /// <b>Invariant.</b> Every path out of a write that did not commit calls this. A reservation that
    /// leaked would answer <see cref="DeduplicationOutcome.InFlight"/> for the whole window, and the
    /// "retry with the same requestId" instruction of <c>DatabaseBusy</c> and
    /// <c>WriteBudgetExceeded</c> would be impossible to follow.
    /// </remarks>
    public void Release(string requestId)
    {
        lock (_gate)
        {
            // Only a reservation is ended. A committed response is never dropped by a late caller.
            //
            // The entry stays in the dictionary as a released marker and it is NOT removed. The key
            // is in the order queue, thus removing it here would let a later reservation enqueue the
            // same key a second time, and eviction would then drop a live entry while a stale
            // duplicate sat in front of it. Trim clears the marker at the end of the window.
            if (_entries.TryGetValue(requestId, out var entry) && entry is { Response: null, Reserved: true })
            {
                _entries[requestId] = entry with { Reserved = false };
            }
        }
    }

    /// <summary>Records that this identifier is running. The caller holds the lock.</summary>
    /// <remarks>
    /// The caller enqueues the key when it is new. This method never does, because it also reuses a
    /// released entry whose key is already in the queue.
    /// </remarks>
    private void Reserve(string requestId, string payloadHash)
    {
        _entries[requestId] = new Entry(
            payloadHash, Response: null, Reserved: true, TimeProvider.System.GetTimestamp());

        while (_insertionOrder.Count > MaxEntries)
        {
            _entries.Remove(_insertionOrder.Dequeue());
        }
    }

    /// <summary>
    /// Stores the response of a committed write, and ends the reservation. Call it after the
    /// transaction commits.
    /// </summary>
    public void Store(string requestId, string payloadHash, string response)
    {
        ArgumentNullException.ThrowIfNull(response);

        lock (_gate)
        {
            Trim();

            // The reservation from Check is normally still here. It can be absent when eviction or
            // the window removed it while the write ran, thus the queue entry is added only when the
            // key is really new and the order queue keeps one entry for each key.
            if (!_entries.ContainsKey(requestId))
            {
                _insertionOrder.Enqueue(requestId);
            }

            _entries[requestId] = new Entry(payloadHash, response, Reserved: false, TimeProvider.System.GetTimestamp());

            while (_insertionOrder.Count > MaxEntries)
            {
                _entries.Remove(_insertionOrder.Dequeue());
            }
        }
    }

    /// <summary>
    /// The hash of a normalized request. It detects a repeated identifier that carries different work.
    /// </summary>
    /// <remarks>
    /// The caller builds the canonical text. The hash and not the text goes into the entry, thus the
    /// cache holds no column value and a memory dump discloses no data.
    /// </remarks>
    public static string HashPayload(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Drops the entries that left the window. The caller holds the lock.</summary>
    /// <remarks>
    /// A key at the front of the queue can be absent from the dictionary, because eviction removed it
    /// already. Discard such a key and continue: a <c>break</c> there would stop every later trim and
    /// make the window permanent.
    /// </remarks>
    private void Trim()
    {
        var now = TimeProvider.System.GetTimestamp();
        while (_insertionOrder.TryPeek(out var oldest))
        {
            if (!_entries.TryGetValue(oldest, out var entry))
            {
                _insertionOrder.Dequeue();
                continue;
            }

            if (TimeProvider.System.GetElapsedTime(entry.At, now) < Window)
            {
                return;
            }

            _insertionOrder.Dequeue();
            _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// One cache entry. The reservation and the committed answer are one record, thus a second
    /// dictionary is not needed and the order queue keeps holding one key each.
    /// </summary>
    /// <remarks>
    /// Three states: reserved (<c>Response</c> null, <c>Reserved</c> true), released (both false and
    /// null, the write did not commit and the identifier is free again) and committed
    /// (<c>Response</c> set).
    /// </remarks>
    private readonly record struct Entry(string PayloadHash, string? Response, bool Reserved, long At);
}
