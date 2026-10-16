using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
using Qahwa.Data;
using Qahwa.Data.Postgres;
using Qahwa.Data.SqlServer;
using Qahwa.Testing;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Qahwa.Data.Tests;

/// <summary>
/// Everything a stack needs from one database product. The whole suite runs against PostgreSQL, or against SQL Server when the
/// environment variable <c>QAHWA_TEST_DB</c> is <c>sqlserver</c> (tests that look at one product's catalogs skip themselves on the other).
/// </summary>
public sealed class DbFlavor(string name, ISqlDialect dialect, IDbConnectionFactory connections, Func<IDbConnectionFactory, ISchemaInspector> inspector, Func<string, DbCommand> rawCommand, Func<ValueTask> dispose)
{
    public string Name { get; } = name;
    public ISqlDialect Dialect { get; } = dialect;
    public IDbConnectionFactory Connections { get; } = connections;
    public Func<IDbConnectionFactory, ISchemaInspector> Inspector { get; } = inspector;
    public Func<string, DbCommand> RawCommand { get; } = rawCommand;
    public Func<ValueTask> Dispose { get; } = dispose;
}

/// <summary>A way to run a statement of the test's own on the stack's database.</summary>
public sealed class RawSql(Func<string, DbCommand> create)
{
    public DbCommand CreateCommand(string sql) => create(sql);
}

/// <summary>Starts one database container per test class. Tests skip themselves when Docker is unavailable.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private MsSqlContainer? _mssql;

    // One SQL Server for the whole run: each needs about 2 GB, and test classes would otherwise start one apiece, at the same time.
    private static readonly Lazy<Task<MsSqlContainer>> SharedSqlServer = new(async () =>
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await container.StartAsync();
        return container;
    });

    public static bool UseSqlServer { get; } = string.Equals(Environment.GetEnvironmentVariable("QAHWA_TEST_DB"), "sqlserver", StringComparison.OrdinalIgnoreCase);

    public bool Available { get; private set; }

    public string ConnectionString => UseSqlServer ? _mssql!.GetConnectionString() : _container!.GetConnectionString();
    public string SkipReason { get; private set; } = "Docker is not available.";

    public async Task InitializeAsync()
    {
        try
        {
            if (UseSqlServer)
            {
                _mssql = await SharedSqlServer.Value;
            }
            else
            {
                _container = new PostgreSqlBuilder("postgres:17").Build();
                await _container.StartAsync();
            }

            Available = true;
        }
        catch (Exception ex)
        {
            if (UseSqlServer)
                throw new InvalidOperationException("QAHWA_TEST_DB=sqlserver was asked for but SQL Server could not start: " + ex.Message, ex); // never skip silently
            SkipReason = $"The database container could not start: {ex.GetType().Name}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
        // The shared SQL Server stays up for the other classes; it is removed when the test run ends.
    }

    public async Task<PgStack> CreateStackAsync()
    {
        var unique = "t_" + Guid.NewGuid().ToString("N")[..12];
        DbFlavor flavor;
        if (UseSqlServer)
        {
            // A database of its own for each stack (a schema would need a user of its own).
            await using (var admin = new SqlConnection(_mssql!.GetConnectionString()))
            {
                await admin.OpenAsync();
                await using var create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{unique}]";
                await create.ExecuteNonQueryAsync();
            }

            var connectionString = new SqlConnectionStringBuilder(_mssql.GetConnectionString()) { InitialCatalog = unique, Pooling = false }.ConnectionString;
            var factory = new SqlServerConnectionFactory(connectionString);
            flavor = new DbFlavor("sqlserver", new SqlServerDialect(), factory, c => new SqlServerSchemaInspector(c), sql =>
            {
                var connection = new SqlConnection(connectionString);
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = sql;
                return command;
            }, () => ValueTask.CompletedTask);
        }
        else
        {
            await using (var admin = NpgsqlDataSource.Create(_container!.GetConnectionString()))
            await using (var command = admin.CreateCommand($"CREATE SCHEMA {unique}"))
                await command.ExecuteNonQueryAsync();

            var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { SearchPath = unique }.ToString());
            var factory = new NpgsqlConnectionFactory(source);
            flavor = new DbFlavor("postgres", new PostgresDialect(), factory, c => new PostgresSchemaInspector(c), sql => source.CreateCommand(sql), () => factory.DisposeAsync());
        }

        var stack = new PgStack(flavor);
        await stack.MigrateAsync();
        return stack;
    }
}

public sealed class PgStack : IAsyncDisposable
{
    private readonly IDbConnectionFactory _connections;
    private readonly DbFlavor _flavor;

    public PgStack(DbFlavor flavor)
    {
        _flavor = flavor;
        DataSource = new RawSql(flavor.RawCommand);
        _connections = flavor.Connections;
        Dialect = flavor.Dialect;
        Sync = new SchemaSync(_connections, Dialect, flavor.Inspector(_connections));
        Registry = TestMetas.Registry();
        Repository = new DocumentRepository(_connections, Dialect, new SqlBuilder(Dialect));
        Clock = new TestClock(DateTimeOffset.UtcNow);
        Overrides = new PermissionOverrides();
        PermissionStore = new PermissionStore(_connections, Dialect, flavor.Inspector(_connections));
        PermissionManager = new PermissionManager(Registry, PermissionStore, Overrides, Clock);
        var permissions = new PermissionService(Overrides);
        Shares = new ShareStore(Registry, Repository);
        WorkflowCatalog = new WorkflowCatalog();
        AssignmentRuleCatalog = new AssignmentRuleCatalog();
        NotificationRuleCatalog = new NotificationRuleCatalog();
        WebhookCatalog = new WebhookCatalog();
        WebhookStore = new WebhookStore(_connections, Dialect, flavor.Inspector(_connections));
        WebhookSender = new FakeWebhookSender();
        FileStorage = new InMemoryFileStorage();
        NotificationRuleStore = new NotificationRuleStore(_connections, Dialect, flavor.Inspector(_connections));
        AssignmentRuleStore = new AssignmentRuleStore(_connections, Dialect, flavor.Inspector(_connections));
        Profiles = new ProfileService(Registry, Repository, Clock);
        History = new HistoryRecorder(Registry, Repository, Clock);
        var lazyDocuments = new Lazy<DocumentService>(() => Documents!);
        Notifications = new NotificationService(Registry, Repository, lazyDocuments, History);
        Follows = new FollowService(Registry, Repository, lazyDocuments, History);
        Collaboration = new CollaborationHooks(Follows, Notifications, Registry, Repository);
        Documents = new DocumentService(Registry, Repository, permissions, new NamingService(Repository, Clock), Clock, new UserPermissionService(Registry, Repository), Shares, Profiles, History, Collaboration, WorkflowCatalog, new Lazy<AssignmentRuleEngine>(() => AssignmentRules!), new Lazy<NotificationRuleEngine>(() => NotificationRules!), new Lazy<WebhookService>(() => Webhooks!), new Lazy<FileService>(() => Files!), new Lazy<DocumentActionEngine>(() => DocumentActions!), new Lazy<ServerScriptEngine>(() => ServerScripts!), new Lazy<AppHookRunner>(() => AppHooks!), new FakeSecretProtector());
        ShareManager = new ShareManager(Registry, Repository, Documents, permissions, Shares, History, Notifications);
        Activity = new ActivityService(Registry, Repository, Documents, permissions, History, Collaboration);
        Assignments = new AssignmentService(Registry, Repository, Documents, permissions, ShareManager, Notifications, Follows, History);
        AutoRepeat = new AutoRepeatService(Registry, Repository, Documents, permissions, Notifications, History);
        ListSettings = new ListSettingsService(Registry, Repository, Documents, permissions, History);
        Bulk = new BulkService(Registry, Documents);
        DocTypeStore = new DocTypeStore(_connections, Dialect, flavor.Inspector(_connections));
        SchemaManager = new SchemaManager(Sync, Dialect);
        Designer = new DocTypeDesigner(Registry, DocTypeStore, SchemaManager, Repository, Clock);
        CustomizationStore = new CustomizationStore(_connections, Dialect, flavor.Inspector(_connections));
        WorkflowStore = new WorkflowStore(_connections, Dialect, flavor.Inspector(_connections));
        AssignmentRules = new AssignmentRuleEngine(AssignmentRuleCatalog, Registry, Repository, Assignments, Notifications);
        Print = new PrintService(Registry, Repository, Documents, permissions, History);
        Dashboards = new DashboardService(Registry, Repository, Documents, permissions, Notifications, History);
        DataImport = new DataImportService(Registry, Documents, permissions);
        Workspaces = new WorkspaceService(Registry, Repository, Dashboards, permissions, History);
        EmailSender = new FakeEmailSender();
        Email = new EmailService(Registry, Repository, Documents, permissions, History, EmailSender);
        NotificationRules = new NotificationRuleEngine(NotificationRuleCatalog, Registry, Repository, Documents, Notifications, Follows, Email, Clock);
        NotificationRuleManager = new NotificationRuleManager(Registry, NotificationRuleStore, NotificationRuleCatalog, Repository, permissions, Clock);
        AssignmentRuleManager = new AssignmentRuleManager(Registry, AssignmentRuleStore, AssignmentRuleCatalog, Repository, permissions, Clock);
        Workflows = new WorkflowManager(Registry, WorkflowStore, WorkflowCatalog, permissions, Clock);
        Customization = new CustomizationManager(Registry, CustomizationStore, SchemaManager, permissions, Clock);
        AuthStore = new AuthStore(_connections, Dialect);
        TotpStore = new TotpStore(_connections, Dialect);
        SessionStore = new SessionStore(_connections, Dialect);
        IdentityStore = new IdentityStore(_connections, Dialect);
        AuthOptions = new AuthOptions { HashIterations = 1000 };
        Auth = new AuthService(
            Registry, Repository, Documents, AuthStore,
            new PasswordHasher<string>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })),
            Options.Create(AuthOptions), Clock);
        Webhooks = new WebhookService(WebhookCatalog, Registry, Repository, Documents, WebhookSender, History);
        WebhookManager = new WebhookManager(Registry, WebhookStore, WebhookCatalog, permissions, Clock, allowInsecure: false);
        Bundle = new BundleService(Registry, Designer, Customization, Workflows, AssignmentRuleManager, NotificationRuleManager, permissions, Clock);
        Backup = new BackupService(Registry, Repository, Bundle, permissions, Clock);
        DeletedDocuments = new DeletedDocumentService(Registry, Repository, permissions, History, Clock);
        Reports = new ReportService(Registry, Repository, Documents, permissions, Notifications, History);
        DocumentActions = new DocumentActionEngine(Registry, Repository, Documents, Clock);
        AppCatalog = new AppCatalog([]);
        var lazyDocs2 = new Lazy<DocumentService>(() => Documents);
        AppHooks = new AppHookRunner(AppCatalog, new Qahwa.Testing.EmptyServices(), lazyDocs2, new ErrorLogService(Documents, new HitCounter(Clock)));
        Methods = new MethodService(AppCatalog, new Qahwa.Testing.EmptyServices(), lazyDocs2, new HitCounter(Clock));
        AppInstaller = new AppInstaller(AppCatalog, Registry, Repository, Documents, Clock);
        ServerScripts = new ServerScriptEngine(Registry, Repository, new ScriptSandbox(Documents, Clock), Clock);
        Files = new FileService(Registry, Repository, Documents, permissions, Notifications, FileStorage, History);
        ApiKeys = new ApiKeyService(Registry, Repository, Auth, permissions, History);
    }

    public PermissionOverrides Overrides { get; }
    public PermissionStore PermissionStore { get; }
    public PermissionManager PermissionManager { get; }
    public ShareStore Shares { get; }
    public CustomizationStore CustomizationStore { get; }
    public CustomizationManager Customization { get; }
    public WorkflowCatalog WorkflowCatalog { get; }
    public WorkflowStore WorkflowStore { get; }
    public WorkflowManager Workflows { get; }
    public AutoRepeatService AutoRepeat { get; }
    public AssignmentRuleCatalog AssignmentRuleCatalog { get; }
    public AssignmentRuleStore AssignmentRuleStore { get; }
    public AssignmentRuleEngine AssignmentRules { get; }
    public AssignmentRuleManager AssignmentRuleManager { get; }
    public NotificationRuleCatalog NotificationRuleCatalog { get; }
    public NotificationRuleStore NotificationRuleStore { get; }
    public NotificationRuleEngine NotificationRules { get; }
    public FakeEmailSender EmailSender { get; }
    public DashboardService Dashboards { get; }
    public PrintService Print { get; }
    public WorkspaceService Workspaces { get; }
    public DataImportService DataImport { get; }
    public ApiKeyService ApiKeys { get; }
    public WebhookCatalog WebhookCatalog { get; }
    public WebhookStore WebhookStore { get; }
    public FakeWebhookSender WebhookSender { get; }
    public WebhookService Webhooks { get; }
    public WebhookManager WebhookManager { get; }
    public InMemoryFileStorage FileStorage { get; }
    public FileService Files { get; }
    public EmailService Email { get; }
    public NotificationRuleManager NotificationRuleManager { get; }
    public ProfileService Profiles { get; }
    public HistoryRecorder History { get; }
    public ActivityService Activity { get; }
    public NotificationService Notifications { get; }
    public FollowService Follows { get; }
    public CollaborationHooks Collaboration { get; }
    public AssignmentService Assignments { get; }
    public ListSettingsService ListSettings { get; }
    public BulkService Bulk { get; }
    public ShareManager ShareManager { get; }
    public DocTypeStore DocTypeStore { get; }
    public SchemaManager SchemaManager { get; }
    public DocTypeDesigner Designer { get; }
    public TotpStore TotpStore { get; }
    public SessionStore SessionStore { get; }
    public IdentityStore IdentityStore { get; }
    public DocumentActionEngine DocumentActions { get; }
    public ServerScriptEngine ServerScripts { get; }
    public AppCatalog AppCatalog { get; }
    public AppHookRunner AppHooks { get; }
    public MethodService Methods { get; }
    public AppInstaller AppInstaller { get; }
    public BundleService Bundle { get; }
    public BackupService Backup { get; }
    public DeletedDocumentService DeletedDocuments { get; }
    public ReportService Reports { get; }
    public AuthStore AuthStore { get; }
    public AuthOptions AuthOptions { get; }
    public AuthService Auth { get; }

    public RawSql DataSource { get; }
    public ISqlDialect Dialect { get; }
    public bool IsPostgres => _flavor.Name == "postgres";
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

    public async Task<bool> TableExistsAsync(string table) =>
        await ScalarAsync(IsPostgres
            ? $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = '{table.Replace("'", "")}'"
            : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = SCHEMA_NAME() AND table_name = '{table.Replace("'", "")}'") == 1;

    /// <summary>How many of these columns the table has.</summary>
    public Task<long> ColumnCountAsync(string table, params string[] columns) =>
        ScalarAsync($"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = {(IsPostgres ? "current_schema()" : "SCHEMA_NAME()")} AND table_name = '{table.Replace("'", "")}' AND column_name IN ({string.Join(", ", columns.Select(c => "'" + c.Replace("'", "") + "'"))})");

    /// <summary>How many indexes the table has on exactly this column.</summary>
    public Task<long> IndexCountAsync(string table, string column) => ScalarAsync(IsPostgres
        ? $"SELECT COUNT(*) FROM pg_indexes WHERE schemaname = current_schema() AND tablename = '{table.Replace("'", "")}' AND indexdef LIKE '%({column.Replace("'", "")})%'"
        : $"SELECT COUNT(*) FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.object_id = OBJECT_ID(QUOTENAME(SCHEMA_NAME()) + '.' + QUOTENAME('{table.Replace("'", "")}')) AND c.name = '{column.Replace("'", "")}' AND i.is_primary_key = 0");

    /// <summary>The statement that changes a column to long text behind the application's back.</summary>
    public string ChangeColumnToTextSql(string table, string column) => IsPostgres
        ? $"ALTER TABLE \"{table}\" ALTER COLUMN \"{column}\" TYPE text"
        : $"ALTER TABLE [{table}] ALTER COLUMN [{column}] nvarchar(max)";

    public async Task<long> ScalarAsync(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>The database type of a column, from the catalog (for example "integer" or "character varying").</summary>
    public async Task<string?> ColumnTypeAsync(string table, string column)
    {
        await using var command = DataSource.CreateCommand($"SELECT data_type FROM information_schema.columns WHERE table_name = '{table.Replace("'", "")}' AND column_name = '{column.Replace("'", "")}'");
        return await command.ExecuteScalarAsync() as string;
    }

    public ValueTask DisposeAsync() => _flavor.Dispose();
}
