using AcxiomCRM.Models;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

// Step 1: the schema enforces the §16 constraints on its own.
[Collection("db")]
public class DatabaseTests(DbFixture fx)
{
    async Task AssertRejected(object entity)
    {
        using var db = fx.NewContext();
        db.Add(entity);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void All_migrations_are_applied()
    {
        using var db = fx.NewContext();
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [Fact]
    public async Task Customer_and_lead_codes_are_generated()
    {
        var customer = await fx.AddCustomerAsync();
        Assert.Matches(@"^CUS-\d{6}$", customer.CustomerCode);

        var user = await fx.AddUserAsync();
        using var db = fx.NewContext();
        var lead = new Lead { LeadName = "Lead", AssignedTo = user.Id };
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        Assert.Matches(@"^LEAD-\d{6}$", lead.LeadCode);
    }

    [Fact]
    public async Task Customer_email_is_unique_ignoring_case()
    {
        var existing = await fx.AddCustomerAsync(email: $"dup{DbFixture.Next()}@example.com");
        await AssertRejected(DbFixture.NewCustomer(existing.OwnerId, email: existing.Email.ToUpperInvariant()));
    }

    [Fact]
    public async Task Customer_phone_is_unique_including_inactive()
    {
        var existing = await fx.AddCustomerAsync();
        using (var db = fx.NewContext())
        {
            db.Attach(existing).Entity.Status = CustomerStatus.Inactive;
            await db.SaveChangesAsync();
        }
        await AssertRejected(DbFixture.NewCustomer(existing.OwnerId, phone: existing.Phone));
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(100, 101)]
    [InlineData(100, -1)]
    public async Task Opportunity_amount_and_probability_ranges_are_enforced(decimal amount, int probability)
    {
        var customer = await fx.AddCustomerAsync();
        await AssertRejected(new Opportunity
        {
            OpportunityName = "Deal", CustomerId = customer.CustomerId, AssignedTo = customer.OwnerId,
            Amount = amount, Probability = probability, ExpectedCloseDate = DateOnly.FromDateTime(DateTime.Today),
        });
    }

    [Fact]
    public async Task Lead_expected_value_range_is_enforced()
    {
        var user = await fx.AddUserAsync();
        await AssertRejected(new Lead { LeadName = "Big", AssignedTo = user.Id, ExpectedValue = 100_000_001m });
    }

    [Fact]
    public async Task FollowUp_needs_exactly_one_related_record()
    {
        var customer = await fx.AddCustomerAsync();
        int leadId;
        using (var db = fx.NewContext())
        {
            var lead = new Lead { LeadName = "Lead", AssignedTo = customer.OwnerId };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
            leadId = lead.LeadId;
        }
        FollowUp Make(int? customerId, int? lead) => new()
        {
            CustomerId = customerId, LeadId = lead, Subject = "Call back", AssignedTo = customer.OwnerId,
            FollowUpDate = DateOnly.FromDateTime(DateTime.Today),
        };

        await AssertRejected(Make(null, null));
        await AssertRejected(Make(customer.CustomerId, leadId));

        using var ok = fx.NewContext();
        ok.FollowUps.Add(Make(customer.CustomerId, null));
        await ok.SaveChangesAsync();
    }

    [Fact]
    public async Task Activity_needs_a_related_record()
    {
        var user = await fx.AddUserAsync();
        await AssertRejected(new Activity { Subject = "Call", AssignedTo = user.Id, ActivityDate = DateTime.UtcNow });
    }

    [Fact]
    public async Task Customer_with_opportunities_cannot_be_deleted()
    {
        var customer = await fx.AddCustomerAsync();
        using (var db = fx.NewContext())
        {
            db.Opportunities.Add(new Opportunity
            {
                OpportunityName = "Deal", CustomerId = customer.CustomerId, AssignedTo = customer.OwnerId,
                Amount = 10, Probability = 50, ExpectedCloseDate = DateOnly.FromDateTime(DateTime.Today),
            });
            await db.SaveChangesAsync();
        }

        using var del = fx.NewContext();
        del.Customers.Remove(del.Customers.Single(c => c.CustomerId == customer.CustomerId));
        await Assert.ThrowsAsync<DbUpdateException>(() => del.SaveChangesAsync());
    }

    [Fact]
    public async Task Soft_deleted_leads_are_hidden_from_queries()
    {
        var user = await fx.AddUserAsync();
        using var db = fx.NewContext();
        var lead = new Lead { LeadName = "Gone", AssignedTo = user.Id, IsDeleted = true };
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        using var read = fx.NewContext();
        Assert.False(await read.Leads.AnyAsync(l => l.LeadId == lead.LeadId));
        Assert.True(await read.Leads.IgnoreQueryFilters().AnyAsync(l => l.LeadId == lead.LeadId));
    }
}
