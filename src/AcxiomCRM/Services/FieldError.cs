using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AcxiomCRM.Services;

// A business-rule failure from a service, tied to the input field it concerns.
// Conflict marks a clash with existing data (e.g. a duplicate), which the API answers with 409.
public record FieldError(string Field, string Message, bool Conflict = false);

public static class FieldErrorExtensions
{
    // Adds the errors to ModelState; true when there were any.
    public static bool AddErrors(this ModelStateDictionary modelState, IReadOnlyCollection<FieldError> errors)
    {
        foreach (var e in errors)
            modelState.AddModelError(e.Field, e.Message);
        return errors.Count > 0;
    }
}
