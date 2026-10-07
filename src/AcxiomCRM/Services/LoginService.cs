using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity;

namespace AcxiomCRM.Services;

// One credential check for the web login and the API login (AUTH-02, AUTH-07, AUTH-12): active users only,
// lockout counting, and the same audit entries. It never signs anyone in; the caller issues a cookie or a token.
public class LoginService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, AuditService audit)
{
    public const string InvalidLogin = "Invalid login attempt.";
    public const string LockedOut = "This account is locked. Try again later or contact an administrator.";
    const string Module = "Authentication";

    public record Result(ApplicationUser? User, string? Error);

    // channel is "Web" or "API"; API events are marked in the audit details.
    public async Task<Result> CheckAsync(string login, string password, string channel)
    {
        string Detail(string text) => channel == "Web" ? text : $"{text} ({channel})";

        var user = await users.FindByEmailAsync(login) ?? await users.FindByNameAsync(login);
        if (user is null)
        {
            await audit.LogAsync(Module, "LoginFailed", "User", null, result: "Failure", details: Detail($"Unknown login '{login}'"));
            return new(null, InvalidLogin);
        }

        var wasLockedOut = await users.IsLockedOutAsync(user);
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            await audit.LogAsync(Module, "LoginSuccess", "User", user.Id, details: channel == "Web" ? null : channel, userId: user.Id);
            return new(user, null);
        }

        if (result.IsLockedOut)
        {
            await audit.LogAsync(Module, wasLockedOut ? "LoginFailed" : "Lockout", "User", user.Id, result: "Failure",
                details: Detail(wasLockedOut ? "Account is locked" : "Locked after repeated failed logins"), userId: user.Id);
            return new(null, LockedOut);
        }

        await audit.LogAsync(Module, "LoginFailed", "User", user.Id, result: "Failure",
            details: Detail(result.IsNotAllowed ? "Inactive account" : "Wrong password"), userId: user.Id);
        return new(null, InvalidLogin);
    }
}
