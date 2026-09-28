namespace Xakpc.SQLiteMCPSidecar.Exceptions;

/// <summary>
/// The request shape is wrong. The message reaches the agent, thus it names only what the caller
/// already sent and it never carries a value, a path or a SQLite message.
/// </summary>
internal sealed class InvalidWriteException(string reason) : Exception(reason);
