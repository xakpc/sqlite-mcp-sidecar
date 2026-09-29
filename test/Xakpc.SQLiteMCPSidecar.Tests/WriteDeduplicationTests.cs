using Xakpc.SQLiteMCPSidecar.Security;

namespace Xakpc.SQLiteMCPSidecar.Tests;

/// <summary>
/// The idempotency cache on its own, for the one property that no end-to-end test can pin down.
/// </summary>
/// <remarks>
/// <para>
/// <c>WriteIdempotencyTests</c> proves the sequential contract through a real MCP client and it stays
/// the main coverage. This class exists for the concurrent case: an end-to-end version of it depends
/// on two calls reaching the same point at the same time, which no assertion can guarantee. Here the
/// two calls are two method calls, thus the proof needs no timing at all.
/// </para>
/// </remarks>
public sealed class WriteDeduplicationTests
{
    private const string Key = "one-request-id";

    /// <summary>The sequential contract, as the reference point for the test below it.</summary>
    [Fact]
    public void ARepeatedIdentifierReplaysTheStoredResponseAfterACommit()
    {
        var cache = new WriteDeduplication();
        var payload = WriteDeduplication.HashPayload("insert\nevents\nkind=x");

        Assert.Equal(DeduplicationOutcome.NotFound, cache.Check(Key, payload, out _));
        cache.Store(Key, payload, "rowsAffected: 1");

        Assert.Equal(DeduplicationOutcome.Replay, cache.Check(Key, payload, out var stored));
        Assert.Equal("rowsAffected: 1", stored);
    }

    /// <summary>
    /// A second caller with the same identifier is not told to execute while the first one is still
    /// running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Check</c> reserves the identifier in the same lock that reads it, thus a caller that
    /// arrives before the commit gets <c>InFlight</c>. Before the reservation existed it got
    /// <c>NotFound</c>, which means "execute", and the write applied two times. <c>maxRows</c> bounds
    /// one call; it does not bound the same call two times.
    /// </para>
    /// <para>
    /// This test needs no timing at all, which is why it exists next to the end-to-end one in
    /// <c>LiveDatabaseTests</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASecondCheckBeforeTheFirstStoreDoesNotSayExecute()
    {
        var cache = new WriteDeduplication();
        var payload = WriteDeduplication.HashPayload("insert\nevents\nkind=x");

        // The first caller arrives and starts its write.
        Assert.Equal(DeduplicationOutcome.NotFound, cache.Check(Key, payload, out _));

        // The retry of the same request arrives before the first one committed.
        Assert.Equal(DeduplicationOutcome.InFlight, cache.Check(Key, payload, out _));
    }

    /// <summary>
    /// A reservation that produced no committed write is released, and the identifier is free again.
    /// </summary>
    /// <remarks>
    /// This is the other half of the reservation contract. A leaked reservation would answer
    /// <c>InFlight</c> for the whole five minute window and make the "retry with the same requestId"
    /// instruction of <c>DatabaseBusy</c> impossible to follow.
    /// </remarks>
    [Fact]
    public void AReleasedReservationLetsTheRetryExecute()
    {
        var cache = new WriteDeduplication();
        var payload = WriteDeduplication.HashPayload("insert\nevents\nkind=x");

        Assert.Equal(DeduplicationOutcome.NotFound, cache.Check(Key, payload, out _));
        cache.Release(Key);

        // The write did not commit, thus the retry does the work.
        Assert.Equal(DeduplicationOutcome.NotFound, cache.Check(Key, payload, out _));

        cache.Store(Key, payload, "rowsAffected: 1");
        Assert.Equal(DeduplicationOutcome.Replay, cache.Check(Key, payload, out var stored));
        Assert.Equal("rowsAffected: 1", stored);
    }

    /// <summary>A release never drops a committed answer, whoever calls it and whenever.</summary>
    [Fact]
    public void AReleaseAfterACommitKeepsTheStoredResponse()
    {
        var cache = new WriteDeduplication();
        var payload = WriteDeduplication.HashPayload("insert\nevents\nkind=x");

        Assert.Equal(DeduplicationOutcome.NotFound, cache.Check(Key, payload, out _));
        cache.Store(Key, payload, "rowsAffected: 1");

        cache.Release(Key);

        Assert.Equal(DeduplicationOutcome.Replay, cache.Check(Key, payload, out var stored));
        Assert.Equal("rowsAffected: 1", stored);
    }

    /// <summary>
    /// A reserved identifier that a second caller asks for with different work is still a conflict.
    /// </summary>
    [Fact]
    public void ADifferentPayloadOnAReservedIdentifierIsAConflict()
    {
        var cache = new WriteDeduplication();
        var mine = WriteDeduplication.HashPayload("insert\nevents\nkind=x");
        var other = WriteDeduplication.HashPayload("delete\nevents\nid=1");

        Assert.Equal(DeduplicationOutcome.NotFound, cache.Check(Key, mine, out _));

        // Not InFlight: the caller asked for different work, thus the answer names the real mistake.
        Assert.Equal(DeduplicationOutcome.PayloadConflict, cache.Check(Key, other, out _));
    }
}
