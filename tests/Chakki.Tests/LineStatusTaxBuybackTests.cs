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

        var bb = await env.Ops.BuybackAsync(order.CustomerId, khal.Id, 10, null, null, order.Id, TestEnv.User, order.Lines[1].Id); // Khal from the mustard

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

    [Fact]
    public async Task Buyback_is_optional_and_cannot_weigh_more_than_its_grain()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        await env.Orders.BillAsync(order, 0, TestEnv.User);
        var khal = new InventoryItem { Name = "Khal", Unit = "kg", BuybackRate = 2 };
        env.Db.InventoryItems.Add(khal);
        await env.Db.SaveChangesAsync();
        var mustard = order.Lines[1]; // 15 kg

        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Ops.BuybackAsync(order.CustomerId, khal.Id, 16, null, null, order.Id, TestEnv.User, mustard.Id));
        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Ops.BuybackAsync(order.CustomerId, khal.Id, 5, null, null, order.Id, TestEnv.User)); // two grains: say which
        await env.Ops.BuybackAsync(order.CustomerId, khal.Id, 10, null, null, order.Id, TestEnv.User, mustard.Id);
        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Ops.BuybackAsync(order.CustomerId, khal.Id, 6, null, null, order.Id, TestEnv.User, mustard.Id)); // 10 + 6 > 15
        await env.Ops.BuybackAsync(order.CustomerId, khal.Id, 5, null, null, order.Id, TestEnv.User, mustard.Id); // exactly 15
        Assert.Equal(135m - 30m, order.Outstanding);
    }

    [Fact]
    public async Task Ready_grain_is_handed_over_now_and_the_rest_later()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        var wheat = order.Lines[0];
        var mustard = order.Lines[1];
        await env.Orders.SetLineStatusAsync(order, wheat.Id, LineStatus.Done);
        await env.Orders.SetLineStatusAsync(order, mustard.Id, LineStatus.Grinding);

        await env.Orders.AdvanceAsync(order, OrderStatus.Delivered); // hands over only the wheat
        Assert.Equal(LineStatus.Delivered, wheat.Status);
        Assert.NotNull(wheat.DeliveredAt);
        Assert.Equal(LineStatus.Grinding, mustard.Status);
        Assert.Equal(OrderStatus.Grinding, order.Status);
        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.AdvanceAsync(order, OrderStatus.Delivered)); // nothing ready

        await env.Orders.SetLineStatusAsync(order, mustard.Id, LineStatus.Done);
        Assert.Equal(OrderStatus.Ready, order.Status);
        await env.Orders.SetLineStatusAsync(order, mustard.Id, LineStatus.Delivered);
        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.NotNull(order.DeliveredAt);

        await env.Orders.SetLineStatusAsync(order, mustard.Id, LineStatus.Done, TestEnv.User); // undo one hand-over
        Assert.Equal(OrderStatus.Ready, order.Status);
        Assert.Null(order.DeliveredAt);
    }

    [Fact]
    public async Task Pay_for_one_grain_now_and_the_other_later()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, _, _) = await WheatAndMustardAsync(env);
        await env.Orders.BillAsync(order, 0, TestEnv.User);
        await env.Orders.CollectCashAsync(order, 60, TestEnv.User); // wheat only
        Assert.Equal(PaymentState.Partial, order.PaymentState);
        Assert.Equal(75m, order.Outstanding);
        await env.Orders.CollectCashAsync(order, 75, TestEnv.User); // mustard, on the second visit
        Assert.Equal(PaymentState.Paid, order.PaymentState);
        Assert.Equal(0m, await env.Ledger.CustomerUdhaarAsync(order.CustomerId));
    }

    [Fact]
    public async Task Paying_bill_plus_old_udhaar_clears_both()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var old = await env.BilledOrderAsync(25);   // ₹60 left unpaid earlier
        var (order, _, _) = await WheatAndMustardAsync(env);
        Assert.Equal(old.CustomerId, order.CustomerId);
        await env.Orders.BillAsync(order, 0, TestEnv.User); // ₹135 today

        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.CollectCashAsync(order, 196, TestEnv.User)); // more than 195 owed
        var p = await env.Orders.CollectCashAsync(order, 195, TestEnv.User);
        Assert.Equal(60m, p.OldUdhaarPart);
        Assert.Equal(0m, order.Outstanding);
        var reloaded = await env.Db.GrindingOrders.FirstAsync(o => o.Id == old.Id);
        Assert.Equal(0m, reloaded.Outstanding);
        Assert.Equal(0m, await env.Ledger.CustomerUdhaarAsync(order.CustomerId));
        var tb = await env.Ledger.TrialBalanceAsync();
        Assert.Equal(tb.Sum(a => a.Debit), tb.Sum(a => a.Credit));
    }

    [Fact]
    public async Task Going_back_to_received_lets_the_order_be_changed()
    {
        using var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var (order, wheatRate, _) = await WheatAndMustardAsync(env);
        await env.Orders.AdvanceAsync(order, OrderStatus.Ready);
        await env.Orders.AdvanceAsync(order, OrderStatus.Delivered);

        await env.Orders.StepBackToAsync(order, OrderStatus.Intake, "wrong weight", TestEnv.User);
        Assert.Equal(OrderStatus.Intake, order.Status);
        Assert.All(order.Lines, l => Assert.Equal(LineStatus.Waiting, l.Status));

        await env.Orders.UpdateAsync(order, order.CustomerId, [new(wheatRate, 30, null, null)], "fixed", TestEnv.User);
        Assert.Single(order.Lines);
        Assert.Equal(72m, order.Total); // 30 × 2.40

        await env.Orders.BillAsync(order, 0, TestEnv.User);
        await Assert.ThrowsAsync<BusinessRuleException>(() => env.Orders.UpdateAsync(order, order.CustomerId, [new(wheatRate, 20, null, null)], null, TestEnv.User));
    }
}
