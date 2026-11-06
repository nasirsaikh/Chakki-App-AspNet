using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Services;
using Chakki.Web.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chakki.Tests;

/// <summary>A real SQLite database in memory with two chakkis, A and B.</summary>
public sealed class TestEnv : IDisposable
{
    private readonly SqliteConnection _conn = new("DataSource=:memory:");
    public FixedTenantContext Tenant { get; } = new();
    public AppDbContext Db { get; }
    public LedgerService Ledger { get; }
    public OrderService Orders { get; }
    public OperationsService Ops { get; }
    public SecretProtector Secrets { get; } = new(new EphemeralDataProtectionProvider());
    public PaymentService Payments { get; }
    public Guid OrgA { get; private set; }
    public Guid OrgB { get; private set; }
    public const string User = "test-user";

    private TestEnv()
    {
        _conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options, Tenant);
        Db.Database.EnsureCreated();
        Ledger = new LedgerService(Db);
        Orders = new OrderService(Db, Ledger);
        Ops = new OperationsService(Db, Ledger);
        Payments = new PaymentService(Db, Orders, Secrets, new NoHttp(), NullLogger<PaymentService>.Instance);
    }

    public static async Task<TestEnv> CreateAsync()
    {
        var env = new TestEnv();
        env.OrgA = await env.AddOrgAsync("Chakki A");
        env.OrgB = await env.AddOrgAsync("Chakki B");
        return env;
    }

    private async Task<Guid> AddOrgAsync(string name)
    {
        var org = new Organization { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-') };
        Db.Organizations.Add(org);
        await Db.SaveChangesAsync();
        using (Tenant.Use(org.Id))
        {
            await LedgerService.SeedAccountsAsync(Db, org.Id);
            Db.RateCards.Add(new RateCard { Grain = "Wheat", Service = "Atta", SystemRate = 2.40m, MinRate = 2.00m, MaxRate = 3.00m, AllowOverride = true });
            Db.Customers.Add(new Customer { Name = $"{name} customer", Village = "Rampur" });
            await Db.SaveChangesAsync();
        }
        return org.Id;
    }

    public IDisposable As(Guid org) => Tenant.Use(org);

    public async Task<GrindingOrder> BilledOrderAsync(decimal kg, decimal forgiven = 0)
    {
        var rate = await Db.RateCards.FirstAsync();
        var customer = await Db.Customers.FirstAsync();
        var order = await Orders.CreateAsync(customer.Id, [new(rate.Id, kg, null, null)], null, User);
        await Orders.AdvanceAsync(order, OrderStatus.Grinding);
        await Orders.AdvanceAsync(order, OrderStatus.Ready);
        await Orders.BillAsync(order, forgiven, User);
        return order;
    }

    public void Dispose() { Db.Dispose(); _conn.Dispose(); }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network in tests.");
    }
}
