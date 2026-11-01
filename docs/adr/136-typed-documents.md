# ADR-136: Typed documents from a source generator

**Status:** accepted (developer experience)

## Context
A document is a dictionary (ADR-002), which needs no recompile when a DocType changes but gives an app developer no IntelliSense and no compile-time check of field names.

## Decision
- `Qahwa.SourceGenerators` is a Roslyn incremental generator. A project lists its DocType files as `AdditionalFiles` and references the generator as an analyzer; for every file that has a `name` and `fields` it writes a `partial` class (`Sales Invoice` becomes `SalesInvoice`) in the namespace `Qahwa.Typed` (set `QahwaTypedNamespace` to change it).
- **The class is a view over the dictionary** (`TypedDocument` in `Qahwa.Abstractions`): `new SalesInvoice(values)` wraps what `GetAsync` returned without copying, and `invoice.Values` goes straight into `InsertAsync` and `UpdateAsync`. Nothing in the engine changes.
- Properties by field type: Int/Rating/Duration `int?`, Float `double?`, Decimal/Currency/Percent `decimal?`, Check `bool`, Date `DateOnly?`, Datetime `DateTime?`, Time `TimeOnly?`, everything else `string?`. Layout fields (sections, columns, tabs, headings, HTML) get none. Values that arrive as text (a JSON body) are converted when read.
- **Table fields** become `List<Child>` of the child's typed class when its file is listed too (otherwise a list of dictionaries), plus an `AddLines()` that appends an empty row. Rows are views, so changing one changes the document.
- A nested `Fields` class holds the field names as constants. A field whose name clashes with a built-in member (`Name`, `Owner`, `Values`, …) gets `Field` added (`OwnerField`).
- A file that is not valid JSON gives warning QWG001; two DocTypes that give one class name give QWG002 and the second is skipped.
- The generator reads JSON with its own small parser, so no package has to ship beside it.

## Consequences
- The generator uses the DocType JSON as written, not the merged result of customisations, so a field added in the Desk has no property until it is added to the file.
- Not yet: a NuGet package of the generator, typed Link targets and Select options (enums), and a template switch that turns it on.
