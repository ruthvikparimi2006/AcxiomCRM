using System.Net;
using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;
using static AcxiomCRM.Tests.TestHttp;

namespace AcxiomCRM.Tests;

// Step 5: CUS-01..08 with VAL rules, D6 ownership rules, scope and audit.
[Collection("db")]
public class CustomerTests(DbFixture fx)
{
    static Dictionary<string, string> Form(string? name = null, string? email = null, string? phone = null,
        string? ownerId = null, string status = "Active", string? company = null) => new()
    {
        ["CustomerName"] = name ?? $"Customer {DbFixture.Next()}",
        ["Email"] = email ?? $"cust{DbFixture.Next()}@example.com",
        ["Phone"] = phone ?? DbFixture.NextPhone(),
        ["CompanyName"] = company ?? "Acme Pvt Ltd",
        ["City"] = "Pune",
        ["Status"] = status,
        ["OwnerId"] = ownerId ?? "",
    };

    async Task<(ApplicationUser User, HttpClient Client)> UserAsync(string role = Roles.SalesExecutive, string? managerId = null)
    {
        var user = await fx.CreateIdentityUserAsync(role, managerId: managerId);
        return (user, await SignedInAsync(fx, user.UserName!, DbFixture.Password));
    }

    static async Task<int> CreateAsync(HttpClient client, Dictionary<string, string> form)
    {
        var response = await PostFormAsync(client, "/Customers/Create", form);
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, await response.Content.ReadAsStringAsync());
        return int.Parse(response.Headers.Location!.OriginalString.Split('/').Last());
    }

    async Task<Customer> LoadAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.Customers.SingleAsync(c => c.CustomerId == id);
    }

    async Task<List<AuditLog>> AuditAsync(int id)
    {
        using var db = fx.NewContext();
        return await db.AuditLogs.Where(a => a.EntityName == "Customer" && a.RecordId == id.ToString()).ToListAsync();
    }

    [Fact]
    public async Task Creating_a_customer_saves_it_owned_by_the_creator_and_audits_it()
    {
        var (me, client) = await UserAsync();
        var id = await CreateAsync(client, Form(name: "Sharma Traders"));

        var customer = await LoadAsync(id);
        Assert.Equal(me.Id, customer.OwnerId);
        Assert.Equal(me.Id, customer.CreatedBy);
        Assert.Matches(@"^CUS-\d{6}$", customer.CustomerCode);
        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Create" && a.UserId == me.Id);

        var details = await client.GetStringAsync($"/Customers/Details/{id}");
        Assert.Contains("Sharma Traders", details);
        Assert.Contains(customer.CustomerCode, details);
    }

    [Fact]
    public async Task Create_form_carries_client_side_validation_rules() // §17.19 #3, VAL-01..04
    {
        var (_, client) = await UserAsync();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync("/Customers/Create"));

        Assert.Contains("data-val-required=\"Customer Name is required.\"", html);
        Assert.Contains("data-val-length-max=\"150\"", html);
        Assert.Contains("data-val-regex=\"Enter a valid email address.\"", html);
        Assert.Contains("data-val-regex=\"Enter a valid phone number.\"", html);
        Assert.Contains(@"data-val-regex-pattern=""^[6-9]\d{9}$""", html);
        Assert.Contains("jquery.validate.unobtrusive", html);
    }

    [Theory] // §17.19 #4, VAL-07, VAL-12, §5.4 messages
    [InlineData("Email", "not-an-email", "Enter a valid email address.")]
    [InlineData("Email", "user@nodot", "Enter a valid email address.")]
    [InlineData("Phone", "12345", "Enter a valid phone number.")]
    [InlineData("Phone", "5123456789", "Enter a valid phone number.")]
    [InlineData("Phone", "98765abcde", "Enter a valid phone number.")]
    [InlineData("CustomerName", "", "Customer Name is required.")]
    [InlineData("CustomerName", "LONG", "Customer Name cannot exceed 150 characters.")]
    [InlineData("Notes", "LONG", "Notes cannot exceed 2000 characters.")]
    [InlineData("Status", "Bogus", "Enter a valid value for Status.")]
    [InlineData("Status", "7", "Enter a valid value.")]
    public async Task Crafted_invalid_requests_are_rejected_by_the_server(string field, string value, string message)
    {
        var (_, client) = await UserAsync();
        var form = Form();
        form[field] = value == "LONG" ? new string('x', field == "Notes" ? 2001 : 151) : value;
        var marker = form["Phone"] == value ? form["Email"] : form["Phone"];

        var response = await PostFormAsync(client, "/Customers/Create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(message, body);
        using var db = fx.NewContext();
        Assert.False(await db.Customers.AnyAsync(c => c.Phone == marker || c.Email == marker));
    }

    [Fact]
    public async Task Duplicate_email_ignoring_case_or_phone_is_rejected_even_outside_my_scope() // VAL-17..19
    {
        var (_, other) = await UserAsync();
        var email = $"dupe{DbFixture.Next()}@example.com";
        var phone = DbFixture.NextPhone();
        var existing = await CreateAsync(other, Form(email: email, phone: phone));
        await PostFormAsync(other, $"/Customers/Delete/{existing}", new(), $"/Customers/Details/{existing}"); // inactive still counts

        var (_, me) = await UserAsync();
        var byEmail = await PostFormAsync(me, "/Customers/Create", Form(email: email.ToUpperInvariant()));
        Assert.Contains("A customer with this email already exists.", await byEmail.Content.ReadAsStringAsync());
        var byPhone = await PostFormAsync(me, "/Customers/Create", Form(phone: phone));
        Assert.Contains("A customer with this phone number already exists.", await byPhone.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SalesExecutive_cannot_assign_a_customer_to_someone_else() // D6, VAL-10
    {
        var other = await fx.CreateIdentityUserAsync();
        var (_, client) = await UserAsync();
        var form = Form(ownerId: other.Id);

        var response = await PostFormAsync(client, "/Customers/Create", form);

        Assert.Contains("You cannot assign customers to this user.", await response.Content.ReadAsStringAsync());
        using var db = fx.NewContext();
        Assert.False(await db.Customers.AnyAsync(c => c.Email == form["Email"]));
    }

    [Fact]
    public async Task Manager_assigns_within_team_and_Admin_to_anyone() // D6
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var member = await fx.CreateIdentityUserAsync(managerId: manager.Id);
        var outsider = await fx.CreateIdentityUserAsync();

        var teamCustomer = await CreateAsync(managerClient, Form(ownerId: member.Id));
        Assert.Equal(member.Id, (await LoadAsync(teamCustomer)).OwnerId);

        var refused = await PostFormAsync(managerClient, "/Customers/Create", Form(ownerId: outsider.Id));
        Assert.Contains("You cannot assign customers to this user.", await refused.Content.ReadAsStringAsync());

        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
        var adminCustomer = await CreateAsync(admin, Form(ownerId: outsider.Id));
        Assert.Equal(outsider.Id, (await LoadAsync(adminCustomer)).OwnerId);
    }

    [Fact]
    public async Task SalesExecutive_cannot_see_or_change_another_users_customer() // RBAC-03/04, CUS-08
    {
        var (_, owner) = await UserAsync();
        var form = Form(name: $"Private {DbFixture.Next()}");
        var id = await CreateAsync(owner, form);
        var (_, intruder) = await UserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/Customers/Details/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await intruder.GetAsync($"/Customers/Edit/{id}")).StatusCode);
        Assert.Contains("No customers found.", await intruder.GetStringAsync($"/Customers?search={form["CustomerName"]}"));

        var edit = form.ToDictionary(); edit["CustomerName"] = "Hijacked";
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(intruder, $"/Customers/Edit/{id}", edit, "/Customers")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(intruder, $"/Customers/Delete/{id}", new(), "/Customers")).StatusCode);

        var after = await LoadAsync(id);
        Assert.Equal(form["CustomerName"], after.CustomerName);
        Assert.Equal(CustomerStatus.Active, after.Status);
    }

    [Fact]
    public async Task Manager_sees_team_customers_but_not_others() // CUS-08
    {
        var (manager, managerClient) = await UserAsync(Roles.Manager);
        var (_, memberClient) = await UserAsync(managerId: manager.Id);
        var (_, outsiderClient) = await UserAsync();
        var teamId = await CreateAsync(memberClient, Form());
        var otherId = await CreateAsync(outsiderClient, Form());

        Assert.Equal(HttpStatusCode.OK, (await managerClient.GetAsync($"/Customers/Details/{teamId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerClient.GetAsync($"/Customers/Details/{otherId}")).StatusCode);

        var admin = await SignedInAsync(fx, DbFixture.AdminEmail, DbFixture.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/Customers/Details/{otherId}")).StatusCode);
    }

    [Fact]
    public async Task Edits_and_status_changes_are_audited_and_shown_in_history() // CUS-06, CUS-07
    {
        var (_, client) = await UserAsync();
        var form = Form(name: "Before Name");
        var id = await CreateAsync(client, form);

        form["CustomerName"] = "After Name";
        form["Status"] = "Inactive";
        var response = await PostFormAsync(client, $"/Customers/Edit/{id}", form, $"/Customers/Edit/{id}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var audit = await AuditAsync(id);
        var update = Assert.Single(audit, a => a.Action == "Update");
        Assert.Contains("Before Name", update.OldValue);
        Assert.Contains("After Name", update.NewValue);
        Assert.Single(audit, a => a.Action == "StatusChanged");

        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/Customers/Details/{id}"));
        Assert.Contains("CustomerName: Before Name → After Name", details);
        Assert.Contains("Status: Active → Inactive", details);
    }

    [Fact]
    public async Task Delete_marks_the_customer_inactive_and_is_audited() // §16 #8
    {
        var (_, client) = await UserAsync();
        var form = Form();
        var id = await CreateAsync(client, form);

        var response = await PostFormAsync(client, $"/Customers/Delete/{id}", new(), $"/Customers/Details/{id}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(CustomerStatus.Inactive, (await LoadAsync(id)).Status);
        Assert.Contains(await AuditAsync(id), a => a.Action == "Deactivate");
        Assert.Contains(form["CustomerName"], await client.GetStringAsync($"/Customers?status=Inactive&search={form["Email"]}"));
    }

    [Theory] // CUS-05
    [InlineData("CustomerName")]
    [InlineData("Email")]
    [InlineData("Phone")]
    [InlineData("CompanyName")]
    public async Task Search_finds_customers_by_name_email_phone_or_company(string field)
    {
        var (_, client) = await UserAsync();
        var tag = $"Find{DbFixture.Next()}";
        var form = Form(name: $"{tag} Name", company: $"{tag} Corp", email: $"{tag.ToLowerInvariant()}@example.com");
        await CreateAsync(client, form);

        var html = await client.GetStringAsync($"/Customers?search={Uri.EscapeDataString(form[field])}");
        Assert.Contains($"{tag} Name", html);
        Assert.Contains("Showing 1–1 of 1", html);
    }

    [Fact]
    public async Task Fields_outside_the_form_cannot_be_over_posted()
    {
        var (me, client) = await UserAsync();
        var other = await fx.CreateIdentityUserAsync();
        var form = Form();
        form["CustomerCode"] = "HACKED";
        form["CreatedBy"] = other.Id;
        form["CustomerId"] = "1";

        var id = await CreateAsync(client, form);

        var customer = await LoadAsync(id);
        Assert.NotEqual("HACKED", customer.CustomerCode);
        Assert.Equal(me.Id, customer.CreatedBy);
    }
}
