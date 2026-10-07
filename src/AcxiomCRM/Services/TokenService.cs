using System.Security.Claims;
using System.Text;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AcxiomCRM.Services;

// JWT bearer tokens for the REST API (§16 #13): 60 minutes, signed with Jwt:Key from user-secrets/environment.
// The token carries the same claims as the web cookie (id, name, role, security stamp); Program.cs rejects a token
// once the user is deactivated or their security stamp changes (password reset, role change).
public class TokenService(SignInManager<ApplicationUser> signIn, IConfiguration config)
{
    public const string Issuer = "AcxiomCRM";
    public const int LifetimeMinutes = 60;

    public static SymmetricSecurityKey SigningKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length < 32)
            throw new InvalidOperationException("Jwt:Key must be configured (user-secrets or environment) with at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public async Task<(string Token, DateTime ExpiresAt)> CreateAsync(ApplicationUser user)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        var expires = DateTime.UtcNow.AddMinutes(LifetimeMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(principal.Claims),
            Issuer = Issuer,
            Audience = Issuer,
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey(config["Jwt:Key"]), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
