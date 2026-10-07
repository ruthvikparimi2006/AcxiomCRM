using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace AcxiomCRM.Models;

// Identity user extended with the §9 user fields. Passwords, lockout and roles stay with Identity.
public class ApplicationUser : IdentityUser
{
    [MaxLength(150)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // §16 #1: a SalesExecutive's Manager. Team = the Manager plus users pointing at them.
    public string? ManagerId { get; set; }
    public ApplicationUser? Manager { get; set; }
}
