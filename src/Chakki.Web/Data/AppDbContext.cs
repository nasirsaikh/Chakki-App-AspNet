using System.Linq.Expressions;
using System.Reflection;
using Chakki.Web.Domain;
using Chakki.Web.Tenancy;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Chakki.Web.Data;

/// <summary>
/// Shared-database, shared-schema multi-tenancy with three guards:
/// 1. a global query filter on every <see cref="ITenantOwned"/> entity (reads only ever see the current chakki);
/// 2. SaveChanges stamps new rows with the current chakki and refuses to write rows that belong to another one;
/// 3. SaveChanges checks every foreign key to a tenant-owned row points at a row of the same chakki
///    (so a forged CustomerId from another chakki is rejected even if a controller forgot to look it up).
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : IdentityDbContext<AppUser>(options)
{
    public Guid CurrentOrganizationId => tenant.OrganizationId ?? Guid.Empty;
    public ITenantContext Tenant => tenant;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<OpeningBalance> OpeningBalances => Set<OpeningBalance>();
    public DbSet<RateCard> RateCards => Set<RateCard>();
    public DbSet<GrindingOrder> GrindingOrders => Set<GrindingOrder>();
    public DbSet<GrindingOrderLine> GrindingOrderLines => Set<GrindingOrderLine>();
    public DbSet<PaymentGateway> PaymentGateways => Set<PaymentGateway>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<LedgerLine> LedgerLines => Set<LedgerLine>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<Buyback> Buybacks => Set<Buyback>();
    public DbSet<ProductionRun> ProductionRuns => Set<ProductionRun>();
    public DbSet<WastageLog> WastageLogs => Set<WastageLog>();
    public DbSet<UtilityExpense> UtilityExpenses => Set<UtilityExpense>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Attendance> Attendances => Set<Attendance>();
    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();
    public DbSet<MaintenanceTask> MaintenanceTasks => Set<MaintenanceTask>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Organization>().HasIndex(o => o.Slug).IsUnique();
        b.Entity<AppUser>().HasIndex(u => u.OrganizationId);
        b.Entity<AppUser>().HasOne<Organization>().WithMany().HasForeignKey(u => u.OrganizationId).OnDelete(DeleteBehavior.Restrict);

        // Uniqueness is always per chakki.
        b.Entity<GrindingOrder>().HasIndex(o => new { o.OrganizationId, o.Number }).IsUnique();
        b.Entity<JournalEntry>().HasIndex(j => new { j.OrganizationId, j.Number }).IsUnique();
        b.Entity<Account>().HasIndex(a => new { a.OrganizationId, a.Code }).IsUnique();
        b.Entity<Attendance>().HasIndex(a => new { a.OrganizationId, a.EmployeeId, a.Date }).IsUnique();
        b.Entity<Customer>().HasIndex(c => new { c.OrganizationId, c.Name });
        b.Entity<LedgerLine>().HasIndex(l => new { l.OrganizationId, l.AccountId });
        b.Entity<LedgerLine>().HasIndex(l => new { l.OrganizationId, l.CustomerId });
        // Payment references are global (webhooks arrive without a tenant) and unguessable.
        b.Entity<Payment>().HasIndex(p => p.Reference).IsUnique();

        b.Entity<GrindingOrder>().HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.GrindingOrderId);
        b.Entity<GrindingOrder>().HasMany(o => o.Payments).WithOne(p => p.GrindingOrder).HasForeignKey(p => p.GrindingOrderId);
        b.Entity<Payment>().HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<JournalEntry>().HasMany(j => j.Lines).WithOne(l => l.JournalEntry).HasForeignKey(l => l.JournalEntryId);
        b.Entity<LedgerLine>().HasOne<Customer>().WithMany().HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LedgerLine>().HasOne<Supplier>().WithMany().HasForeignKey(l => l.SupplierId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ChatSession>().HasMany(s => s.Messages).WithOne().HasForeignKey(m => m.ChatSessionId);
        b.Entity<ProductionRun>().HasOne(p => p.InputItem).WithMany().HasForeignKey(p => p.InputItemId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProductionRun>().HasOne(p => p.OutputItem).WithMany().HasForeignKey(p => p.OutputItemId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ProductionRun>().HasOne(p => p.ByProductItem).WithMany().HasForeignKey(p => p.ByProductItemId).OnDelete(DeleteBehavior.Restrict);

        foreach (var entity in b.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entity.ClrType)) continue;
            entity.AddIndex(entity.FindProperty(nameof(ITenantOwned.OrganizationId))!);
            typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entity.ClrType).Invoke(this, [b]);
            // Every tenant row hangs off an organisation.
            b.Entity(entity.ClrType).HasOne(typeof(Organization)).WithMany().HasForeignKey(nameof(ITenantOwned.OrganizationId)).OnDelete(DeleteBehavior.Restrict);
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned
    {
        Expression<Func<T, bool>> filter = e => e.OrganizationId == CurrentOrganizationId;
        b.Entity<T>().HasQueryFilter(filter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardTenantWritesAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        await GuardTenantWritesAsync(ct);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private async Task GuardTenantWritesAsync(CancellationToken ct)
    {
        var entries = ChangeTracker.Entries<ITenantOwned>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (entries.Count == 0) return;

        var org = tenant.OrganizationId ?? throw new CrossTenantWriteException("No organisation is selected for this write.");
        foreach (var entry in entries)
        {
            if (entry.State == EntityState.Added && entry.Entity.OrganizationId == Guid.Empty)
                entry.Entity.OrganizationId = org;
            if (entry.Entity.OrganizationId != org)
                throw new CrossTenantWriteException($"{entry.Metadata.ClrType.Name} belongs to another organisation.");
            if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.OrganizationId)).IsModified &&
                (Guid)entry.Property(nameof(ITenantOwned.OrganizationId)).OriginalValue! != org)
                throw new CrossTenantWriteException("Rows cannot move between organisations.");
        }

        foreach (var entry in entries.Where(e => e.State != EntityState.Deleted))
            await GuardForeignKeysAsync(entry, org, ct);
    }

    private async Task GuardForeignKeysAsync(EntityEntry entry, Guid org, CancellationToken ct)
    {
        foreach (var fk in entry.Metadata.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType.ClrType;
            if (!typeof(TenantEntity).IsAssignableFrom(principal) || fk.Properties.Count != 1) continue;
            var value = entry.Property(fk.Properties[0].Name).CurrentValue;
            if (value is not int id || id <= 0) continue;
            if (entry.State == EntityState.Modified && !entry.Property(fk.Properties[0].Name).IsModified) continue;

            var tracked = ChangeTracker.Entries().FirstOrDefault(e => e.Metadata.ClrType == principal && e.Entity is TenantEntity t && t.Id == id);
            Guid owner;
            if (tracked != null) owner = ((TenantEntity)tracked.Entity).OrganizationId;
            else
            {
                var method = typeof(AppDbContext).GetMethod(nameof(OwnerOfAsync), BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(principal);
                owner = await (Task<Guid>)method.Invoke(this, [id, ct])!;
            }
            if (owner != org)
                throw new CrossTenantWriteException($"{entry.Metadata.ClrType.Name}.{fk.Properties[0].Name} points at a record of another organisation.");
        }
    }

    private Task<Guid> OwnerOfAsync<T>(int id, CancellationToken ct) where T : TenantEntity =>
        Set<T>().IgnoreQueryFilters().Where(e => e.Id == id).Select(e => e.OrganizationId).FirstOrDefaultAsync(ct);
}
