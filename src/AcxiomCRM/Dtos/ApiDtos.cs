using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;

namespace AcxiomCRM.Dtos;

// API-02 / API-07: what the REST API returns. Never Entity Framework entities, never password hashes,
// security stamps, tokens or internal flags such as IsDeleted. Requests reuse the validated *Input models.

public class LoginRequest
{
    [Required(ErrorMessage = "Username or email is required.")]
    [StringLength(256, ErrorMessage = "Username or email cannot exceed 256 characters.")]
    public string Login { get; set; } = "";

    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100, ErrorMessage = "Password cannot exceed 100 characters.")]
    public string Password { get; set; } = "";
}

public record ApiUserDto(string Id, string Name, string? Role);

public record LoginResponse(string Token, DateTime ExpiresAt, ApiUserDto User);

public record PagedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount);

public record CustomerDto(int Id, string Code, string Name, string Email, string Phone, string? Company, string? Address,
    string? City, string? State, CustomerStatus Status, string? Notes, string OwnerId, string? OwnerName,
    DateTime CreatedDate, DateTime? ModifiedDate);

public record LeadDto(int Id, string Code, string Name, string? Email, string? Phone, string? Company, LeadSource? Source,
    LeadStatus Status, LeadPriority? Priority, decimal? ExpectedValue, string? Notes, string AssignedTo, string? AssignedName,
    int? ConvertedCustomerId, DateTime CreatedDate);

public record OpportunityDto(int Id, string Name, int CustomerId, string? CustomerName, int? LeadId, decimal Amount,
    decimal WeightedAmount, OpportunityStage Stage, OpportunityStatus Status, int Probability, DateOnly ExpectedCloseDate,
    LeadSource? Source, string? Notes, string AssignedTo, string? AssignedName, DateTime? ClosedDate);

public record FollowUpDto(int Id, int? CustomerId, int? LeadId, int? OpportunityId, string? RelatedName, DateOnly FollowUpDate,
    ActivityType Type, string Subject, string? Remarks, FollowUpStatus Status, string AssignedTo, string? AssignedName);

public record PipelineStageDto(OpportunityStage Stage, int Count, decimal Amount, decimal WeightedAmount);

// D9: Scope is "all" (Admin), "team" (Manager) or "own" (SalesExecutive).
public record PipelineReportDto(string Scope, List<PipelineStageDto> Stages, int OpenCount, decimal OpenAmount, decimal OpenWeightedAmount);

// Database projections, so only these fields are ever read for the API.
public static class DtoProjections
{
    public static IQueryable<CustomerDto> ToDto(this IQueryable<Customer> q) => q.Select(c => new CustomerDto(
        c.CustomerId, c.CustomerCode, c.CustomerName, c.Email, c.Phone, c.CompanyName, c.Address, c.City, c.State, c.Status,
        c.Notes, c.OwnerId, c.Owner!.Name, c.CreatedDate, c.ModifiedDate));

    public static IQueryable<LeadDto> ToDto(this IQueryable<Lead> q) => q.Select(l => new LeadDto(
        l.LeadId, l.LeadCode, l.LeadName, l.Email, l.Phone, l.CompanyName, l.Source, l.Status, l.Priority, l.ExpectedValue,
        l.Notes, l.AssignedTo, l.AssignedUser!.Name, l.ConvertedCustomerId, l.CreatedDate));

    public static IQueryable<OpportunityDto> ToDto(this IQueryable<Opportunity> q) => q.Select(o => new OpportunityDto(
        o.OpportunityId, o.OpportunityName, o.CustomerId, o.Customer!.CustomerName, o.LeadId, o.Amount,
        o.Amount * o.Probability / 100m, o.Stage, o.Status, o.Probability, o.ExpectedCloseDate, o.Source, o.Notes,
        o.AssignedTo, o.AssignedUser!.Name, o.ClosedDate));

    public static IQueryable<FollowUpDto> ToDto(this IQueryable<FollowUp> q) => q.Select(f => new FollowUpDto(
        f.FollowUpId, f.CustomerId, f.LeadId, f.OpportunityId,
        f.Customer != null ? f.Customer.CustomerName : f.Lead != null ? f.Lead.LeadName : f.Opportunity != null ? f.Opportunity.OpportunityName : null,
        f.FollowUpDate, f.FollowUpType, f.Subject, f.Remarks, f.Status, f.AssignedTo, f.AssignedUser!.Name));
}
