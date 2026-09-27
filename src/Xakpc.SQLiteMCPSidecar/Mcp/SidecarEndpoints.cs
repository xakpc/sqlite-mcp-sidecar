namespace Xakpc.SQLiteMCPSidecar.Mcp;

/// <summary>
/// The fixed paths of the sidecar and the name of the request rate-limit policy. One deployment
/// serves these two paths and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Mcp"/> is the whole public path and not a root-mounted <c>/mcp</c>. A reverse proxy
/// matches the prefix and forwards the request unchanged, thus the URL that the agent calls is the
/// URL that the endpoint routes. Prefix rewriting is the part of a proxy configuration that fails
/// quietly, and a stripped prefix gives a <c>404</c> that looks like a server fault. Kamal
/// therefore needs <c>strip_path_prefix: false</c> and Coolify needs the strip-prefix option off.
/// </para>
/// <para>
/// <see cref="Health"/> stays at the root. A platform probes the container directly and not through
/// the proxy, thus a prefix on this path adds one more way to break a probe and gives nothing.
/// </para>
/// </remarks>
public static class SidecarEndpoints
{
    /// <summary>The MCP endpoint. It needs the deployment token.</summary>
    public const string Mcp = "/db/mcp";

    /// <summary>Process health. It needs no token and it touches no database.</summary>
    public const string Health = "/health";

    /// <summary>The rate-limit policy that <see cref="Mcp"/> carries.</summary>
    public const string McpRateLimitPolicy = "mcp";
}
