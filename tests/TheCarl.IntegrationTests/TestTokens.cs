using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TheCarl.Application.Security;

namespace TheCarl.IntegrationTests;

/// <summary>
/// Mints access tokens the way <c>AuthService</c> does, so tests can exercise any tenant,
/// branch and role combination without going through a login round-trip.
/// </summary>
internal static class TestTokens
{
    public static string Create(
        Guid userId,
        Guid organizationId,
        Guid? branchId,
        params string[] roles)
        => Create(userId, organizationId, branchId, SigningKeyBytes(CarlApiFactory.SigningKey), roles);

    /// <summary>Mints a token signed with a different key, to prove signatures are checked.</summary>
    public static string CreateWithForeignKey(
        Guid userId,
        Guid organizationId,
        Guid? branchId,
        params string[] roles)
        => Create(userId, organizationId, branchId, SigningKeyBytes("an-entirely-different-key-of-sufficient-length!!"), roles);

    private static string Create(
        Guid userId,
        Guid organizationId,
        Guid? branchId,
        byte[] keyBytes,
        string[] roles)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(keyBytes),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(CarlClaimTypes.OrganizationId, organizationId.ToString()),
            new(CarlClaimTypes.SecurityStamp, Guid.NewGuid().ToString("N"))
        };

        if (branchId is { } branch)
        {
            claims.Add(new Claim(CarlClaimTypes.BranchId, branch.ToString()));
        }

        foreach (var role in roles)
        {
            claims.Add(new Claim(CarlClaimTypes.Role, role));
        }

        var token = new JwtSecurityToken(
            issuer: CarlApiFactory.Issuer,
            audience: CarlApiFactory.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static byte[] SigningKeyBytes(string key) => Encoding.UTF8.GetBytes(key);

    public static HttpClient Authenticated(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
