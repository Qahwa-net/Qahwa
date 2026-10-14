var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", framework = "Qahwa" }));

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
