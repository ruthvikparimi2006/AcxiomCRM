using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using AcxiomCRM.Services;
using AcxiomCRM.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Controllers.Api;

// /api/customers (§10, §17.14). Out-of-scope or unknown ids answer 404.
[Route("api/customers")]
public class CustomersApiController(CustomerService customers) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResponse<CustomerDto>> List(string? search, CustomerStatus? status, int page = 1, int pageSize = 20)
    {
        var query = (await customers.VisibleAsync()).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(c => c.CustomerName.Contains(s) || c.Email.Contains(s) || c.Phone.Contains(s)
                                     || (c.CompanyName != null && c.CompanyName.Contains(s)));
        }
        if (status is not null) query = query.Where(c => c.Status == status);
        return await PageAsync(query.OrderBy(c => c.CustomerName).ThenBy(c => c.CustomerId).ToDto(), page, pageSize);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CustomerDto>> Get(int id) =>
        await DtoAsync(id) is { } dto ? dto : NotFound();

    [HttpPost]
    public async Task<ActionResult<CustomerDto>> Create(CustomerInput input)
    {
        var (customer, errors) = await customers.CreateAsync(input);
        if (errors.Count > 0) return Errors(errors);
        return CreatedAtAction(nameof(Get), new { id = customer!.CustomerId }, await DtoAsync(customer.CustomerId));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CustomerDto>> Update(int id, CustomerInput input)
    {
        var customer = await customers.FindAsync(id);
        if (customer is null) return NotFound();
        var errors = await customers.UpdateAsync(customer, input);
        if (errors.Count > 0) return Errors(errors);
        return (await DtoAsync(id))!;
    }

    // §16 #8: delete marks the customer Inactive; the updated record is returned.
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<CustomerDto>> Delete(int id)
    {
        var customer = await customers.FindAsync(id);
        if (customer is null) return NotFound();
        await customers.DeactivateAsync(customer);
        return (await DtoAsync(id))!;
    }

    async Task<CustomerDto?> DtoAsync(int id) =>
        await (await customers.VisibleAsync()).AsNoTracking().Where(c => c.CustomerId == id).ToDto().FirstOrDefaultAsync();
}
