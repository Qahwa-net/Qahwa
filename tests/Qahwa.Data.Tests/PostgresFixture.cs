using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
using Qahwa.Data;
using Qahwa.Data.Postgres;
using Qahwa.Testing;
using Testcontainers.PostgreSql;

namespace Qahwa.Data.Tests;

/// <summary>Starts one PostgreSQL container per test class. Tests skip themselves when Docker is unavailable.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool Available { get; private set; }
    public string SkipReason { get; private set; } = "Docker is not available.";

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17").Build();
            await _container.StartAsync();
            Available = true;
        }
        catch (Exception ex)
        {
            SkipReason = $"PostgreSQL container could not start: {ex.GetType().Name}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>A fully wired data stack in its own schema, so tests never see each other's tables.</summary>
    public async Task<PgStack> CreateStackAsync()
    {
        var schema = "t_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = NpgsqlDataSource.Create(_container!.GetConnectionString()))
        await using (var command = admin.CreateCommand($"CREATE SCHEMA {schema}"))
            await command.ExecuteNonQueryAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { SearchPath = schema }.ToString();
        var stack = new PgStack(NpgsqlDataSource.Create(connectionString));
        await stack.MigrateAsync();
        return stack;
    }
}

public sealed class PgStack : IAsyncDisposable
{
    private readonly NpgsqlConnectionFactory _connections;

    public PgStack(NpgsqlDataSource dataSource)
    {
        DataSource = dataSource;
        _connections = new NpgsqlConnectionFactory(dataSource);
        Dialect = new PostgresDialect();
        Sync = new SchemaSync(_connections, Dialect, new PostgresSchemaInspector(_connections));
        Registry = TestMetas.Registry();
        Repository = new DocumentRepository(_connections, Dialect, new SqlBuilder(Dialect));
        Clock = new TestClock(DateTimeOffset.UtcNow);
        Documents = new DocumentService(Registry, Repository, new PermissionService(), new NamingService(Repository), Clock);
        DocTypeStore = new DocTypeStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        SchemaManager = new SchemaManager(Sync, Dialect);
        Designer = new DocTypeDesigner(Registry, DocTypeStore, SchemaManager, Repository, Clock);
        AuthStore = new AuthStore(_connections, Dialect);
        AuthOptions = new AuthOptions { HashIterations = 1000 };
        Auth = new AuthService(
            Registry, Repository, Documents, AuthStore,
            new PasswordHasher<string>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })),
            Options.Create(AuthOptions), Clock);
    }

    public DocTypeStore DocTypeStore { get; }
    public SchemaManager SchemaManager { get; }
    public DocTypeDesigner Designer { get; }
    public AuthStore AuthStore { get; }
    public AuthOptions AuthOptions { get; }
    public AuthService Auth { get; }

    public NpgsqlDataSource DataSource { get; }
    public PostgresDialect Dialect { get; }
    public SchemaSync Sync { get; }
    public MetaRegistry Registry { get; }
    public DocumentRepository Repository { get; }
    public TestClock Clock { get; }
    public DocumentService Documents { get; }

    public async Task MigrateAsync()
    {
        await Sync.EnsureSystemTablesAsync();
        foreach (var meta in Registry.All)
            await Sync.ApplyAsync(await Sync.PlanAsync(meta));
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public ValueTask DisposeAsync() => _connections.DisposeAsync();
}
