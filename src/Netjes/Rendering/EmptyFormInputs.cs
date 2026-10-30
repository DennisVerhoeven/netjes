using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Netjes.Rendering;

/// <summary>
/// Gives an action an empty <c>[FromForm]</c> input where form binding gives it <c>null</c>. Form binding builds no
/// object for a post that carries none of the type's fields, such as a form whose only fields are unchecked
/// checkboxes, or one that posts nothing but its hidden fields. The delegate gets the object those missing fields
/// make instead: one from the type's parameterless constructor, or from its only constructor (a positional record's)
/// with every argument at its default. <see cref="ActionInputValidation"/> runs after this, so a server action reports
/// the empty input's <c>[Required]</c> members as field errors. A parameter declared nullable (<c>NoteInput?</c>)
/// keeps its <c>null</c>.
/// </summary>
internal static class EmptyFormInputs
{
    public static EndpointFilterDelegate Filter(EndpointFilterFactoryContext context, EndpointFilterDelegate next)
    {
        var nullability = new NullabilityInfoContext();
        var inputs = context.MethodInfo.GetParameters()
            .Select((parameter, index) => (index, create: EmptyFor(parameter, nullability)))
            .Where(input => input.create is not null)
            .Select(input => (input.index, create: input.create!))
            .ToArray();
        if (inputs.Length == 0)
        {
            return next;
        }

        return invocation =>
        {
            // Form binding that failed (a value it cannot read, a positional record without one of its fields) leaves
            // null too. Outside Development it answers 400 only after the filters have run, and the delegate does not
            // run: that null stays, so the answer stays 400 instead of becoming the empty input's field errors.
            if (invocation.HttpContext.Response.StatusCode < StatusCodes.Status400BadRequest)
            {
                foreach (var (index, create) in inputs)
                {
                    invocation.Arguments[index] ??= create();
                }
            }

            return next(invocation);
        };
    }

    /// <summary>
    /// Whether form binding maps the parameter from several fields: a <c>[FromForm]</c> class or record of the app's.
    /// Strings, files, lists and the framework's own types bind from one field, or are refused when it is missing.
    /// </summary>
    internal static bool IsFormObject(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        return parameter.GetCustomAttributes(inherit: true).OfType<IFromFormMetadata>().Any()
            && !type.IsValueType && !type.IsAbstract && !type.IsInterface && !typeof(IEnumerable).IsAssignableFrom(type)
            && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true
            && type.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) != true;
    }

    /// <summary>
    /// How to make the empty input of a form object (<see cref="IsFormObject"/>); null for any other parameter, for one
    /// declared nullable, and for a type form binding cannot build either.
    /// </summary>
    private static Func<object>? EmptyFor(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        if (!IsFormObject(parameter) || nullability.Create(parameter).ReadState == NullabilityState.Nullable)
        {
            return null;
        }

        var type = parameter.ParameterType;
        if (type.GetConstructor(Type.EmptyTypes) is { } parameterless)
        {
            return () => parameterless.Invoke(BindingFlags.DoNotWrapExceptions, null, [], null);
        }

        if (type.GetConstructors() is [var constructor])
        {
            var defaults = constructor.GetParameters()
                .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                .ToArray();
            return () => constructor.Invoke(BindingFlags.DoNotWrapExceptions, null, [.. defaults], null);
        }

        return null;
    }
}
