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
