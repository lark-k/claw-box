using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using ClawBox.Executor.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ClawBox.Executor.Security;

public sealed class TokenAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> schemes,
    ILoggerFactory logger, UrlEncoder encoder, ExecutorOptions options)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    public const string SchemeName = "ExecutorToken";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        byte[] incoming = SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]));
        string? role = Matches(incoming, options.AdminToken) ? "admin" :
            Matches(incoming, options.WorkerToken) ? "worker" : null;
        if (role is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid credential"));
        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, role), new Claim(ClaimTypes.Role, role)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    private static bool Matches(byte[] incoming, string expected) =>
        CryptographicOperations.FixedTimeEquals(incoming, SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
