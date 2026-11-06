using Chakki.Web.Domain;
using Chakki.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Tests;

public class LedgerAndOrderTests
{
    [Fact]
    public async Task Unbalanced_journal_is_rejected()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
            await Assert.ThrowsAsync<UnbalancedJournalException>(() => env.Ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), "bad", "manual", null,
                [new(SystemAccounts.Cash, 100, 0), new(SystemAccounts.GrindingIncome, 0, 90)], TestEnv.User));
    }

    [Fact]
    public async Task Trial_balance_stays_balanced_through_a_full_order()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var o = await env.BilledOrderAsync(20, forgiven: 3);
            await env.Orders.CollectCashAsync(o, 20, TestEnv.User);
            var tb = await env.Ledger.TrialBalanceAsync();
            Assert.Equal(tb.Sum(a => a.Debit), tb.Sum(a => a.Credit));
        }
    }

    [Fact]
    public async Task Forgiven_amount_never_becomes_udhaar()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var o = await env.BilledOrderAsync(10, forgiven: 4); // 10 kg x 2.40 = 24, forgive 4
            Assert.Equal(24m, o.Total);
            Assert.Equal(20m, await env.Ledger.CustomerUdhaarAsync(o.CustomerId));
            Assert.Equal(20m, o.Outstanding);
            Assert.Equal(4m, await env.Ledger.AccountBalanceAsync(SystemAccounts.Goodwill));
        }
    }

    [Fact]
    public async Task Udhaar_of_one_chakki_is_invisible_to_another()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA)) await env.BilledOrderAsync(10);
        env.Db.ChangeTracker.Clear();
        using (env.As(env.OrgB)) Assert.All((await env.Ledger.AllUdhaarAsync()).Values, v => Assert.Equal(0m, v));
    }

    [Theory]
    [InlineData(2.40, null, true)]     // system rate, no reason needed
    [InlineData(2.20, "Regular", true)]
    [InlineData(2.20, null, false)]    // change needs a reason
    [InlineData(1.50, "Too low", false)]
    [InlineData(3.50, "Too high", false)]
    public void Rate_must_stay_in_band_and_changes_need_a_reason(double requested, string? reason, bool ok)
    {
        var rate = new RateCard { Grain = "Wheat", Service = "Atta", SystemRate = 2.40m, MinRate = 2.00m, MaxRate = 3.00m, AllowOverride = true };
        if (ok) Assert.Equal((decimal)requested, OrderService.ValidateRate(rate, (decimal)requested, reason));
        else Assert.Throws<BusinessRuleException>(() => OrderService.ValidateRate(rate, (decimal)requested, reason));
    }

    [Fact]
    public void Locked_rate_cannot_be_changed()
    {
        var rate = new RateCard { Grain = "Wheat", Service = "Atta", SystemRate = 2.40m, MinRate = 2.40m, MaxRate = 2.40m, AllowOverride = false };
        Assert.Throws<BusinessRuleException>(() => OrderService.ValidateRate(rate, 2.30m, "please"));
    }

    [Fact]
    public async Task Cash_cannot_exceed_the_outstanding_amount()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var o = await env.BilledOrderAsync(10);
            await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.CollectCashAsync(o, 25, TestEnv.User));
        }
    }

    [Fact]
    public async Task Udhaar_receipt_settles_old_balance_first_then_oldest_order()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var customer = await env.Db.Customers.FirstAsync();
            var ob = await env.Ops.AddOpeningBalanceAsync(customer.Id, 50, DateOnly.FromDateTime(DateTime.Today.AddMonths(-2)), "old register", TestEnv.User);
            var first = await env.BilledOrderAsync(10);   // 24
            var second = await env.BilledOrderAsync(10);  // 24
            Assert.Equal(98m, await env.Ledger.CustomerUdhaarAsync(customer.Id));

            await env.Orders.ReceiveUdhaarAsync(customer.Id, 60, TestEnv.User);

            Assert.Equal(0m, ob.Remaining);
            Assert.Equal(14m, first.Outstanding);
            Assert.Equal(24m, second.Outstanding);
            Assert.Equal(38m, await env.Ledger.CustomerUdhaarAsync(customer.Id));
            await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.ReceiveUdhaarAsync(customer.Id, 39, TestEnv.User));
        }
    }

    [Fact]
    public async Task Reversal_cancels_a_manual_entry()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA))
        {
            var j = await env.Ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), "Tea", "manual", null,
                [new(SystemAccounts.Cash, 0, 50), new(SystemAccounts.OpeningEquity, 50, 0)], TestEnv.User);
            await env.Db.SaveChangesAsync();
            await env.Ledger.ReverseAsync(j.Id, "typo", TestEnv.User);
            await env.Db.SaveChangesAsync();
            Assert.Equal(0m, await env.Ledger.AccountBalanceAsync(SystemAccounts.Cash));
        }
    }
}
