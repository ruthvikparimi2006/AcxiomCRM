using AcxiomCRM.Data;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AcxiomCRM.Tests;

// Builds a throwaway database from the real migrations and hosts the real app against it.
// Override the database with ACXIOMCRM_TEST_DB.
public class DbFixture : IDisposable
{
    static readonly string ConnectionString = Environment.GetEnvironmentVariable("ACXIOMCRM_TEST_DB")
        ?? @"Server=.\SQLEXPRESS;Database=AcxiomCRM_Tests;Trusted_Connection=True;TrustServerCertificate=True";

    public const string AdminEmail = "admin@acxiomcrm.test";
    public const string AdminPassword = "Admin#Pass1";
    public const string Password = "Valid#Pass1";

    static int _counter;

    public WebApplicationFactory<Program> App { get; }

    public DbFixture()
    {
        using (var db = NewContext())
        {
            db.Database.EnsureDeleted();
            db.Database.Migrate();
        }

        App = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing"); // keeps the developer's user-secrets out of the tests
            b.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
            b.UseSetting("Seed:AdminEmail", AdminEmail);
            b.UseSetting("Seed:AdminPassword", AdminPassword);
            b.UseSetting("Jwt:Key", "test-only-signing-key-0123456789-abcdefghijklmnop");
            // Adds the test-only ThrowController (GEN-09 check).
            b.ConfigureTestServices(s => s.AddControllersWithViews().AddApplicationPart(typeof(DbFixture).Assembly));
        });
    }

    public HttpClient NewClient() =>
        App.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    // Creates a user through Identity, so the password is hashed exactly as in the app.
    public async Task<ApplicationUser> CreateIdentityUserAsync(string role = Roles.SalesExecutive, bool active = true,
        string? managerId = null, string? name = null)
    {
        using var scope = App.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var n = Next();
        var user = new ApplicationUser
        {
            UserName = $"login{n}", Email = $"login{n}@example.com", Name = name ?? $"Login {n}", IsActive = active, ManagerId = managerId,
        };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
        return user;
    }

    public AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public static int Next() => Interlocked.Increment(ref _counter);

    // Unique valid phone numbers (§16: ^[6-9]\d{9}$).
    public static string NextPhone() => "9" + Next().ToString("D9");

    public async Task<ApplicationUser> AddUserAsync()
    {
        using var db = NewContext();
        var n = Next();
        var user = new ApplicationUser { UserName = $"user{n}", Email = $"user{n}@example.com", Name = $"User {n}" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<Customer> AddCustomerAsync(string? email = null, string? phone = null)
    {
        var user = await AddUserAsync();
        using var db = NewContext();
        var customer = NewCustomer(user.Id, email, phone);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    public static Customer NewCustomer(string userId, string? email = null, string? phone = null) => new()
    {
        CustomerName = "Test Customer",
        Email = email ?? $"c{Next()}@example.com",
        Phone = phone ?? NextPhone(),
        OwnerId = userId,
        CreatedBy = userId,
    };

    public void Dispose()
    {
        App.Dispose();
        using var db = NewContext();
        db.Database.EnsureDeleted();
    }
}

[CollectionDefinition("db")]
public class DbCollection : ICollectionFixture<DbFixture>;
