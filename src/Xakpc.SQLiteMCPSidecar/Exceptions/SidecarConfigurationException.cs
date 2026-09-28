namespace Xakpc.SQLiteMCPSidecar.Exceptions;

/// <summary>
/// Reports every configuration problem at one time, thus an operator repairs one deployment and
/// not one variable.
/// </summary>
public sealed class SidecarConfigurationException(IReadOnlyList<string> problems)
    : Exception(BuildMessage(problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;

    private static string BuildMessage(IReadOnlyList<string> problems) =>
        "The sidecar configuration is not valid:" + Environment.NewLine
        + string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
}
