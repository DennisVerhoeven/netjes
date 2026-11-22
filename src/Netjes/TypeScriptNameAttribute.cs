namespace Netjes;

/// <summary>
/// Names the TypeScript schema and type that the generated file writes for a C# type, in place of the C# type's own name.
/// Without it, a type is named by its own name, and by its declaring type's and its namespace's too where two types
/// would share one, so a nested record <c>AuditFilters.Values</c> is <c>Values</c>: put the attribute on the nested
/// type to name it <c>AuditFilterValues</c>. Only the types that carry it are renamed.
/// </summary>
/// <remarks>
/// The name is an identifier of ASCII letters, digits, <c>_</c> and <c>$</c> that does not start with a digit, is no
/// reserved word of a JavaScript module and is none of the names the generated file declares (<c>pages</c>, <c>Href</c>,
/// <c>z</c>, ...). It is given to one type: the generator stops, naming both, when two types ask for it. Another type that
/// would have had the name by its own name takes another, as it does when two types share one. On a generic type the
/// attribute reaches every closed form of it, so only one of them can be written: name a non-generic type.
/// </remarks>
/// <param name="name">The name of the schema and of the type in the generated file.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class TypeScriptNameAttribute(string name) : Attribute
{
    /// <summary>The name of the schema and of the type in the generated file.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}
