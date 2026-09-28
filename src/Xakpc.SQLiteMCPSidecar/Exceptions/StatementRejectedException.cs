using Xakpc.SQLiteMCPSidecar.Database;

namespace Xakpc.SQLiteMCPSidecar.Exceptions;

/// <summary>
/// The statement did not pass the one-statement check, or the authorizer rejected it. The message
/// never reaches a remote caller: the tool maps <see cref="Check"/> to an error code.
/// </summary>
internal sealed class StatementRejectedException(StatementCheck check)
    : Exception($"The statement did not pass the sandbox check: {check}.")
{
    public StatementCheck Check { get; } = check;
}
