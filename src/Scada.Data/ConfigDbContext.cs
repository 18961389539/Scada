using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Scada.Core.Entities;
using Scada.Core.Scripting;
using Scada.Data.Infrastructure;

namespace Scada.Data;

public class ConfigDbContext : DbContext
{
    public ConfigDbContext(DbContextOptions<ConfigDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsSqlGenerator, ScadaSqliteMigrationsSqlGenerator>();
        optionsBuilder.AddInterceptors(new SqliteSchemaCommandInterceptor());
    }

    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AcquisitionChannel> Channels => Set<AcquisitionChannel>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagDependency> TagDependencies => Set<TagDependency>();
    public DbSet<AlarmRule> AlarmRules => Set<AlarmRule>();
    public DbSet<AlarmShelve> AlarmShelves => Set<AlarmShelve>();
    public DbSet<AlarmAnalyticsConfig> AlarmAnalyticsConfigs => Set<AlarmAnalyticsConfig>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationRoute> NotificationRoutes => Set<NotificationRoute>();
    public DbSet<DutyShift> DutyShifts => Set<DutyShift>();
    public DbSet<CalcJob> CalcJobs => Set<CalcJob>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<ConfigVersion> ConfigVersions => Set<ConfigVersion>();
    public DbSet<CalcJobPublication> CalcJobPublications => Set<CalcJobPublication>();
    public DbSet<ScriptLibrary> ScriptLibraries => Set<ScriptLibrary>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<ProcessScreen> ProcessScreens => Set<ProcessScreen>();
    public DbSet<ProcessScreenVersion> ProcessScreenVersions => Set<ProcessScreenVersion>();
    public DbSet<HmiLibraryItem> HmiLibrary => Set<HmiLibraryItem>();
    public DbSet<HmiLayout> HmiLayouts => Set<HmiLayout>();
    public DbSet<HmiLayoutVersion> HmiLayoutVersions => Set<HmiLayoutVersion>();
    public DbSet<HmiWorkstation> HmiWorkstations => Set<HmiWorkstation>();
    public DbSet<DeviceObjectTemplate> ObjectTemplates => Set<DeviceObjectTemplate>();
    public DbSet<DeviceObjectTemplateVersion> ObjectTemplateVersions => Set<DeviceObjectTemplateVersion>();
    public DbSet<DeviceObjectInstance> ObjectInstances => Set<DeviceObjectInstance>();
    public DbSet<AcquisitionGateway> Gateways => Set<AcquisitionGateway>();
    public DbSet<GatewayConfigurationRevision> GatewayConfigurations => Set<GatewayConfigurationRevision>();
    public DbSet<ControlPoint> ControlPoints => Set<ControlPoint>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<MaintenanceWorkOrder> MaintenanceWorkOrders => Set<MaintenanceWorkOrder>();
    public DbSet<SparePart> SpareParts => Set<SparePart>();
    public DbSet<SparePartMovement> SparePartMovements => Set<SparePartMovement>();
    public DbSet<SpcChart> SpcCharts => Set<SpcChart>();
    public DbSet<QualityDefectCode> QualityDefectCodes => Set<QualityDefectCode>();
    public DbSet<ControlSequence> ControlSequences => Set<ControlSequence>();
    public DbSet<RecipeTemplate> RecipeTemplates => Set<RecipeTemplate>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeVersion> RecipeVersions => Set<RecipeVersion>();

    public DbSet<ProductionLine> ProductionLines => Set<ProductionLine>();
    public DbSet<ProductionProduct> ProductionProducts => Set<ProductionProduct>();
    public DbSet<ProductionShift> ProductionShifts => Set<ProductionShift>();
    public DbSet<DowntimeReason> DowntimeReasons => Set<DowntimeReason>();
    public DbSet<OeeConfig> OeeConfigs => Set<OeeConfig>();

    public DbSet<AnnunciatorPanel> AnnunciatorPanels => Set<AnnunciatorPanel>();
    public DbSet<AnnunciatorOutput> AnnunciatorOutputs => Set<AnnunciatorOutput>();
    public DbSet<AnnunciatorLevelMap> AnnunciatorLevelMaps => Set<AnnunciatorLevelMap>();

    public DbSet<ExternalApiKey> ExternalApiKeys => Set<ExternalApiKey>();

    // 审计发件箱：与业务写入同事务落库，再异步搬到运行库的 OperationLog（见 AuditInterceptor）。
    public DbSet<AuditOutboxEntry> AuditOutbox => Set<AuditOutboxEntry>();

    public DbSet<TraceabilityJob> TraceabilityJobs => Set<TraceabilityJob>();

    public DbSet<SerialProfileConfig> SerialProfiles => Set<SerialProfileConfig>();

    public DbSet<SiteOperationsSettings> SiteOperationsSettings => Set<SiteOperationsSettings>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) { AssignKeys(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) { AssignKeys(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }

    // 保存前补齐报警规则稳定键：新增或修改时 Key 为空就生成一个（API、模板实例化、初始化数据都不必关心）。
    // 同时归一化位报警触发条件，保证初始化数据、工程包和接口写入的语义一致。
    private void AssignKeys()
    {
        foreach (var entry in ChangeTracker.Entries<AlarmRule>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            if (string.IsNullOrWhiteSpace(entry.Entity.Key)) entry.Entity.Key = Guid.NewGuid().ToString("N");
            entry.Entity.ActiveWhen = Scada.Core.Models.AlarmRuleText.NormalizeActiveWhen(entry.Entity.Type, entry.Entity.ActiveWhen);
            // 自动优先级在落库前统一计算，避免接口、模板、工程包各自算一套。
            entry.Entity.ApplyAutoPriority();
        }
    }

    // 长度约定：HasMaxLength(...) 在 SQLite 上只是 EF 模型注解，不会生成 CHECK 约束。
    // Sn / Code / UserName 等字段的上限目前由接口层校验（EndpointHelpers 及各端点），
    // 外部 API、网关上报、工程包导入这些旁路必须各自校验，不能指望数据库兜底。
    // 真要数据库兜底，得用 ToTable(t => t.HasCheckConstraint(...)) 并配套迁移，只加属性无效。
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 程序集里同时放着 Config 与 Runtime 两组配置类；必须按命名空间过滤，
        // 否则另一个 DbContext 的实体会被一起建进当前模型（凭空多出几十张表）。
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ConfigDbContext).Assembly,
            type => type.Namespace == "Scada.Data.Configurations.Config");
    }
}
