using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// The fields a user may submit for a customer (CUS-02, CUS-03). Code, creator and dates are never bound.
public class CustomerInput
{
    [Required(ErrorMessage = "Customer Name is required.")]
    [StringLength(150, ErrorMessage = "Customer Name cannot exceed 150 characters.")]
    [Display(Name = "Customer Name")]
    public string CustomerName { get; set; } = "";

    [Required(ErrorMessage = "Email is required.")]
    [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters.")]
    [RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Phone is required.")]
    [RegularExpression(ValidationRules.PhonePattern, ErrorMessage = ValidationRules.PhoneMessage)]
    public string Phone { get; set; } = "";

    [StringLength(150, ErrorMessage = "Company cannot exceed 150 characters.")]
    [Display(Name = "Company")]
    public string? CompanyName { get; set; }

    [StringLength(250, ErrorMessage = "Address cannot exceed 250 characters.")]
    public string? Address { get; set; }

    [StringLength(100, ErrorMessage = "City cannot exceed 100 characters.")]
    public string? City { get; set; }

    [StringLength(100, ErrorMessage = "State cannot exceed 100 characters.")]
    public string? State { get; set; }

    [Required(ErrorMessage = "Status is required.")]
    [EnumDataType(typeof(CustomerStatus), ErrorMessage = "Select a valid status.")]
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    [StringLength(2000, ErrorMessage = "Notes cannot exceed 2000 characters.")]
    public string? Notes { get; set; }

    // Empty means "me". Checked against ScopeService.AssignableUsersAsync (D6).
    [Display(Name = "Owner")]
    public string? OwnerId { get; set; }
}

public class CustomerFormViewModel : CustomerInput
{
    public List<SelectListItem> Owners { get; set; } = [];
}

public record CustomerListItem(int Id, string Code, string Name, string? Company, string Email, string Phone,
    string OwnerName, CustomerStatus Status);

public class CustomerIndexViewModel
{
    public string? Search { get; set; }
    public CustomerStatus? Status { get; set; }
    public PagedList<CustomerListItem> Customers { get; set; } = new();
}

public record HistoryEntry(DateTime When, string? UserName, string Action, string? Changes);

public record RelatedActivity(int Id, ActivityType Type, string Subject, DateTime Date, ActivityStatus Status);

public class CustomerDetailsViewModel
{
    public Customer Customer { get; set; } = null!;
    public string? OwnerName { get; set; }
    public string? CreatedByName { get; set; }
    public List<HistoryEntry> History { get; set; } = [];
    public List<RelatedActivity> Activities { get; set; } = [];
}
