using Chakki.Web.Domain;
using Chakki.Web.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Tests;

public class TenantIsolationTests
{
    [Fact]
    public async Task Queries_only_return_the_current_chakkis_rows()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var names = await env.Db.Customers.Select(c => c.Name).ToListAsync();
            Assert.Equal(["Chakki A customer"], names);
            Assert.Equal(1, await env.Db.RateCards.CountAsync());
        }
        using (env.As(env.OrgB))
            Assert.Equal(["Chakki B customer"], await env.Db.Customers.Select(c => c.Name).ToListAsync());
    }

    [Fact]
    public async Task No_tenant_means_no_rows()
    {
        using var env = await TestEnv.CreateAsync();
        Assert.Empty(await env.Db.Customers.ToListAsync());
    }

    [Fact]
    public async Task Find_by_id_of_another_chakkis_row_returns_nothing()
    {
        using var env = await TestEnv.CreateAsync();
        int bCustomer;
        using (env.As(env.OrgB)) bCustomer = (await env.Db.Customers.FirstAsync()).Id;
        env.Db.ChangeTracker.Clear();
        using (env.As(env.OrgA))
            Assert.Null(await env.Db.Customers.FirstOrDefaultAsync(c => c.Id == bCustomer));
    }

    [Fact]
    public async Task New_rows_are_stamped_with_the_current_chakki()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var c = new Customer { Name = "New" };
            env.Db.Customers.Add(c);
            await env.Db.SaveChangesAsync();
            Assert.Equal(env.OrgA, c.OrganizationId);
        }
    }

    [Fact]
    public async Task Writing_a_row_for_another_chakki_is_rejected()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            env.Db.Customers.Add(new Customer { Name = "Sneaky", OrganizationId = env.OrgB });
            await Assert.ThrowsAnyAsync<CrossTenantWriteException>(() => env.Db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Referencing_another_chakkis_customer_is_rejected()
    {
        using var env = await TestEnv.CreateAsync();
        int bCustomer;
        using (env.As(env.OrgB)) bCustomer = (await env.Db.Customers.FirstAsync()).Id;
        env.Db.ChangeTracker.Clear();
        using (env.As(env.OrgA))
        {
            env.Db.OpeningBalances.Add(new OpeningBalance { CustomerId = bCustomer, Amount = 100, AsOf = DateOnly.FromDateTime(DateTime.Today) });
            await Assert.ThrowsAnyAsync<CrossTenantWriteException>(() => env.Db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Changing_a_rows_chakki_is_rejected()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var c = await env.Db.Customers.FirstAsync();
            c.OrganizationId = env.OrgB;
            await Assert.ThrowsAnyAsync<CrossTenantWriteException>(() => env.Db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task Order_numbers_are_per_chakki()
    {
        using var env = await TestEnv.CreateAsync();
        GrindingOrder a, b;
        using (env.As(env.OrgA)) a = await env.BilledOrderAsync(10);
        using (env.As(env.OrgB)) b = await env.BilledOrderAsync(10);
        Assert.Equal(a.Number, b.Number);
    }
}
