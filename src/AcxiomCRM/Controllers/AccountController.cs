using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers;

// Login, Register, Logout, change password and the admin-issued reset link (AUTH-01..07, AUTH-12).
// Anti-forgery is validated on every POST by the global filter in Program.cs (AUTH-09).
public class AccountController(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn,
    LoginService login,
    AuditService audit) : Controller
{
    const string Module = "Authentication";

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await login.CheckAsync(model.Login, model.Password, "Web");
        if (result.User is null)
        {
            ModelState.AddModelError("", result.Error!);
            return View(model);
        }

        await signIn.SignInAsync(result.User, isPersistent: false);
        return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl) : RedirectToAction("Index", "Dashboard");
    }

    [AllowAnonymous, HttpGet]
    public IActionResult Register() => View();

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        // §16 #2: self-registered users are active SalesExecutives with no records.
        var user = new ApplicationUser { UserName = model.Email, Email = model.Email, Name = model.Name };
        var created = await users.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            AddErrors(created);
            return View(model);
        }

        var roleAdded = await users.AddToRoleAsync(user, Roles.SalesExecutive);
        if (!roleAdded.Succeeded)
            throw new InvalidOperationException("Could not assign the SalesExecutive role.");

        await audit.LogAsync(Module, "Register", "User", user.Id,
            newValue: new { user.UserName, user.Email, user.Name, Role = Roles.SalesExecutive }, userId: user.Id);

        await signIn.SignInAsync(user, isPersistent: false);
        await audit.LogAsync(Module, "LoginSuccess", "User", user.Id, details: "Signed in after registration", userId: user.Id);
        return RedirectToAction("Index", "Dashboard");
    }

    [Authorize, HttpPost]
    public async Task<IActionResult> Logout()
    {
        var userId = users.GetUserId(User);
        await audit.LogAsync(Module, "Logout", "User", userId, userId: userId);
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [Authorize, HttpGet]
    public IActionResult ChangePassword() => View();

    [Authorize, HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();

        var result = await users.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            await audit.LogAsync(Module, "PasswordChanged", "User", user.Id, result: "Failure", userId: user.Id);
            AddErrors(result);
            return View(model);
        }

        await signIn.RefreshSignInAsync(user);
        await audit.LogAsync(Module, "PasswordChanged", "User", user.Id, userId: user.Id);
        TempData["Success"] = "Your password has been changed.";
        return RedirectToAction(nameof(ChangePassword));
    }

    // Opened from the one-time link an administrator generates (Step 3). Identity tokens expire after
    // the lifespan set in Program.cs and stop working once used, because a reset changes the security stamp.
    [AllowAnonymous, HttpGet]
    public IActionResult ResetPassword(string? userId, string? token)
    {
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token))
        {
            TempData["Error"] = "This reset link is invalid or has expired. Ask an administrator for a new one.";
            return RedirectToAction(nameof(Login));
        }
        return View(new ResetPasswordViewModel { UserId = userId, Token = token });
    }

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var user = await users.FindByIdAsync(model.UserId);
        var result = user is null
            ? IdentityResult.Failed(new IdentityError { Code = nameof(IdentityErrorDescriber.InvalidToken) })
            : await users.ResetPasswordAsync(user, model.Token, model.NewPassword);

        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                await audit.LogAsync(Module, "PasswordReset", "User", user?.Id, result: "Failure",
                    details: "Invalid or expired reset link", userId: user?.Id);
                ModelState.AddModelError("", "This reset link is invalid or has expired. Ask an administrator for a new one.");
            }
            else
            {
                AddErrors(result);
            }
            return View(model);
        }

        await audit.LogAsync(Module, "PasswordReset", "User", user!.Id, userId: user.Id);
        TempData["Success"] = "Your password has been reset. You can now log in.";
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous, HttpGet]
    public IActionResult AccessDenied() => View();

    void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
            ModelState.AddModelError("", error.Description);
    }
}
