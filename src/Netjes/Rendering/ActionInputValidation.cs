using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Netjes.Rendering;

/// <summary>
/// Checks the DataAnnotations of a server action's or page action's inputs before it runs, and answers with
/// <c>ServerAction.Invalid(errors)</c>: field errors for <c>useAction</c>, or the form's page again (a page action's own
/// page, with and without JavaScript).
/// Attributes count on properties and on record constructor parameters; <see cref="IValidatableObject"/> runs once the
/// attributes pass (its errors without a member are keyed <c>""</c>). An input is checked by the rules of its runtime
/// type, such as a derived type of a polymorphic JSON input, when its declared type or a derived type that type lists
/// with <see cref="JsonDerivedTypeAttribute"/> has rules; derived types set up only through <c>JsonSerializerOptions</c>
/// are checked only when the declared type has rules. Works without <c>AddValidation()</c>, whose generator does not
/// see inputs of actions mapped with <c>MapAction</c> or <c>MapPageAction</c>.
/// </summary>
internal static class ActionInputValidation
{
    private sealed record Rule(PropertyInfo Property, string DisplayName, ValidationAttribute[] Attributes);

    private sealed record TypeRules(Rule[] Rules, bool Validatable);

    private static readonly ConcurrentDictionary<Type, TypeRules?> Cache = new();

    public static EndpointFilterDelegate Filter(EndpointFilterFactoryContext context, EndpointFilterDelegate next)
    {
        // An input is checked by the rules of what it is: a derived type the declared one lists may have rules of its own.
        // A parameter none of those types has rules for is not checked, whatever is sent for it.
        var checks = context.MethodInfo.GetParameters()
            .Select((parameter, index) => (index, checkable: TypesOf(parameter.ParameterType).Any(type => RulesFor(type) is not null)))
            .Where(check => check.checkable)
            .Select(check => check.index)
            .ToArray();
        if (checks.Length == 0)
        {
            return next;
        }

        return async invocation =>
        {
            foreach (var index in checks)
            {
                if (invocation.Arguments[index] is { } input && Validate(input) is { } errors)
                {
                    return ServerAction.Invalid(errors);
                }
            }

            return await next(invocation);
        };
    }

    /// <summary>
    /// Stops the app when the DataAnnotations of an action's inputs cannot be read, naming the action
    /// (<paramref name="action"/>, e.g. "The server action 'posts.save'"). <see cref="Filter"/> reads them when ASP.NET
    /// Core first builds the endpoints, on the first request, and an endpoint it cannot build fails every request of the
    /// app; here they are read at startup. So are those of the derived types an input lists with
    /// <see cref="JsonDerivedTypeAttribute"/>, which <see cref="Validate"/> would read on the first request that sends one.
    /// </summary>
    public static void RequireReadable(string action, MethodInfo method)
    {
        foreach (var parameter in method.GetParameters())
        {
            try
            {
                foreach (var type in TypesOf(parameter.ParameterType))
                {
                    RulesFor(type);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{action} cannot validate parameter '{parameter.Name}'. {ex.Message}", ex);
            }
        }
    }

    /// <summary>The type an input is declared with, and the derived types it lists for polymorphic JSON.</summary>
    private static IEnumerable<Type> TypesOf(Type declared) =>
        declared.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).Select(a => a.DerivedType).Prepend(declared).Distinct();

    public static IDictionary<string, string[]>? Validate(object input)
    {
        if (RulesFor(input.GetType()) is not { } type)
        {
            return null;
        }

        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var rule in type.Rules)
        {
            var value = rule.Property.GetValue(input);
            var context = new ValidationContext(input) { MemberName = rule.Property.Name, DisplayName = rule.DisplayName };
            foreach (var attribute in rule.Attributes)
            {
                if (attribute.GetValidationResult(value, context) is { ErrorMessage: { } message })
                {
                    Add(errors, rule.Property.Name, message);
                }
            }
        }

        if (errors.Count == 0 && type.Validatable && input is IValidatableObject validatable)
        {
            foreach (var result in validatable.Validate(new ValidationContext(input)))
            {
                foreach (var member in result.MemberNames.DefaultIfEmpty(""))
                {
                    Add(errors, member, result.ErrorMessage ?? "Invalid.");
                }
            }
        }

        return errors.Count == 0 ? null : errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    private static void Add(Dictionary<string, List<string>> errors, string member, string message)
    {
        if (!errors.TryGetValue(member, out var messages))
        {
            errors[member] = messages = [];
        }

        messages.Add(message);
    }

    private static TypeRules? RulesFor(Type type) => Cache.GetOrAdd(type, static type =>
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.IsAbstract || type.IsInterface
            || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true)
        {
            return null;
        }

        // A record's attributes sit on its constructor parameters unless written [property: ...].
        var parameters = type.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => p.Name is not null)
            .ToLookup(p => p.Name!, StringComparer.OrdinalIgnoreCase);
        var rules = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => RuleFor(p, parameters[p.Name]))
            .Where(rule => rule.Attributes.Length > 0)
            .ToArray();
        var validatable = typeof(IValidatableObject).IsAssignableFrom(type);
        return rules.Length > 0 || validatable ? new TypeRules(rules, validatable) : null;
    });

    /// <summary>A property's rule, from its attributes and those of the constructor parameters of the same name.</summary>
    private static Rule RuleFor(PropertyInfo property, IEnumerable<ParameterInfo> parameters)
    {
        try
        {
            return new Rule(
                property,
                property.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? property.Name,
                [.. property.GetCustomAttributes<ValidationAttribute>(inherit: true), .. parameters.SelectMany(p => p.GetCustomAttributes<ValidationAttribute>(inherit: true))]);
        }
        catch (Exception ex)
        {
            // A [Display] whose resource type lacks the name, an attribute whose constructor throws.
            throw new InvalidOperationException($"The DataAnnotations of {property.DeclaringType?.Name}.{property.Name} cannot be read: {ex.Message}", ex);
        }
    }
}
