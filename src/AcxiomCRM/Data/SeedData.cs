using AcxiomCRM.Models;
using AcxiomCRM.Services;
using Microsoft.AspNetCore.Identity;

namespace AcxiomCRM.Data;

// §16 #2: creates the three roles, and the first Admin from Seed:AdminEmail / Seed:AdminPassword
// (user-secrets or environment) only when no Admin exists yet.
public static class SeedData
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var roles = sp.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
                Check(await roles.CreateAsync(new IdentityRole(role)));

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        if ((await users.GetUsersInRoleAsync(Roles.Admin)).Count > 0) return;

        var config = sp.GetRequiredService<IConfiguration>();
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            sp.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SeedData))
                .LogWarning("No Admin account exists. Set Seed:AdminEmail and Seed:AdminPassword to create one.");
            return;
        }

        var admin = new ApplicationUser { UserName = email, Email = email, Name = "Administrator", EmailConfirmed = true };
        Check(await users.CreateAsync(admin, password));
        Check(await users.AddToRoleAsync(admin, Roles.Admin));
        await sp.GetRequiredService<AuditService>().LogAsync("Authentication", "AdminSeeded", "User", admin.Id,
            newValue: new { admin.UserName, admin.Email, Role = Roles.Admin });
    }

    static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Seeding failed: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
