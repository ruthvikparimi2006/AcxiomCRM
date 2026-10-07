using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers.Api;

// The remaining §10 endpoints: list and create for leads, opportunities and follow-ups, and the pipeline report.
// §10 defines no single-record GET for these, so a create answers 201 with the new record in the body.

[Route("api/leads")]
public class LeadsApiController(LeadService leads) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResponse<LeadDto>> List(string? search, LeadStatus? status, string? assignedTo, int page = 1, int pageSize = 20)
    {
        var query = (await leads.VisibleAsync()).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(l => l.LeadName.Contains(search.Trim()) || (l.CompanyName != null && l.CompanyName.Contains(search.Trim())));
        if (status is not null) query = query.Where(l => l.Status == status);
        if (!string.IsNullOrEmpty(assignedTo)) query = query.Where(l => l.AssignedTo == assignedTo);
        return await PageAsync(query.OrderBy(l => l.LeadName).ThenBy(l => l.LeadId).ToDto(), page, pageSize);
    }

    [HttpPost]
    public async Task<ActionResult<LeadDto>> Create(LeadInput input)
    {
        var (lead, errors) = await leads.CreateAsync(input);
        if (errors.Count > 0) return Errors(errors);
        return StatusCode(StatusCodes.Status201Created,
            await (await leads.VisibleAsync()).AsNoTracking().Where(l => l.LeadId == lead!.LeadId).ToDto().FirstAsync());
    }
}

[Route("api/opportunities")]
public class OpportunitiesApiController(OpportunityService opportunities) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResponse<OpportunityDto>> List(string? search, string? customer, OpportunityStage? stage,
        OpportunityStatus? status, int page = 1, int pageSize = 20)
    {
        var query = (await opportunities.VisibleAsync()).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(o => o.OpportunityName.Contains(search.Trim()));
        if (!string.IsNullOrWhiteSpace(customer)) query = query.Where(o => o.Customer!.CustomerName.Contains(customer.Trim()));
        if (stage is not null) query = query.Where(o => o.Stage == stage);
        if (status is not null) query = query.Where(o => o.Status == status);
        return await PageAsync(query.OrderBy(o => o.ExpectedCloseDate).ThenBy(o => o.OpportunityId).ToDto(), page, pageSize);
    }

    [HttpPost]
    public async Task<ActionResult<OpportunityDto>> Create(OpportunityInput input)
    {
        var (opportunity, errors) = await opportunities.CreateAsync(input);
        if (errors.Count > 0) return Errors(errors);
        return StatusCode(StatusCodes.Status201Created,
            await (await opportunities.VisibleAsync()).AsNoTracking().Where(o => o.OpportunityId == opportunity!.OpportunityId).ToDto().FirstAsync());
    }
}

[Route("api/followups")]
public class FollowUpsApiController(FollowUpService followUps) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResponse<FollowUpDto>> List(DateOnly? from, DateOnly? to, FollowUpStatus? status, int page = 1, int pageSize = 20)
    {
        var query = (await followUps.VisibleAsync()).AsNoTracking();
        if (from is not null) query = query.Where(f => f.FollowUpDate >= from);
        if (to is not null) query = query.Where(f => f.FollowUpDate <= to);
        if (status is not null) query = query.Where(f => f.Status == status);
        return await PageAsync(query.OrderBy(f => f.FollowUpDate).ThenBy(f => f.FollowUpId).ToDto(), page, pageSize);
    }

    [HttpPost]
    public async Task<ActionResult<FollowUpDto>> Create(FollowUpInput input)
    {
        var (followUp, errors) = await followUps.CreateAsync(input);
        if (errors.Count > 0) return Errors(errors);
        return StatusCode(StatusCodes.Status201Created,
            await (await followUps.VisibleAsync()).AsNoTracking().Where(f => f.FollowUpId == followUp!.FollowUpId).ToDto().FirstAsync());
    }
}

// D9: every role may call it; ScopeService limits the data to own (SalesExecutive), team (Manager) or all (Admin).
[Route("api/reports")]
public class ReportsApiController(OpportunityService opportunities) : ApiControllerBase
{
    [HttpGet("pipeline")]
    public async Task<PipelineReportDto> Pipeline()
    {
        var totals = await (await opportunities.VisibleAsync()).AsNoTracking()
            .GroupBy(o => o.Stage)
            .Select(g => new { Stage = g.Key, Count = g.Count(), Amount = g.Sum(o => o.Amount), Weighted = g.Sum(o => o.Amount * o.Probability / 100m) })
            .ToListAsync();

        var stages = Enum.GetValues<OpportunityStage>().Select(stage =>
            totals.FirstOrDefault(t => t.Stage == stage) is { } t
                ? new PipelineStageDto(stage, t.Count, t.Amount, t.Weighted)
                : new PipelineStageDto(stage, 0, 0, 0)).ToList();
        var open = stages.Where(s => OpportunityService.IsOpen(s.Stage)).ToList();
        var scope = User.IsInRole(Roles.Admin) ? "all" : User.IsInRole(Roles.Manager) ? "team" : "own";

        return new PipelineReportDto(scope, stages, open.Sum(s => s.Count), open.Sum(s => s.Amount), open.Sum(s => s.WeightedAmount));
    }
}
