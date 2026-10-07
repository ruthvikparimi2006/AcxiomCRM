using System.Net;
using System.Text.RegularExpressions;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 9: final cross-module validation pass (VAL-01..23, §5.4, VAL-10/11/12, AUTH-09) with crafted requests.
// Module-specific rules are tested in each module's own test class; this sweeps the same checks over every form.
[Collection("db")]
public partial class ValidationSweepTests(DbFixture fx)
{
    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // Text that must never reach a user: exception types and framework internals...
    static readonly string[] Internals = ["SqlException", "EntityFramework", "System.", "Exception", "   at "];

    // ...and database object names (IX_/CK_/FK_ as whole words, so a random anti-forgery token can't match).
    [GeneratedRegex(@"(?<![A-Za-z0-9])(IX|CK|FK|PK)_[A-Z]")]
    private static partial Regex DatabaseObjectName();

    [GeneratedRegex(@"The [^""<>]+? field is required|The field [^""<>]+? must|is not valid for|The value '[^']*' is invalid")]
    private static partial Regex FrameworkDefaultMessage();

    // ---------- valid baseline forms, one per module ----------

    sealed record Target(string Url, Dictionary<string, string> Form, Func<Task<int>> Count, bool AsAdmin = false, bool Anonymous = false);

    async Task<(ApplicationUser User, HttpClient Client)> SalesAsync()
    {
        var user = await fx.CreateIdentityUserAsync();
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    Task<HttpClient> AdminAsync() => SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);

    async Task<int> CustomerOfAsync(string ownerId)
    {
        using var db = fx.NewContext();
        var c = DbFixture.NewCustomer(ownerId);
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.CustomerId;
    }

    Func<Task<int>> CountOf<T>() where T : class => async () =>
    {
        using var db = fx.NewContext();
        return await db.Set<T>().IgnoreQueryFilters().CountAsync();
    };

    async Task<Target> TargetAsync(string module, ApplicationUser user)
    {
        var n = DbFixture.Next();
        return module switch
        {
            "Customer" => new("/Customers/Create", new()
            {
                ["CustomerName"] = $"Sweep {n}", ["Email"] = $"sweep{n}@example.com", ["Phone"] = DbFixture.NextPhone(),
                ["CompanyName"] = "Co", ["Address"] = "1 Road", ["City"] = "Pune", ["State"] = "MH", ["Status"] = "Active", ["Notes"] = "",
            }, CountOf<Customer>()),
            "Lead" => new("/Leads/Create", new()
            {
                ["LeadName"] = $"Sweep {n}", ["Email"] = $"lead{n}@example.com", ["Phone"] = "", ["CompanyName"] = "Co",
                ["Status"] = "New", ["ExpectedValue"] = "100", ["Notes"] = "",
            }, CountOf<Lead>()),
            "Opportunity" => new("/Opportunities/Create", new()
            {
                ["OpportunityName"] = $"Sweep {n}", ["CustomerId"] = (await CustomerOfAsync(user.Id)).ToString(),
                ["Amount"] = "100", ["Stage"] = "Qualification", ["Probability"] = "10",
                ["ExpectedCloseDate"] = Today.AddDays(10).ToString("yyyy-MM-dd"), ["Notes"] = "",
            }, CountOf<Opportunity>()),
            "FollowUp" => new("/FollowUps/Create", new()
            {
                ["CustomerId"] = (await CustomerOfAsync(user.Id)).ToString(), ["FollowUpDate"] = Today.ToString("yyyy-MM-dd"),
                ["FollowUpType"] = "Call", ["Subject"] = $"Sweep {n}", ["Remarks"] = "",
            }, CountOf<FollowUp>()),
            "Activity" => new("/Activities/Create", new()
            {
                ["ActivityType"] = "Call", ["Subject"] = $"Sweep {n}", ["Description"] = "", ["ActivityDate"] = "2030-01-01T10:00",
                ["CustomerId"] = (await CustomerOfAsync(user.Id)).ToString(), ["Status"] = "Planned",
            }, CountOf<Activity>()),
            "User" => new("/Users/Create", new()
            {
                ["Name"] = $"Sweep {n}", ["UserName"] = $"sweep{n}", ["Email"] = $"sweepuser{n}@example.com", ["Role"] = Roles.SalesExecutive,
            }, CountOf<ApplicationUser>(), AsAdmin: true),
            "Register" => new("/Account/Register", new()
            {
                ["Name"] = $"Sweep {n}", ["Email"] = $"sweepreg{n}@example.com",
                ["Password"] = DbFixture.Password, ["ConfirmPassword"] = DbFixture.Password,
            }, CountOf<ApplicationUser>(), Anonymous: true),
            _ => throw new ArgumentException(module),
        };
    }

    async Task<(HttpResponseMessage Response, string Body, int Before, int After)> SubmitAsync(string module, Action<Dictionary<string, string>> change)
    {
        var (user, sales) = await SalesAsync();
        var target = await TargetAsync(module, user);
        var client = target.Anonymous ? fx.NewClient() : target.AsAdmin ? await AdminAsync() : sales;
        change(target.Form);

        var before = await target.Count();
        var response = await PostFormAsync(client, target.Url, target.Form);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        return (response, body, before, await target.Count());
    }

    static void AssertRejectedCleanly(HttpResponseMessage response, string body, int before, int after, string message)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // form shown again, nothing saved
        Assert.Equal(before, after);
        Assert.Contains(message, body);
        Assert.All(Internals, i => Assert.DoesNotContain(i, body));
        Assert.DoesNotMatch(DatabaseObjectName(), body);
    }

    // ---------- every form: client validation present, no framework default wording ----------

    [Fact]
    public async Task Every_form_has_client_validation_and_only_friendly_messages() // VAL-01..06, VAL-11
    {
        var (user, sales) = await SalesAsync();
        var admin = await AdminAsync();
        int qualifiedLead;
        using (var db = fx.NewContext())
        {
            var lead = new Lead { LeadName = "Sweep convert", Status = LeadStatus.Qualified, AssignedTo = user.Id, Email = $"sc{DbFixture.Next()}@example.com" };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            qualifiedLead = lead.LeadId;
        }

        var pages = new (HttpClient Client, string Url)[]
        {
            (fx.NewClient(), "/Account/Login"), (fx.NewClient(), "/Account/Register"),
            (fx.NewClient(), "/Account/ResetPassword?userId=x&token=y"), (sales, "/Account/ChangePassword"),
            (admin, "/Users/Create"), (sales, "/Customers/Create"), (sales, "/Leads/Create"),
            (sales, $"/Leads/Convert/{qualifiedLead}"), (sales, "/Opportunities/Create"), (sales, "/FollowUps/Create"),
            (sales, "/Activities/Create"),
        };
        foreach (var (client, url) in pages)
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(url));
            Assert.True(html.Contains("jquery.validate.unobtrusive"), $"{url} has no client-side validation");
            Assert.True(html.Contains("data-val-required"), $"{url} has no required-field rules");
            var defaultMessage = FrameworkDefaultMessage().Match(html);
            Assert.False(defaultMessage.Success, $"{url} shows a framework default message: {defaultMessage.Value}");
        }
    }

    // ---------- VAL-04 / VAL-12: every text field's maximum length, rejected before save ----------

    [Theory]
    [InlineData("Customer", "CustomerName", 150)]
    [InlineData("Customer", "CompanyName", 150)]
    [InlineData("Customer", "Address", 250)]
    [InlineData("Customer", "City", 100)]
    [InlineData("Customer", "State", 100)]
    [InlineData("Customer", "Notes", 2000)]
    [InlineData("Customer", "Email", 256)]
    [InlineData("Lead", "LeadName", 150)]
    [InlineData("Lead", "CompanyName", 150)]
    [InlineData("Lead", "Notes", 2000)]
    [InlineData("Lead", "Email", 256)]
    [InlineData("Opportunity", "OpportunityName", 150)]
    [InlineData("Opportunity", "Notes", 2000)]
    [InlineData("FollowUp", "Subject", 200)]
    [InlineData("FollowUp", "Remarks", 2000)]
    [InlineData("Activity", "Subject", 200)]
    [InlineData("Activity", "Description", 2000)]
    [InlineData("User", "Name", 150)]
    [InlineData("User", "UserName", 256)]
    [InlineData("User", "Email", 256)]
    [InlineData("Register", "Name", 150)]
    [InlineData("Register", "Email", 256)]
    public async Task Text_longer_than_the_limit_is_rejected_before_save(string module, string field, int max)
    {
        var (response, body, before, after) = await SubmitAsync(module, form =>
            form[field] = field.Contains("Email") ? new string('a', max) + "@example.com" : new string('x', max + 1));

        AssertRejectedCleanly(response, body, before, after, "cannot exceed");
    }

    [Theory]
    [InlineData("Customer", "CustomerName", "Customer Name is required.")]
    [InlineData("Customer", "Email", "Email is required.")]
    [InlineData("Customer", "Phone", "Phone is required.")]
    [InlineData("Lead", "LeadName", "Lead Name is required.")]
    [InlineData("Opportunity", "OpportunityName", "Opportunity Name is required.")]
    [InlineData("FollowUp", "Subject", "Subject is required.")]
    [InlineData("Activity", "Subject", "Subject is required.")]
    [InlineData("User", "Name", "Name is required.")]
    [InlineData("Register", "Name", "Name is required.")]
    public async Task Whitespace_only_required_fields_are_rejected(string module, string field, string message) // VAL-01, VAL-22
    {
        var (response, body, before, after) = await SubmitAsync(module, form => form[field] = "    ");
        AssertRejectedCleanly(response, body, before, after, message);
    }

    // ---------- VAL-06 precision and the shared email rule on the identity forms ----------

    [Theory]
    [InlineData("Opportunity", "Amount", "100.555", "Amount can have at most 2 decimal places.")]
    [InlineData("Lead", "ExpectedValue", "1.999", "Expected Value can have at most 2 decimal places.")]
    [InlineData("Opportunity", "Amount", "1e3x", "Enter a valid value for Amount.")]
    [InlineData("User", "Email", "user@nodot", "Enter a valid email address.")]
    [InlineData("Register", "Email", "user@nodot", "Enter a valid email address.")]
    [InlineData("Register", "Email", "two@@example.com", "Enter a valid email address.")]
    public async Task Numeric_precision_and_email_format_are_enforced_everywhere(string module, string field, string value, string message)
    {
        var (response, body, before, after) = await SubmitAsync(module, form => form[field] = value);
        AssertRejectedCleanly(response, body, before, after, message);
    }

    [Theory]
    [InlineData("Opportunity", "Amount", "100.5")]
    [InlineData("Opportunity", "Amount", "100.50")]
    [InlineData("Lead", "ExpectedValue", "0.01")]
    public async Task Two_decimal_places_are_accepted(string module, string field, string value)
    {
        var (response, _, before, after) = await SubmitAsync(module, form => form[field] = value);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(before + 1, after);
    }

    // ---------- AUTH-09: every state-changing POST needs the anti-forgery token ----------

    [Fact]
    public async Task Every_post_without_an_antiforgery_token_is_rejected()
    {
        var admin = await AdminAsync();
        string[] urls =
        [
            "/Account/Login", "/Account/Register", "/Account/Logout", "/Account/ChangePassword", "/Account/ResetPassword",
            "/Users/Create", "/Users/Edit/x", "/Users/SetActive/x", "/Users/Unlock/x", "/Users/ResetLink/x",
            "/Customers/Create", "/Customers/Edit/1", "/Customers/Delete/1",
            "/Leads/Create", "/Leads/Edit/1", "/Leads/Delete/1", "/Leads/Convert/1",
            "/Opportunities/Create", "/Opportunities/Edit/1", "/Opportunities/Delete/1",
            "/FollowUps/Create", "/FollowUps/Edit/1", "/FollowUps/Complete/1", "/FollowUps/Missed/1",
            "/FollowUps/Cancel/1", "/FollowUps/Reschedule/1", "/FollowUps/Delete/1",
            "/Activities/Create", "/Activities/Edit/1", "/Activities/Delete/1",
        ];
        foreach (var url in urls)
        {
            var response = await admin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" }));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{url} answered {response.StatusCode}");
        }
    }

    // ---------- VAL-10: fields that are not on a form cannot be forced in ----------

    [Fact]
    public async Task Self_registration_cannot_choose_a_role_or_account_state()
    {
        var (response, _, _, _) = await SubmitAsync("Register", form =>
        {
            form["Email"] = form["Email"].Replace("sweepreg", "overpost");
            form["Role"] = Roles.Admin;
            form["IsActive"] = "false";
            form["EmailConfirmed"] = "true";
            form["ManagerId"] = "x";
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var db = fx.NewContext();
        var user = await db.Users.OrderByDescending(u => u.CreatedDate).FirstAsync(u => u.Email!.StartsWith("overpost"));
        Assert.True(user.IsActive);
        Assert.Null(user.ManagerId);
        var roles = await db.UserRoles.Where(r => r.UserId == user.Id).Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name).ToListAsync();
        Assert.Equal([Roles.SalesExecutive], roles);
    }

    [Fact]
    public async Task Crm_records_ignore_posted_system_fields()
    {
        var lead = await SubmitAsync("Lead", form =>
        {
            form["LeadName"] = $"Overpost lead {DbFixture.Next()}";
            form["LeadCode"] = "HACK"; form["ConvertedCustomerId"] = "1"; form["IsDeleted"] = "true"; form["CreatedDate"] = "2000-01-01";
        });
        var followUp = await SubmitAsync("FollowUp", form =>
        {
            form["Subject"] = $"Overpost follow-up {DbFixture.Next()}";
            form["Status"] = "Completed"; form["IsDeleted"] = "true";
        });
        var activity = await SubmitAsync("Activity", form =>
        {
            form["Subject"] = $"Overpost activity {DbFixture.Next()}";
            form["IsDeleted"] = "true";
        });
        Assert.All(new[] { lead.Response, followUp.Response, activity.Response }, r => Assert.Equal(HttpStatusCode.Redirect, r.StatusCode));

        using var db = fx.NewContext();
        var savedLead = await db.Leads.IgnoreQueryFilters().SingleAsync(l => l.LeadId == IdOf(lead.Response));
        Assert.NotEqual("HACK", savedLead.LeadCode);
        Assert.Null(savedLead.ConvertedCustomerId);
        Assert.False(savedLead.IsDeleted);
        Assert.True(savedLead.CreatedDate > DateTime.UtcNow.AddMinutes(-5));

        var savedFollowUp = await db.FollowUps.IgnoreQueryFilters().SingleAsync(f => f.FollowUpId == IdOf(followUp.Response));
        Assert.Equal(FollowUpStatus.Planned, savedFollowUp.Status);
        Assert.False(savedFollowUp.IsDeleted);
        Assert.False((await db.Activities.IgnoreQueryFilters().SingleAsync(a => a.ActivityId == IdOf(activity.Response))).IsDeleted);
    }

    [Fact]
    public async Task Admin_user_edits_cannot_force_security_fields()
    {
        var target = await fx.CreateIdentityUserAsync();
        string hashBefore;
        using (var db = fx.NewContext()) hashBefore = (await db.Users.SingleAsync(u => u.Id == target.Id)).PasswordHash!;
        var admin = await AdminAsync();

        var response = await PostFormAsync(admin, $"/Users/Edit/{target.Id}", new()
        {
            ["Name"] = "Renamed", ["UserName"] = target.UserName!, ["Email"] = target.Email!, ["Role"] = Roles.SalesExecutive,
            ["PasswordHash"] = "forged", ["IsActive"] = "false", ["LockoutEnd"] = "2099-01-01", ["AccessFailedCount"] = "9",
        }, $"/Users/Edit/{target.Id}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var check = fx.NewContext();
        var after = await check.Users.SingleAsync(u => u.Id == target.Id);
        Assert.Equal("Renamed", after.Name);
        Assert.Equal(hashBefore, after.PasswordHash);
        Assert.True(after.IsActive);
        Assert.Null(after.LockoutEnd);
        Assert.Equal(0, after.AccessFailedCount);
    }

    static int IdOf(HttpResponseMessage response) => int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
}
