using System.ComponentModel.DataAnnotations;

namespace AcxiomCRM.ViewModels;

// Client-side mirror of the Identity password policy configured in Program.cs (§16: min 8, upper, lower, digit, symbol).
public static class PasswordRules
{
    public const string Pattern = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^a-zA-Z\d]).{8,}$";
    public const string Message = "Password must be at least 8 characters and include an upper-case letter, a lower-case letter, a digit and a symbol.";
}

public class LoginViewModel
{
    [Required(ErrorMessage = "Username or email is required.")]
    [StringLength(256, ErrorMessage = "Username or email cannot exceed 256 characters.")]
    [Display(Name = "Username or email")]
    public string Login { get; set; } = "";

    [Required(ErrorMessage = "Password is required.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(150, ErrorMessage = "Name cannot exceed 150 characters.")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Email is required.")]
    [RegularExpression(ValidationRules.EmailPattern, ErrorMessage = ValidationRules.EmailMessage)]
    [StringLength(256, ErrorMessage = "Email cannot exceed 256 characters.")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100, ErrorMessage = "Password cannot exceed 100 characters.")]
    [RegularExpression(PasswordRules.Pattern, ErrorMessage = PasswordRules.Message)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Required(ErrorMessage = "Confirm your password.")]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    public string ConfirmPassword { get; set; } = "";
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Current password is required.")]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "New password is required.")]
    [StringLength(100, ErrorMessage = "Password cannot exceed 100 characters.")]
    [RegularExpression(PasswordRules.Pattern, ErrorMessage = PasswordRules.Message)]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "Confirm your new password.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}

public class ResetPasswordViewModel
{
    [Required(ErrorMessage = ResetLinkMessage)]
    public string UserId { get; set; } = "";

    [Required(ErrorMessage = ResetLinkMessage)]
    public string Token { get; set; } = "";

    public const string ResetLinkMessage = "This reset link is invalid or has expired. Ask an administrator for a new one.";

    [Required(ErrorMessage = "New password is required.")]
    [StringLength(100, ErrorMessage = "Password cannot exceed 100 characters.")]
    [RegularExpression(PasswordRules.Pattern, ErrorMessage = PasswordRules.Message)]
    [DataType(DataType.Password)]
    [Display(Name = "New password")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "Confirm your new password.")]
    [Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    [DataType(DataType.Password)]
    [Display(Name = "Confirm new password")]
    public string ConfirmPassword { get; set; } = "";
}
