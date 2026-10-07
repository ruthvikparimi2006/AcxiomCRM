using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace AcxiomCRM.ViewModels;

// Validation rules that can be limited to a condition on another field, e.g. "only while Stage is an open stage".
// Each runs on the server and, through wwwroot/js/validation-rules.js, in the browser.
public abstract class ConditionalRuleAttribute(string clientRule) : ValidationAttribute, IClientModelValidator
{
    // Optional: the rule only applies when property DependsOn has one of the comma-separated Values.
    public string? DependsOn { get; set; }
    public string? Values { get; set; }

    protected abstract bool IsAllowed(object value);

    protected override ValidationResult? IsValid(object? value, ValidationContext context)
    {
        if (value is null || !Applies(context.ObjectInstance) || IsAllowed(value)) return ValidationResult.Success;
        return new ValidationResult(ErrorMessage, [context.MemberName!]);
    }

    bool Applies(object instance)
    {
        if (DependsOn is null) return true;
        var other = instance.GetType().GetProperty(DependsOn)?.GetValue(instance)?.ToString();
        return other is not null && Values!.Split(',').Contains(other);
    }

    public void AddValidation(ClientModelValidationContext context)
    {
        context.Attributes.TryAdd("data-val", "true");
        context.Attributes.TryAdd($"data-val-{clientRule}", ErrorMessage!);
        if (DependsOn is null) return;
        context.Attributes.TryAdd($"data-val-{clientRule}-dependson", DependsOn);
        context.Attributes.TryAdd($"data-val-{clientRule}-values", Values!);
    }
}

// VAL-05 / VAL-15 / VAL-16: a date that cannot be earlier than today.
// Limitation: "today" is the server's local date; per-user time zones are not modelled.
[AttributeUsage(AttributeTargets.Property)]
public class NotBeforeTodayAttribute() : ConditionalRuleAttribute("notbeforetoday")
{
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    protected override bool IsAllowed(object value) => value is not DateOnly date || date >= Today;
}

// VAL-06 / VAL-13: a number that must be greater than zero.
[AttributeUsage(AttributeTargets.Property)]
public class GreaterThanZeroAttribute() : ConditionalRuleAttribute("greaterthanzero")
{
    protected override bool IsAllowed(object value) => Convert.ToDecimal(value) > 0;
}
