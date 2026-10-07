using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AcxiomCRM.ViewModels;

// None of these carry password hashes, security stamps or tokens (USR-07).

public record UserListItem(string Id, string Name, string UserName, string Email, string? Role, bool IsActive, bool IsLockedOut);

public class UserIndexViewModel
{
    public string? Search { get; set; }
    public string? Role { get; set; }
    public bool? Active { get; set; }
    public PagedList<UserListItem> Users { get; set; } = new();
    public bool CanManage { get; set; }
}

public class UserDetailsViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Role { get; set; }
    public string? ManagerName { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public DateTime CreatedDate { get; set; }
    public bool CanManage { get; set; }
    public bool IsLockedOut => LockoutEnd > DateTimeOffset.UtcNow;

    // Shown once, right after an Admin generates it; never stored.
    public string? ResetLink { get; set; }
}

public class UserFormViewModel
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(150, ErrorMessage = "Name cannot exceed 150 characters.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Username is required.")]
    [StringLength(256, ErrorMessage = "Username cannot exceed 256 characters.")]
    [Display(Name = "Username")]
    public string UserName { get; set; } = "";

    [Required(ErrorMessage = "Email is required.")]
    [RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Role is required.")]
    public string Role { get; set; } = "";

    [Display(Name = "Manager")]
    public string? ManagerId { get; set; }

    public List<SelectListItem> Managers { get; set; } = [];
}
