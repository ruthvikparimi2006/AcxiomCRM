using System.Security.Claims;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AcxiomCRM.Controllers.Api;

[Route("api/auth")]
public class AuthApiController(
    LoginService login, TokenService tokens, UserManager<ApplicationUser> users, AuditService audit) : ApiControllerBase
{
    public const string LoginRateLimit = "api-login";

    // Public, but limited to 5 attempts per minute per IP address (D4, API-08); further attempts get 429.
    // Inactive users, wrong passwords and lockout are handled exactly as on the web login.
    [AllowAnonymous, HttpPost("login"), EnableRateLimiting(LoginRateLimit)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
    {
        var result = await login.CheckAsync(request.Login, request.Password, "API");
        if (result.User is not { } user)
            return Problem(title: result.Error, statusCode: StatusCodes.Status401Unauthorized);

        var (token, expiresAt) = await tokens.CreateAsync(user);
        var role = (await users.GetRolesAsync(user)).FirstOrDefault();
        return new LoginResponse(token, expiresAt, new ApiUserDto(user.Id, user.Name, role));
    }

    // §16 #13: tokens are stateless, so logout is recorded and the client discards its token.
    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        await audit.LogAsync("Authentication", "Logout", "User", userId, details: "API (client discards the token)", userId: userId);
        return Ok(new { message = "Logged out. Discard the token." });
    }
}
