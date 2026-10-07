namespace AcxiomCRM.ViewModels;

// Shared input rules and the §5.4 messages. Used by view models (client + server) and, later, API DTOs.
public static class ValidationRules
{
    public const string EmailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
    public const string EmailMessage = "Enter a valid email address.";

    // §16: 10-digit Indian mobile number.
    public const string PhonePattern = @"^[6-9]\d{9}$";
    public const string PhoneMessage = "Enter a valid phone number.";

    // VAL-06 precision: money is stored as decimal(18,2), so more than 2 decimals would be silently rounded.
    // Sign is left to the Range rules so a negative amount gets the "cannot be negative" message.
    public const string MoneyPattern = @"^-?\d+(\.\d{1,2})?$";
    public const string MoneyMessage = "{0} can have at most 2 decimal places.";

    // Standard wording for values the model binder cannot convert (VAL-11): no framework or type details.
    public const string InvalidValueMessage = "Enter a valid value for {0}.";
}
