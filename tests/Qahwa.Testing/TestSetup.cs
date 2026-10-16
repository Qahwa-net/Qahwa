using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;

namespace Qahwa.Testing;

public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class TestMetas
{
    public const string Territory = """
        {
          "name": "Territory",
          "autoname": "field:territory_name",
          "fields": [ { "name": "territory_name", "type": "Data", "required": true } ],
          "permissions": [ { "role": "Sales Manager", "read": true, "create": true } ]
        }
        """;

    public const string Customer = """
        {
          "name": "Customer",
          "autoname": "CUST-.#####",
          "titleField": "customer_name",
          "fields": [
            { "name": "customer_name", "type": "Data", "required": true, "unique": true },
            { "name": "customer_type", "type": "Select", "options": "Company\nIndividual", "default": "Company" },
            { "name": "territory", "type": "Link", "options": "Territory" },
            { "name": "credit_limit", "type": "Currency" },
            { "name": "since", "type": "Date" },
            { "name": "active", "type": "Check", "default": "1" }
          ],
          "permissions": [
            { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true },
            { "role": "Sales User", "read": true, "write": true, "create": true, "ifOwner": true }
          ]
        }
        """;

    public static MetaRegistry Registry()
    {
        var registry = new MetaRegistry();
        registry.AddCoreDocTypes();
        registry.Add(DocTypeLoader.Parse(Territory));
        registry.Add(DocTypeLoader.Parse(Customer));
        registry.ValidateReferences();
        return registry;
    }
}

public static class TestUsers
{
    public static readonly UserContext Admin = new("Administrator", [UserContext.AdministratorRole]);
    public static readonly UserContext Manager = new("manager@example.com", ["Sales Manager"]);
    public static readonly UserContext SalesA = new("a@example.com", ["Sales User"]);
    public static readonly UserContext SalesB = new("b@example.com", ["Sales User"]);
    public static readonly UserContext Nobody = new("nobody@example.com", []);
    public static readonly UserContext SystemManager = new("sysmgr@example.com", ["System Manager"]);

    public const string StrongPassword = "correct horse battery staple";
}

public sealed class TestEngine
{
    public TestEngine(IDocumentRepository? repository = null)
    {
        Registry = TestMetas.Registry();
        Repository = repository ?? new InMemoryDocumentRepository();
        Clock = new TestClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        Overrides = new PermissionOverrides();
        Permissions = new PermissionService(Overrides);
        PermissionStore = new InMemoryPermissionStore();
        PermissionManager = new PermissionManager(Registry, PermissionStore, Overrides, Clock);
        Shares = new ShareStore(Registry, Repository);
        CustomizationStore = new InMemoryCustomizationStore();
        WorkflowCatalog = new WorkflowCatalog();
        WorkflowStore = new InMemoryWorkflowStore();
        AssignmentRuleCatalog = new AssignmentRuleCatalog();
        AssignmentRuleStore = new InMemoryAssignmentRuleStore();
        NotificationRuleCatalog = new NotificationRuleCatalog();
        WebhookCatalog = new WebhookCatalog();
        WebhookStore = new InMemoryWebhookStore();
        WebhookSender = new FakeWebhookSender();
        FileStorage = new InMemoryFileStorage();
        NotificationRuleStore = new InMemoryNotificationRuleStore();
        Profiles = new ProfileService(Registry, Repository, Clock);
        History = new HistoryRecorder(Registry, Repository, Clock);
        var lazyDocuments = new Lazy<DocumentService>(() => Documents!);
        Notifications = new NotificationService(Registry, Repository, lazyDocuments, History);
        Follows = new FollowService(Registry, Repository, lazyDocuments, History);
        Collaboration = new CollaborationHooks(Follows, Notifications, Registry, Repository);
        NamingSeries = new NamingSeriesManager(Registry, Repository, Permissions);
        Documents = new DocumentService(
            Registry, Repository, Permissions, new NamingService(Repository), Clock, new UserPermissionService(Registry, Repository), Shares, Profiles, History, Collaboration, WorkflowCatalog, new Lazy<AssignmentRuleEngine>(() => AssignmentRules!), new Lazy<NotificationRuleEngine>(() => NotificationRules!), new Lazy<WebhookService>(() => Webhooks!), new Lazy<FileService>(() => Files!), new Lazy<DocumentActionEngine>(() => DocumentActions!), new Lazy<ServerScriptEngine>(() => ServerScripts!));
        ShareManager = new ShareManager(Registry, Repository, Documents, Permissions, Shares, History, Notifications);
        Activity = new ActivityService(Registry, Repository, Documents, Permissions, History, Collaboration);
        Assignments = new AssignmentService(Registry, Repository, Documents, Permissions, ShareManager, Notifications, Follows, History);
        AutoRepeat = new AutoRepeatService(Registry, Repository, Documents, Permissions, Notifications, History);
        ListSettings = new ListSettingsService(Registry, Repository, Documents, Permissions, History);
        Bulk = new BulkService(Registry, Documents);
        DocTypeStore = new InMemoryDocTypeStore();
        Schema = new InMemorySchemaManager();
        Designer = new DocTypeDesigner(Registry, DocTypeStore, Schema, Repository, Clock);
        Customization = new CustomizationManager(Registry, CustomizationStore, Schema, Permissions, Clock);
        AssignmentRules = new AssignmentRuleEngine(AssignmentRuleCatalog, Registry, Repository, Assignments, Notifications);
        Print = new PrintService(Registry, Repository, Documents, Permissions, History);
        Dashboards = new DashboardService(Registry, Repository, Documents, Permissions, Notifications, History);
        Reports = new ReportService(Registry, Repository, Documents, Permissions, Notifications, History);
        DataImport = new DataImportService(Registry, Documents, Permissions);
        DocumentActions = new DocumentActionEngine(Registry, Repository, Documents, Clock);
        ServerScripts = new ServerScriptEngine(Registry, Repository, new ScriptSandbox(Documents, Clock), Clock);
        ErrorLog = new ErrorLogService(Documents, new HitCounter(Clock));
        Patches = new List<IPatch>();
        PatchRunner = new PatchRunner(Registry, Repository, Patches, new EmptyServices(), Clock);
        PdfRenderer = new FakePdfRenderer();
        Pdf = new PdfService(Print, PdfRenderer, new HitCounter(Clock));
        Translations = new TranslationService(Registry, Repository);
        WebForms = new WebFormService(Registry, Repository, Documents, new HitCounter(Clock));
        Workspaces = new WorkspaceService(Registry, Repository, Dashboards, Permissions, History);
        EmailSender = new FakeEmailSender();
        Email = new EmailService(Registry, Repository, Documents, Permissions, History, EmailSender);
        NotificationRules = new NotificationRuleEngine(NotificationRuleCatalog, Registry, Repository, Documents, Notifications, Follows, Email, Clock);
        NotificationRuleManager = new NotificationRuleManager(Registry, NotificationRuleStore, NotificationRuleCatalog, Repository, Permissions, Clock);
        AssignmentRuleManager = new AssignmentRuleManager(Registry, AssignmentRuleStore, AssignmentRuleCatalog, Repository, Permissions, Clock);
        Workflows = new WorkflowManager(Registry, WorkflowStore, WorkflowCatalog, Permissions, Clock);
        AuthStore = new InMemoryAuthStore();
        AuthOptions = new AuthOptions { HashIterations = 1000 };
        Auth = new AuthService(
            Registry,
            Repository,
            Documents,
            AuthStore,
            new PasswordHasher<string>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 })),
            Options.Create(AuthOptions),
            Clock);
        TotpStore = new InMemoryTotpStore();
        var protector = new FakeSecretProtector();
        IdentityProvider = new FakeIdentityProvider();
        IdentityStore = new InMemoryIdentityStore();
        SsoOptions = new SsoOptions { Providers = { ["idp"] = new SsoProvider { DisplayName = "Company login", Authority = FakeIdentityProvider.Authority, ClientId = FakeIdentityProvider.ClientId, ClientSecret = "s3cret" } } };
        Sso = new SsoService(Options.Create(SsoOptions), IdentityProvider, IdentityStore, Auth, protector, Clock);
        TwoFactor = new TwoFactorService(TotpStore, protector, AuthStore, Options.Create(AuthOptions), Clock);
        Sessions = new SessionService(new InMemorySessionStore(), Options.Create(AuthOptions), Clock);
        SignUpOptions = new SignUpOptions { Enabled = true, DefaultRoles = ["Clerk"] };
        SignUp = new SignUpService(Auth, protector, EmailSender, new HitCounter(Clock), Options.Create(SignUpOptions), Clock);
        PasswordReset = new PasswordResetService(Auth, AuthStore, protector, Sessions, EmailSender, new HitCounter(Clock), Clock);
        Webhooks = new WebhookService(WebhookCatalog, Registry, Repository, Documents, WebhookSender, History);
        WebhookManager = new WebhookManager(Registry, WebhookStore, WebhookCatalog, Permissions, Clock, allowInsecure: false);
        Bundle = new BundleService(Registry, Designer, Customization, Workflows, AssignmentRuleManager, NotificationRuleManager, Permissions, Clock);
        Backup = new BackupService(Registry, Repository, Bundle, Permissions, Clock);
        Files = new FileService(Registry, Repository, Documents, Permissions, Notifications, FileStorage, History);
        ApiKeys = new ApiKeyService(Registry, Repository, Auth, Permissions, History);
    }

    public MetaRegistry Registry { get; }
    public IDocumentRepository Repository { get; }
    public TestClock Clock { get; }
    public DocumentService Documents { get; }
    public PermissionOverrides Overrides { get; }
    public PermissionService Permissions { get; }
    public InMemoryPermissionStore PermissionStore { get; }
    public PermissionManager PermissionManager { get; }
    public ShareStore Shares { get; }
    public InMemoryCustomizationStore CustomizationStore { get; }
    public CustomizationManager Customization { get; }
    public WorkflowCatalog WorkflowCatalog { get; }
    public InMemoryWorkflowStore WorkflowStore { get; }
    public WorkflowManager Workflows { get; }
    public AutoRepeatService AutoRepeat { get; }
    public AssignmentRuleCatalog AssignmentRuleCatalog { get; }
    public InMemoryAssignmentRuleStore AssignmentRuleStore { get; }
    public AssignmentRuleEngine AssignmentRules { get; }
    public AssignmentRuleManager AssignmentRuleManager { get; }
    public NotificationRuleCatalog NotificationRuleCatalog { get; }
    public InMemoryNotificationRuleStore NotificationRuleStore { get; }
    public NotificationRuleEngine NotificationRules { get; }
    public FakeEmailSender EmailSender { get; }
    public DashboardService Dashboards { get; }
    public ReportService Reports { get; }
    public PrintService Print { get; }
    public WorkspaceService Workspaces { get; }
    public TranslationService Translations { get; }
    public FakeIdentityProvider IdentityProvider { get; }
    public InMemoryIdentityStore IdentityStore { get; }
    public SsoOptions SsoOptions { get; }
    public SsoService Sso { get; }
    public SignUpOptions SignUpOptions { get; }
    public SignUpService SignUp { get; }
    public FakePdfRenderer PdfRenderer { get; }
    public PdfService Pdf { get; }
    public BackupService Backup { get; }
    public List<IPatch> Patches { get; }
    public PatchRunner PatchRunner { get; }
    public ErrorLogService ErrorLog { get; }
    public DocumentActionEngine DocumentActions { get; }
    public ServerScriptEngine ServerScripts { get; }
    public WebFormService WebForms { get; }
    public DataImportService DataImport { get; }
    public ApiKeyService ApiKeys { get; }
    public WebhookCatalog WebhookCatalog { get; }
    public InMemoryWebhookStore WebhookStore { get; }
    public FakeWebhookSender WebhookSender { get; }
    public WebhookService Webhooks { get; }
    public WebhookManager WebhookManager { get; }
    public BundleService Bundle { get; }
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
    public NamingSeriesManager NamingSeries { get; }
    public ShareManager ShareManager { get; }
    public InMemoryDocTypeStore DocTypeStore { get; }
    public InMemorySchemaManager Schema { get; }
    public DocTypeDesigner Designer { get; }
    public IAuthStore AuthStore { get; }
    public InMemoryTotpStore TotpStore { get; }
    public TwoFactorService TwoFactor { get; }
    public SessionService Sessions { get; }
    public PasswordResetService PasswordReset { get; }
    public AuthOptions AuthOptions { get; }
    public AuthService Auth { get; }

    public static Dictionary<string, object?> Values(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}

/// <summary>A service provider that has nothing in it, for code that is handed one but does not need it in a test.</summary>
public sealed class EmptyServices : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
