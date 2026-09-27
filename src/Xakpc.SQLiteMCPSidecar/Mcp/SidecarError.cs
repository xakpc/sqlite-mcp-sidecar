using ModelContextProtocol.Protocol;

namespace Xakpc.SQLiteMCPSidecar.Mcp;

/// <summary>
/// The closed set of error codes that a remote caller can see. The set is small, thus agent
/// behaviour stays predictable: an agent selects its next action from the code.
/// </summary>
public enum SidecarError
{
    /// <summary>Missing or wrong bearer token. The agent must stop.</summary>
    Unauthorized,

    /// <summary>The deployment does not have the permission. The agent must not retry.</summary>
    PermissionDenied,

    /// <summary>Malformed SQL, or more than one statement. The agent corrects the statement.</summary>
    InvalidQuery,

    /// <summary>The authorizer rejected an action. The action is not available.</summary>
    QueryRejected,

    /// <summary>Execution passed the query timeout. The agent makes the query smaller.</summary>
    QueryTimedOut,

    /// <summary>One single row is larger than the byte budget. The agent selects fewer columns.</summary>
    ResultTooLarge,

    /// <summary>The write request shape is wrong. The agent corrects the request.</summary>
    InvalidWrite,

    /// <summary>The filter matched more rows than the effective limit. Nothing changed.</summary>
    WriteLimitExceeded,

    /// <summary>The per-minute write budget is empty. The agent waits, then retries.</summary>
    WriteBudgetExceeded,

    /// <summary>The busy timeout expired. Another writer holds the lock. The agent retries later.</summary>
    DatabaseBusy,

    /// <summary>Any other SQLite failure. The agent reports it to the operator.</summary>
    DatabaseError,

    /// <summary>The backup did not start or did not complete.</summary>
    BackupFailed,
}

public static class SidecarErrors
{
    /// <summary>
    /// Builds the message that a remote caller sees. It carries the code and a short, fixed
    /// explanation only.
    /// </summary>
    /// <remarks>
    /// A remote caller never receives a stack trace, a secret, an absolute filesystem path or a
    /// raw SQLite message, because a SQLite message often names the database file. The detail goes
    /// to the log with the request identifier, thus an operator correlates the two.
    /// </remarks>
    public static string Message(SidecarError code, string explanation) => $"{code}: {explanation}";

    /// <summary>
    /// Builds the tool result that carries one error code to the agent.
    /// </summary>
    /// <remarks>
    /// <b>Lesson.</b> A tool must return this result and must not throw. The SDK catches an
    /// exception out of a tool and replaces the message with its own fixed text, <c>"An error
    /// occurred invoking '&lt;tool&gt;'."</c>. That masking is right for an unexpected fault, and it
    /// is wrong for the error model: the agent selects its next action from the code, and a masked
    /// message gives it nothing to select from.
    /// </remarks>
    public static CallToolResult Failure(SidecarError code, string explanation) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = Message(code, explanation) }],
    };

    /// <summary>Builds the tool result that carries a successful answer.</summary>
    public static CallToolResult Success(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
    };
}
