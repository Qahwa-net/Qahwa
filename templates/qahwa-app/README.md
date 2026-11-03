# MyApp

A Qahwa app made from the `qahwa-app` template.

- `MyApp/MyApp.cs`: the app (`IQahwaApp`): a hook that refuses a title, and a server function `qahwa_app_id.item_count`.
- `MyApp/DocTypes/`: the DocType files the app brings (`DOCTYPE_PREFIX Item`). They are copied next to the assembly.
- `MyApp.Tests/`: tests that load the DocTypes and save a document through the engine.

Register the app in your host (the Qahwa server's `Program.cs`, or your own):

```csharp
builder.Services.AddQahwaApp<MyApp.QahwaAppModule>();
```

then start the server: the first start records the app on the site, creates its tables and starts its hooks, methods and jobs.
See `docs/manual/developer/08-writing-an-app.md` in the Qahwa repository for everything an app can add.
