using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Xakpc.SQLiteMCPSidecar.Configuration;

namespace Xakpc.SQLiteMCPSidecar.Security;

public static class DeploymentTokenDefaults
{
    public const string Scheme = "DeploymentToken";
}

/// <summary>
/// Authenticates the one deployment bearer token and issues the deployment permission set as
/// claims. The claims are what the <c>[Authorize(Policy = "perm:...")]</c> attribute on each MCP
/// tool tests.
/// </summary>
public sealed partial class DeploymentTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly byte[] _expectedToken;
    private readonly ClaimsPrincipal _principal;

    public DeploymentTokenAuthenticationHandler(
        SidecarOptions sidecarOptions,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _expectedToken = Encoding.UTF8.GetBytes(sidecarOptions.Token);

        // One identity for the deployment. Each permission arrives as one claim, thus an
        // authorization policy can require it.
        var identity = new ClaimsIdentity(DeploymentTokenDefaults.Scheme, ClaimTypes.Name, PermissionSet.ClaimType);
        identity.AddClaim(new Claim(ClaimTypes.Name, "deployment"));
        foreach (var permission in sidecarOptions.Permissions.ToNames())
        {
            identity.AddClaim(new Claim(PermissionSet.ClaimType, permission));
        }

        _principal = new ClaimsPrincipal(identity);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Reject());
        }

        var presented = Encoding.UTF8.GetBytes(header[BearerPrefix.Length..].Trim());

        // Constant time. A plain string comparison leaks length and prefix through timing.
        if (!CryptographicOperations.FixedTimeEquals(presented, _expectedToken))
        {
            return Task.FromResult(Reject());
        }

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(_principal, DeploymentTokenDefaults.Scheme)));
    }

    /// <summary>
    /// A bare <c>Bearer</c> challenge. It carries no error description, thus the response does not
    /// state whether the header was absent or the token was wrong.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }

    private AuthenticateResult Reject()
    {
        // Two addresses, because a public deployment runs behind a reverse proxy: remote is the
        // proxy and it is true, forwarded is what the caller claims and a caller can forge it.
        // The header is read here and not through the forwarded-headers middleware, which
        // CreateSlimBuilder does not add, and which would replace the true address with the claim.
        LogRejected(
            Logger,
            Request.Path.Value ?? "/",
            Context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded ? forwarded : "none");

        // The same result for an absent header and for a wrong token. Never the presented value.
        return AuthenticateResult.Fail("Unauthorized.");
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Unauthorized request rejected. path={Path} remote={Remote} forwarded={Forwarded}")]
    private static partial void LogRejected(ILogger logger, string path, string remote, string forwarded);
}
