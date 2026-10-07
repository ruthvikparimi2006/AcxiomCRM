using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AcxiomCRM.Controllers.Api;

// REST API conventions (API-01..07):
// - JWT bearer authentication only: under /api the cookie is never read (see the "WebOrApi" scheme in Program.cs),
//   so cross-site request forgery does not apply and the anti-forgery check is skipped.
// - [ApiController] answers invalid payloads with 400 before an action runs (API-03).
// - Every error is an RFC 7807 problem object with no stack trace or database detail (API-06).
// - The same services as the web pages apply scope, business rules and auditing.
[ApiController]
[IgnoreAntiforgeryToken]
[Authorize(Policy = Policies.CrmUser)]
public abstract class ApiControllerBase : ControllerBase
{
    public const int MaxPageSize = 100;

    // 409 when the data clashes with an existing record (duplicate), otherwise 400.
    protected ActionResult Errors(List<FieldError> errors)
    {
        ModelState.AddErrors(errors);
        return errors.Any(e => e.Conflict)
            ? ValidationProblem(title: "The request conflicts with an existing record.", statusCode: StatusCodes.Status409Conflict, modelStateDictionary: ModelState)
            : ValidationProblem(ModelState);
    }

    protected static async Task<PagedResponse<T>> PageAsync<T>(IQueryable<T> query, int page, int pageSize)
    {
        var paged = await PagedList<T>.CreateAsync(query, page, Math.Clamp(pageSize, 1, MaxPageSize));
        return new PagedResponse<T>(paged.Items, paged.Page, paged.PageSize, paged.TotalCount);
    }
}
