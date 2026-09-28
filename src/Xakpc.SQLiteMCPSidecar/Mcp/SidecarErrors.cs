using ModelContextProtocol.Protocol;

namespace Xakpc.SQLiteMCPSidecar.Mcp;

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
