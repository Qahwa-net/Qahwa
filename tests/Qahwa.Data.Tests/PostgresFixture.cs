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
        Overrides = new PermissionOverrides();
        PermissionStore = new PermissionStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        PermissionManager = new PermissionManager(Registry, PermissionStore, Overrides, Clock);
        var permissions = new PermissionService(Overrides);
        Shares = new ShareStore(Registry, Repository);
        WorkflowCatalog = new WorkflowCatalog();
        AssignmentRuleCatalog = new AssignmentRuleCatalog();
        NotificationRuleCatalog = new NotificationRuleCatalog();
        WebhookCatalog = new WebhookCatalog();
        WebhookStore = new WebhookStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        WebhookSender = new FakeWebhookSender();
        FileStorage = new InMemoryFileStorage();
        NotificationRuleStore = new NotificationRuleStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        AssignmentRuleStore = new AssignmentRuleStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        Profiles = new ProfileService(Registry, Repository, Clock);
        History = new HistoryRecorder(Registry, Repository, Clock);
        var lazyDocuments = new Lazy<DocumentService>(() => Documents!);
        Notifications = new NotificationService(Registry, Repository, lazyDocuments, History);
        Follows = new FollowService(Registry, Repository, lazyDocuments, History);
        Collaboration = new CollaborationHooks(Follows, Notifications, Registry, Repository);
        Documents = new DocumentService(Registry, Repository, permissions, new NamingService(Repository), Clock, new UserPermissionService(Registry, Repository), Shares, Profiles, History, Collaboration, WorkflowCatalog, new Lazy<AssignmentRuleEngine>(() => AssignmentRules!), new Lazy<NotificationRuleEngine>(() => NotificationRules!), new Lazy<WebhookService>(() => Webhooks!), new Lazy<FileService>(() => Files!), new Lazy<DocumentActionEngine>(() => DocumentActions!));
        ShareManager = new ShareManager(Registry, Repository, Documents, permissions, Shares, History, Notifications);
        Activity = new ActivityService(Registry, Repository, Documents, permissions, History, Collaboration);
        Assignments = new AssignmentService(Registry, Repository, Documents, permissions, ShareManager, Notifications, Follows, History);
        AutoRepeat = new AutoRepeatService(Registry, Repository, Documents, permissions, Notifications, History);
        ListSettings = new ListSettingsService(Registry, Repository, Documents, permissions, History);
        Bulk = new BulkService(Registry, Documents);
        DocTypeStore = new DocTypeStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        SchemaManager = new SchemaManager(Sync, Dialect);
        Designer = new DocTypeDesigner(Registry, DocTypeStore, SchemaManager, Repository, Clock);
        CustomizationStore = new CustomizationStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
        WorkflowStore = new WorkflowStore(_connections, Dialect, new PostgresSchemaInspector(_connections));
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
        AuthOptions = new AuthOptions { HashIterations = 1000 };
        Auth = new AuthService(
            Registry, Repository, Documents, AuthStore,
            new PasswordHasher<string>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })),
            Options.Create(AuthOptions), Clock);
        Webhooks = new WebhookService(WebhookCatalog, Registry, Repository, Documents, WebhookSender, History);
        WebhookManager = new WebhookManager(Registry, WebhookStore, WebhookCatalog, permissions, Clock, allowInsecure: false);
        Bundle = new BundleService(Registry, Designer, Customization, Workflows, AssignmentRuleManager, NotificationRuleManager, permissions, Clock);
        Backup = new BackupService(Registry, Repository, Bundle, permissions, Clock);
        Reports = new ReportService(Registry, Repository, Documents, permissions, Notifications, History);
        DocumentActions = new DocumentActionEngine(Registry, Repository, Documents, Clock);
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
    public DocumentActionEngine DocumentActions { get; }
    public BundleService Bundle { get; }
    public BackupService Backup { get; }
    public ReportService Reports { get; }
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
