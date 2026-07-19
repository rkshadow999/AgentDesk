using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud;

internal sealed class CloudAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CloudBootstrapTokens bootstrapTokens,
    CloudStore cloudStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "AgentDeskBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadBearerToken();
        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        CloudIdentity? cloudIdentity;
        if (bootstrapTokens.Matches(token))
        {
            cloudIdentity = new CloudIdentity("default", "bootstrap-admin", "admin");
        }
        else
        {
            cloudIdentity = await cloudStore
                .AuthenticateTokenAsync(token, Context.RequestAborted)
                .ConfigureAwait(false);
            if (cloudIdentity is null)
            {
                return AuthenticateResult.Fail("Invalid bearer token.");
            }
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, cloudIdentity.SubjectId),
            new(ClaimTypes.Role, cloudIdentity.Role),
            new("agentdesk:team", cloudIdentity.TeamId),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private string? ReadBearerToken()
    {
        if (AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(header.Parameter))
        {
            return header.Parameter;
        }

        return null;
    }
}

internal static class CloudPrincipalExtensions
{
    public static CloudIdentity CloudIdentity(this ClaimsPrincipal principal)
    {
        var teamId = principal.FindFirstValue("agentdesk:team");
        var subjectId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var role = principal.FindFirstValue(ClaimTypes.Role);
        if (teamId is null || subjectId is null || role is null)
        {
            throw new InvalidOperationException("The authenticated principal is incomplete.");
        }
        return new CloudIdentity(teamId, subjectId, role);
    }
}
