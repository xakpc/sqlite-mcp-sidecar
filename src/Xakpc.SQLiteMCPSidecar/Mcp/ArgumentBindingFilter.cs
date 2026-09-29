using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Xakpc.SQLiteMCPSidecar.Mcp;

/// <summary>
/// Turns a failure of the SDK argument binder into an error code of this product.
/// </summary>
/// <remarks>
/// <para>
/// <b>The error model must own every failure that an agent can cause.</b> A tool method never throws
/// and it always answers with a code, but the binder runs <b>before</b> the method: an argument of
/// the wrong JSON type never reaches it. The SDK catches that and replaces the message with its own
/// fixed text, <c>"An error occurred invoking 'update'."</c>, thus the agent gets
/// <c>IsError</c> and nothing to select a next action from. No build warning reports it.
/// </para>
/// <para>
/// <b>Lesson.</b> This is the other half of the <c>= null</c> lesson in
/// <c>.lode/mcp/write-tool-arguments.md</c>. That one fixed the absent argument by giving each
/// mandatory parameter a default and validating in the method. The wrong-type half cannot be fixed
/// that way, because the conversion is what fails. A corpus of malformed calls is what found it.
/// </para>
/// <para>
/// <b>Why a filter and not <see cref="JsonElement"/> parameters.</b> Declaring every argument as
/// <see cref="JsonElement"/> and converting by hand also returns a code, and it costs the JSON
/// schema: <c>where</c> stops describing the condition object and the agent loses the one machine
/// readable account of the shape it has to build. The filter keeps the rich schema for the agent
/// that reads it and gives a code to the agent that ignored it. It also covers each tool that this
/// product gains later, with no per-argument work.
/// </para>
/// <para>
/// The trade is that this names the tool and not the argument, because the binder converts each
/// argument on its own and the exception carries the path <c>$</c>. The tool description and the
/// schema carry the shape of each argument, thus the agent has what it needs to find the wrong one.
/// </para>
/// </remarks>
internal static partial class ArgumentBindingFilter
{
    /// <summary>
    /// Wraps <c>tools/call</c> and answers a binding failure with
    /// <see cref="SidecarError.InvalidWrite"/> or <see cref="SidecarError.InvalidQuery"/>.
    /// </summary>
    /// <remarks>
    /// Only <see cref="JsonException"/> is handled. Every other exception belongs to the tool, which
    /// catches its own and returns a code, thus catching more here would hide a real defect behind a
    /// caller-mistake code.
    /// </remarks>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Handle =>
        next => async (request, cancellationToken) =>
        {
            try
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                var tool = request.Params?.Name ?? "-";

                // The logger comes from the request, because a filter is registered before the
                // container is built and resolving one there would build a second container.
                var logger = request.Services?.GetService<ILoggerFactory>()?
                    .CreateLogger(typeof(ArgumentBindingFilter));

                // The exception message can quote the JSON that failed to convert, and a caller value
                // is row data. It reaches the log only, and the caller gets fixed text.
                if (logger is not null)
                {
                    LogBindingFailed(logger, tool, exception);
                }

                var code = tool is "query" ? SidecarError.InvalidQuery : SidecarError.InvalidWrite;
                return SidecarErrors.Failure(
                    code,
                    $"An argument of '{tool}' has the wrong JSON type. Read the tool description and "
                  + "the input schema, and send each argument in the shape they state.");
            }
        };

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information,
        Message = "Tool argument binding failed. tool={Tool}")]
    private static partial void LogBindingFailed(ILogger logger, string tool, Exception exception);
}
