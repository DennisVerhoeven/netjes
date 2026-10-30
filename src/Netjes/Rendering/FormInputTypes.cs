using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.AspNetCore.Http.Metadata;

namespace Netjes.Rendering;

/// <summary>
/// Stops the app when form binding could never build an action's <c>[FromForm]</c> input. It builds a class, struct or
/// record only through its one public constructor (a positional record's, or a parameterless one with <c>init</c>
/// properties; a struct may have none), and never an abstract class or an interface. For a type with several, a class
/// with none, or an abstract type, at the top or anywhere below it (a property's type, a list's items), it binds
/// <c>null</c> without a word, whatever was posted, and the action would get an empty input in its place
/// (<see cref="EmptyFormInputs"/>).
/// </summary>
internal static class FormInputTypes
{
    public static void RequireBuildable(string action, MethodInfo method)
    {
        foreach (var parameter in method.GetParameters().Where(p => p.GetCustomAttributes(inherit: true).OfType<IFromFormMetadata>().Any()))
        {
            if (Unbuildable(parameter.ParameterType, null, []) is ({ } type, var path, var constructors))
            {
                var (problem, fix) = constructors is { } count
                    ? ($"has {(count == 0 ? "none" : count)}",
                        $"Give {type.Name} one public constructor (a parameterless one with init properties, or a positional record's)" +
                        (path is null ? "." : $", or give {path} a type form binding can build."))
                    : (type.IsInterface ? "is an interface" : "is abstract",
                        $"Give {path ?? $"parameter '{parameter.Name}'"} a class, struct or record type with one public constructor.");
                throw new InvalidOperationException(
                    $"{action} cannot bind parameter '{parameter.Name}' from a form: form binding builds a class, struct or record only through its one public constructor, " +
                    $"and {type.Name}{(path is null ? "" : $" ({path})")} {problem}, so a post would never fill it. {fix}");
            }
        }
    }

    /// <summary>
    /// The first type form binding cannot build in what it maps from <paramref name="type"/>, where it is (the member
    /// that has it, null for the input itself) and how many public constructors it has (null for an abstract class or an
    /// interface); null when there is none. What it
    /// maps: an object's constructor parameters and its properties with a public getter and setter (<c>init</c> too) and
    /// no <see cref="IgnoreDataMemberAttribute"/>, and a list's or dictionary's items. Types of .NET and ASP.NET Core,
    /// and those read from one field, are left to it.
    /// </summary>
    private static (Type Type, string? Path, int? Constructors)? Unbuildable(Type type, string? path, HashSet<Type> seen)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (ActionFormFields.IsValue(type) || !seen.Add(type))
        {
            return null;
        }

        if (ActionFormFields.ItemOf(type) is { } item)
        {
            return Unbuildable(item, path, seen);
        }

        if (IsFramework(type))
        {
            return null;
        }

        if (type.IsAbstract || type.IsInterface)
        {
            return (type, path, null);
        }

        var constructors = type.GetConstructors();
        if (constructors.Length > 1 || (constructors.Length == 0 && !type.IsValueType))
        {
            return (type, path, constructors.Length);
        }

        var members = constructors.SelectMany(c => c.GetParameters()).Select(p => (Name: p.Name ?? "", Type: p.ParameterType))
            .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true && p.GetIndexParameters().Length == 0
                    && p.GetCustomAttribute<IgnoreDataMemberAttribute>() is null)
                .Select(p => (p.Name, Type: p.PropertyType)));
        foreach (var member in members)
        {
            if (Unbuildable(member.Type, $"{type.Name}.{member.Name}", seen) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>A type of .NET or ASP.NET Core (<c>System.Uri</c>), by its namespace; an app's <c>SystemsShop.Forms</c> is not one.</summary>
    private static bool IsFramework(Type type) =>
        type.Namespace is { } ns
        && (ns is "System" or "Microsoft"
            || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
}
