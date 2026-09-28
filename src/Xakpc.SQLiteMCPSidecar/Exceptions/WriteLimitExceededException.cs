namespace Xakpc.SQLiteMCPSidecar.Exceptions;

/// <summary>
/// The write changed, or would change, more rows than the effective limit. Nothing was committed.
/// </summary>
/// <param name="Check">
/// <c>pre</c> when the bounded pre-count rejected the filter, <c>post</c> when the executed statement
/// changed too many rows and rolled back. The agent never sees this: the next action is the same in
/// both cases. It reaches the log, thus an operator can tell the two apart.
/// </param>
/// <param name="Limit">The effective limit that the operation passed.</param>
internal sealed class WriteLimitExceededException(string check, int limit)
    : Exception($"The write passed the effective limit of {limit} rows at the {check} check.")
{
    public string Check { get; } = check;

    public int Limit { get; } = limit;

    public const string PreCheck = "pre";

    public const string PostCheck = "post";
}
