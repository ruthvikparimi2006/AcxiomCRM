using AcxiomCRM.Data;
using AcxiomCRM.Models;
using AcxiomCRM.ViewModels;

namespace AcxiomCRM.Services;

// LEAD-06 / §16 #7: a Qualified lead becomes a customer (new, or an existing one with the same email/phone)
// and, optionally, an opportunity. Everything happens in one transaction and is audited.
public class LeadConversionService(
    AppDbContext db, AuditService audit, LeadService leads, CustomerService customers, OpportunityService opportunities)
{
    public record Result(int? CustomerId, int? OpportunityId, List<FieldError> Errors);

    public record Match(Customer? Customer, bool OutsideScope);

    // The existing customer this lead would be linked to. A match the user can't see blocks conversion
    // (it is not linked or revealed); an Admin, who sees everything, can convert such a lead.
    public async Task<Match> FindMatchAsync(Lead lead) =>
        await leads.FindMatchingCustomerAsync(lead) is not { } match ? new(null, false)
        : await customers.FindAsync(match.CustomerId) is { } visible ? new(visible, false)
        : new(null, true);

    public async Task<Result> ConvertAsync(Lead lead, ConvertLeadViewModel input)
    {
        if (!LeadService.CanConvert(lead))
            return Fail("", "Only qualified leads can be converted.");

        var match = await FindMatchAsync(lead);
        if (match.OutsideScope)
            return Fail("", "A customer with this lead's email or phone already exists outside your scope. Ask an administrator to convert this lead.");

        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = match.Customer;
        if (customer is null)
        {
            input.Customer.OwnerId = lead.AssignedTo;
            input.Customer.Status = CustomerStatus.Active;
            var (created, errors) = await customers.CreateAsync(input.Customer);
            if (errors.Count > 0) return new(null, null, Prefixed("Customer", errors));
            customer = created!;
        }

        Opportunity? opportunity = null;
        if (input.CreateOpportunity)
        {
            input.Opportunity.CustomerId = customer.CustomerId;
            input.Opportunity.LeadId = lead.LeadId;
            input.Opportunity.AssignedTo = lead.AssignedTo;
            input.Opportunity.Source ??= lead.Source;
            var (created, errors) = await opportunities.CreateAsync(input.Opportunity);
            if (errors.Count > 0) return new(null, null, Prefixed("Opportunity", errors));
            opportunity = created;
        }

        var oldStatus = lead.Status;
        lead.Status = LeadStatus.Converted;
        lead.ConvertedCustomerId = customer.CustomerId;
        lead.ModifiedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await audit.LogAsync("Leads", "Converted", nameof(Lead), lead.LeadId.ToString(),
            new { Status = oldStatus },
            new
            {
                lead.Status, CustomerId = customer.CustomerId, OpportunityId = opportunity?.OpportunityId,
                LinkedExistingCustomer = match.Customer is not null,
            });

        await tx.CommitAsync();
        return new(customer.CustomerId, opportunity?.OpportunityId, []);
    }

    static Result Fail(string field, string message) => new(null, null, [new(field, message)]);

    // Errors on fields that are not on the conversion form (owner, links) are shown at the top of the form.
    static List<FieldError> Prefixed(string prefix, List<FieldError> errors) =>
        errors.Select(e => e.Field is "OwnerId" or "AssignedTo" or "CustomerId" or "LeadId"
            ? new FieldError("", e.Message)
            : new FieldError($"{prefix}.{e.Field}", e.Message)).ToList();
}
