# 0061 – Typegen names a type by attribute, reads a form as a record and keeps an export in the app's file

Status: accepted; extends [ADR 0006](0006-types-and-zod-from-csharp-contracts.md)

## Context

Four things about the generated TypeScript were found by apps that build on page packages.

- A record nested in another type is named by its own name, `AuditFilters.Values` as `Values`, and by its declaring type's and its namespace's too only where two types share a name. In the app's file the name says nothing about what the record is, and another type that is added later changes it.
- An action that reads the form itself (`IFormCollection`) was typed as a list of key and value pairs, because the collection enumerates as pairs. The browser posts a record of fields by name, so a page that read `forms['/files']` got a type no value has.
- A page package's export (`AddTypeScriptExport`) is written in the app's file and in the package's. What an app registers through a package, such as the typed data of widgets the app adds, belongs to that app, but the package's file ships, so it carried one app's entries to every app.
- Typegen writes a polymorphic union from the System.Text.Json contract of the app's options, modifiers included, so derived types that a package adds at startup are in the union. Nothing said so, or checked it.

## Decision

- `[TypeScriptName("...")]` on a class, struct, enum or interface gives its schema and type that name in the generated file. It is opt-in: a type without it is named as before, so no app's file changes. The name is an identifier of ASCII letters, digits, `_` and `$` that is no reserved word and none of the file's own names. Two types that ask for one name, a name of the file, and a name that is no identifier stop the generator with a message that names the types. A type that would have had the name by its own name takes another, as when two types share one; in the app's file, a type of a package's export that asks for the name of one of the app's own types takes another name there instead, because the app's schemas keep their names and the app cannot change the package's attribute; the package's own file keeps the name it asked for.
- A form read as `IFormCollection` is `z.record(z.string(), z.string())`.
- `AddTypeScriptExport(group, key, slots, appOnly: true)` writes the export in the app's file only, with the schemas of its types. Every entry of an export is of one kind, and the names of the package's file are free for it, since it is not in that file. The overload without `appOnly` is as it was.
- Derived types added to a union through the app's JSON options at startup are documented as the way for a package to add types to one, and a test pins that typegen reads them.

## Alternatives considered

- **Name nested records with their declaring type by default.** It renames what apps already import. An attribute changes nothing until an app asks.
- **A way to name the type at the property or at the export, not on the type.** A type's name is one thing in the file: naming it where it is used would give one type many names, or make the reference the only place that knows it.
- **An optional `appOnly` parameter on the existing method.** It changes the method's signature, which breaks a package compiled against the last version. A new overload keeps it.
- **A list of pairs kept for `IFormCollection`, with a note.** The shape never matched what a page can read; there is no app to keep it for.

## Consequences

- The public API gains `TypeScriptNameAttribute` and one `AddTypeScriptExport` overload.
- A field posted twice reads as one value in the record's type; an action that needs every value of a field reads the collection in C#.
- Renaming a type with the attribute renames its import in every app that uses the generated file: it is a change the app makes on purpose.
