# ADR-134: A `dotnet new` template for apps

**Status:** accepted (developer experience)

## Context
Writing an app means knowing the right project settings, how DocType files travel with the assembly, what a valid app name is, and how to test it. A newcomer has to assemble that from the manual.

## Decision
- `templates/qahwa-app` is a `dotnet new` template (`qahwa-app`). `dotnet new qahwa-app -n Acme.Crm --FrameworkPath <repo>` makes a class library (with an `IQahwaApp` called `QahwaAppModule`, a hook that refuses a title, and a server function), a DocType file that is copied next to the assembly, a test project that loads the DocTypes and saves a document through the engine, and a short README.
- **Names are made safe.** Whatever name is given, the app name (`^[a-z][a-z0-9_]{1,30}$`), the DocType and module names (letters, digits, spaces) and the namespace are derived from it, so `Acme.Crm-2` gives `acme_crm_2`, `Acme Crm 2 Item` and `Acme.Crm_2`. Project file names and references keep the name as typed.
- **It points at a checkout of the framework** (`--FrameworkPath`, default `../Qahwa`) with project references, because the framework is not yet published as packages; when it is, the template switches to package references.
- It was checked by generating an app with an awkward name from this repository, then building it and running its tests.

## Consequences
- Not yet: a host project in the template (the app is registered in the Qahwa server's `Program.cs` or the person's own host), and publishing the template as a NuGet package.
