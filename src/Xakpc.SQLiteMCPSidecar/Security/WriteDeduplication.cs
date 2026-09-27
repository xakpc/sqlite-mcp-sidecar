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
    /// Tests one request identifier against the cache.
    /// </summary>
    /// <remarks>
    /// A repeated identifier with a different payload never returns the stored response. The caller
    /// asked for different work, thus the honest answer is a rejection and not a stale success.
    /// </remarks>
    public DeduplicationOutcome Check(string requestId, string payloadHash, out string? response)
    {
        response = null;

        lock (_gate)
        {
            Trim();

            if (!_entries.TryGetValue(requestId, out var entry))
            {
                return DeduplicationOutcome.NotFound;
            }

            if (!string.Equals(entry.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return DeduplicationOutcome.PayloadConflict;
            }

            response = entry.Response;
            return DeduplicationOutcome.Replay;
        }
    }

    /// <summary>Stores the response of a committed write. Call it after the transaction commits.</summary>
    public void Store(string requestId, string payloadHash, string response)
    {
        lock (_gate)
        {
            Trim();

            if (!_entries.ContainsKey(requestId))
            {
                _insertionOrder.Enqueue(requestId);
            }

            _entries[requestId] = new Entry(payloadHash, response, TimeProvider.System.GetTimestamp());

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

    private readonly record struct Entry(string PayloadHash, string Response, long At);
}
