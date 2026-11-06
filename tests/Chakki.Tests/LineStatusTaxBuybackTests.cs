using Chakki.Web.Domain;
using Chakki.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Tests;

public class LineStatusTaxBuybackTests
{
    private static async Task<(GrindingOrder Order, int Wheat, int Mustard)> WheatAndMustardAsync(TestEnv env)
    {
        var wheat = await env.Db.RateCards.FirstAsync();
        var mustard = new RateCard { Grain = "Mustard", Service = "Oil extraction", SystemRate = 5m, MinRate = 4m, MaxRate = 6m };
        env.Db.RateCards.Add(mustard);
        await env.Db.SaveChangesAsync();
        var customer = await env.Db.Customers.FirstAsync();
        // Wheat 25 kg × 2.40 = 60, mustard 15 kg × 5 = 75 → 135 (the example from the chakki owner).
        var order = await env.Orders.CreateAsync(customer.Id, [new(wheat.Id, 25, null, null), new(mustard.Id, 15, null, null)], null, TestEnv.User);
        return (order, wheat.Id, mustard.Id);
    }

    [Fact]
    public async Task Each_line_is_ground_on_its_own_and_the_order_follows()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        Assert.Equal(135m, order.Total);
        var wheatLine = order.Lines[0];
        var mustardLine = order.Lines[1];

        await env.Orders.SetLineStatusAsync(order, wheatLine.Id, LineStatus.Grinding);
        Assert.Equal(OrderStatus.Grinding, order.Status);
        await env.Orders.SetLineStatusAsync(order, wheatLine.Id, LineStatus.Done);
        Assert.Equal(OrderStatus.Grinding, order.Status); // mustard still waiting
        Assert.NotNull(wheatLine.DoneAt);
        await env.Orders.SetLineStatusAsync(order, mustardLine.Id, LineStatus.Done);
        Assert.Equal(OrderStatus.Ready, order.Status);
    }

    [Fact]
    public async Task Whole_order_button_moves_every_line()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        await env.Orders.AdvanceAsync(order, OrderStatus.Grinding);
        await env.Orders.AdvanceAsync(order, OrderStatus.Ready);
        Assert.All(order.Lines, l => Assert.Equal(LineStatus.Done, l.Status));
    }

    [Fact]
    public async Task Buyback_value_pays_the_whole_bill_and_the_rest_is_returned()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        await env.Orders.BillAsync(order, 0, TestEnv.User);
        var khal = new InventoryItem { Name = "Khal", Unit = "kg", BuybackRate = 20 };
        env.Db.InventoryItems.Add(khal);
        await env.Db.SaveChangesAsync();
        var cashBefore = await env.Ledger.AccountBalanceAsync(SystemAccounts.Cash);

        var bb = await env.Ops.BuybackAsync(order.CustomerId, khal.Id, 10, null, null, order.Id, TestEnv.User);

        Assert.Equal(200m, bb.Value);
        Assert.Equal(135m, bb.FeeAdjusted);
        Assert.Equal(65m, bb.CashPaid);
        Assert.Equal(0m, order.Outstanding);
        Assert.Equal(PaymentState.Paid, order.PaymentState);
        Assert.Equal(cashBefore - 65m, await env.Ledger.AccountBalanceAsync(SystemAccounts.Cash));
        Assert.Equal(0m, await env.Ledger.CustomerUdhaarAsync(order.CustomerId));
    }

    [Theory]
    [InlineData(false, 60, 3, 63)]       // 5% added on top
    [InlineData(true, 57.14, 2.86, 60)]  // 5% included in the rate
    public async Task Tax_on_a_rate_is_added_or_carved_out(bool inclusive, double net, double tax, double total)
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var gst = new TaxRate { Name = "GST", Percent = 5, Inclusive = inclusive, SplitInHalves = true, HalfLabelA = "CGST", HalfLabelB = "SGST" };
        env.Db.TaxRates.Add(gst);
        await env.Db.SaveChangesAsync();
        var wheat = await env.Db.RateCards.FirstAsync();
        wheat.TaxRateId = gst.Id;
        await env.Db.SaveChangesAsync();
        env.Db.ChangeTracker.Clear();

        var o = await env.BilledOrderAsync(25); // 25 × 2.40 = 60
        Assert.Equal((decimal)net, o.Lines[0].Amount);
        Assert.Equal((decimal)tax, o.TaxTotal);
        Assert.Equal((decimal)total, o.Total);
        Assert.Equal(-(decimal)tax, await env.Ledger.AccountBalanceAsync(SystemAccounts.TaxPayable)); // credit balance
        Assert.Equal(-(decimal)net, await env.Ledger.AccountBalanceAsync(SystemAccounts.GrindingIncome));
        Assert.Equal((decimal)total, await env.Ledger.CustomerUdhaarAsync(o.CustomerId));
    }

    [Fact]
    public async Task No_tax_configured_means_no_tax()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var o = await env.BilledOrderAsync(25);
        Assert.Equal(0m, o.TaxTotal);
        Assert.Equal(60m, o.Total);
        Assert.Equal(0m, await env.Ledger.AccountBalanceAsync(SystemAccounts.TaxPayable));
    }

    [Fact]
    public void Halves_keep_the_odd_paisa()
    {
        Assert.Equal((1.43m, 1.43m), Taxes.Halves(2.86m));
        Assert.Equal((1.50m, 1.51m), Taxes.Halves(3.01m));
    }

    [Fact]
    public async Task Taxes_of_one_chakki_are_invisible_to_another()
    {
        using var env = await TestEnv.CreateAsync();
        using (env.As(env.OrgA)) { env.Db.TaxRates.Add(new TaxRate { Name = "GST", Percent = 5 }); await env.Db.SaveChangesAsync(); }
        env.Db.ChangeTracker.Clear();
        using (env.As(env.OrgB)) Assert.Empty(await env.Db.TaxRates.ToListAsync());
    }

    [Fact]
    public async Task Step_back_undoes_ready_and_hand_over()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        await env.Orders.AdvanceAsync(order, OrderStatus.Grinding);
        await env.Orders.AdvanceAsync(order, OrderStatus.Ready);
        await env.Orders.AdvanceAsync(order, OrderStatus.Delivered);

        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.StepBackAsync(order, " ", TestEnv.User)); // reason required
        await env.Orders.StepBackAsync(order, "handed to wrong person", TestEnv.User);
        Assert.Equal(OrderStatus.Ready, order.Status);
        Assert.Null(order.DeliveredAt);
        await env.Orders.StepBackAsync(order, "mustard not finished", TestEnv.User);
        Assert.Equal(OrderStatus.Grinding, order.Status);
        Assert.All(order.Lines, l => Assert.Equal(LineStatus.Grinding, l.Status));
        Assert.Equal(2, await env.Db.AuditLogs.CountAsync(a => a.Action == "order.step_back"));
    }

    [Fact]
    public async Task Bill_can_be_removed_only_before_money_is_received()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var o = await env.BilledOrderAsync(25);
        await env.Orders.UnbillAsync(o, "wrong weight", TestEnv.User);
        Assert.Null(o.BilledAt);
        Assert.Equal(0m, await env.Ledger.CustomerUdhaarAsync(o.CustomerId));
        Assert.Equal(0m, await env.Ledger.AccountBalanceAsync(SystemAccounts.GrindingIncome));

        await env.Orders.BillAsync(o, 0, TestEnv.User); // billed again after the fix
        await env.Orders.CollectCashAsync(o, 10, TestEnv.User);
        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.UnbillAsync(o, "oops", TestEnv.User));
    }

    [Fact]
    public async Task Customer_left_order_is_cancelled_with_reason_and_books_cleared()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var o = await env.BilledOrderAsync(25); // billed, nothing paid
        await env.Orders.CancelAsync(o, "Customer left / went to another chakki", TestEnv.User);
        Assert.Equal(OrderStatus.Cancelled, o.Status);
        Assert.Equal("Customer left / went to another chakki", o.CancelReason);
        Assert.Equal(0m, await env.Ledger.CustomerUdhaarAsync(o.CustomerId));
        var tb = await env.Ledger.TrialBalanceAsync();
        Assert.Equal(tb.Sum(a => a.Debit), tb.Sum(a => a.Credit));
    }

    [Fact]
    public async Task Order_with_money_received_cannot_be_cancelled()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var o = await env.BilledOrderAsync(25);
        await env.Orders.CollectCashAsync(o, 20, TestEnv.User);
        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.CancelAsync(o, "left", TestEnv.User));
    }
}
