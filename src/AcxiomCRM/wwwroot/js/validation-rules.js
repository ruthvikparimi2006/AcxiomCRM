// Client side of the ConditionalRule attributes (ViewModels/ConditionalRules.cs).
(function ($) {
    function today() {
        const d = new Date();
        return d.getFullYear() + '-' + String(d.getMonth() + 1).padStart(2, '0') + '-' + String(d.getDate()).padStart(2, '0');
    }

    // A rule with dependson/values only applies while that other field holds one of the values.
    // The other field shares this field's prefix, e.g. "Opportunity.Amount" depends on "Opportunity.Stage".
    function applies(element, params) {
        if (!params || !params.dependson) return true;
        const prefix = element.name.substring(0, element.name.lastIndexOf('.') + 1);
        const other = element.form && element.form.elements[prefix + params.dependson];
        return !!other && params.values.split(',').indexOf(other.value) >= 0;
    }

    function register(name, test) {
        $.validator.addMethod(name, function (value, element, params) {
            return !value || !applies(element, params) || test(value);
        });
        $.validator.unobtrusive.adapters.add(name, ['dependson', 'values'], function (options) {
            options.rules[name] = options.params;
            options.messages[name] = options.message;
        });
    }

    register('notbeforetoday', function (value) { return value >= today(); }); // yyyy-MM-dd compares as text
    register('greaterthanzero', function (value) { return parseFloat(value) > 0; });
})(jQuery);
