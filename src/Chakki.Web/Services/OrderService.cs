using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Services;

public record OrderLineInput(int RateCardId, decimal WeightKg, decimal? AppliedRate, string? OverrideReason);

public class BusinessRuleException(string message) : InvalidOperationException(message);

public class OrderService(AppDbContext db, LedgerService ledger)
{
    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public async Task<GrindingOrder> CreateAsync(int customerId, IEnumerable<OrderLineInput> inputs, string? notes, string userId)
    {
        var customer = await db.Customers.FindAsync(customerId) ?? throw new BusinessRuleException("Customer not found.");
        var lines = inputs.Where(i => i.WeightKg > 0).ToList();
        if (lines.Count == 0) throw new BusinessRuleException("Add at least one grain with a weight.");

        var org = await db.Organizations.SingleAsync(o => o.Id == db.CurrentOrganizationId);
        var order = new GrindingOrder { Number = org.NextOrderNo++, CustomerId = customer.Id, Notes = notes, CreatedById = userId };
        order.Lines.AddRange(await BuildLinesAsync(lines));
        order.TaxTotal = order.Lines.Sum(l => l.TaxAmount);
        order.Total = order.Lines.Sum(l => l.LineTotal);
        db.GrindingOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<List<GrindingOrderLine>> BuildLinesAsync(IEnumerable<OrderLineInput> inputs)
    {
        var list = new List<GrindingOrderLine>();
        foreach (var input in inputs.Where(i => i.WeightKg > 0))
        {
            var rate = await db.RateCards.Include(r => r.TaxRate).FirstOrDefaultAsync(r => r.Id == input.RateCardId) ?? throw new BusinessRuleException("Rate not found.");
            var applied = ValidateRate(rate, input.AppliedRate, input.OverrideReason);
            var tax = rate.TaxRate is { IsActive: true } t ? t : null;
            var (net, taxAmount) = Taxes.Split(Money(input.WeightKg * applied), tax);
            list.Add(new GrindingOrderLine
            {
                RateCardId = rate.Id, WeightKg = input.WeightKg, SystemRate = rate.SystemRate, AppliedRate = applied,
                OverrideReason = applied != rate.SystemRate ? input.OverrideReason?.Trim() : null,
                Amount = net, TaxAmount = taxAmount, TaxName = tax?.Name, TaxPercent = tax?.Percent ?? 0,
            });
        }
        if (list.Count == 0) throw new BusinessRuleException("Add at least one grain with a weight.");
        return list;
    }

    /// <summary>Applied rate must stay inside the configured band; any change needs a reason.</summary>
    public static decimal ValidateRate(RateCard rate, decimal? requested, string? reason)
    {
        var applied = requested ?? rate.SystemRate;
        if (applied == rate.SystemRate) return applied;
        if (!rate.AllowOverride) throw new BusinessRuleException($"The rate for {rate.Label} cannot be changed.");
        if (applied < rate.MinRate || applied > rate.MaxRate)
            throw new BusinessRuleException($"Rate for {rate.Label} must be between ₹{rate.MinRate} and ₹{rate.MaxRate}.");
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("A reason is required when the rate is changed.");
        return applied;
    }

    /// <summary>Whole-order buttons: start every waiting grain, finish every grain, or hand over every finished grain.</summary>
    public async Task AdvanceAsync(GrindingOrder order, OrderStatus to)
    {
        if (order.Status == OrderStatus.Cancelled) throw new BusinessRuleException("This order is cancelled.");
        var now = DateTime.UtcNow;
        switch (to)
        {
            case OrderStatus.Grinding:
                foreach (var l in order.Lines.Where(l => l.Status == LineStatus.Waiting)) l.Status = LineStatus.Grinding;
                break;
            case OrderStatus.Ready:
                foreach (var l in order.Lines.Where(l => l.Status < LineStatus.Done)) { l.Status = LineStatus.Done; l.DoneAt = now; }
                break;
            case OrderStatus.Delivered:
                var ready = order.Lines.Where(l => l.Status == LineStatus.Done).ToList();
                if (ready.Count == 0) throw new BusinessRuleException("Nothing is ready to hand over yet.");
                foreach (var l in ready) { l.Status = LineStatus.Delivered; l.DeliveredAt = now; }
                break;
            default:
                throw new BusinessRuleException($"An order cannot move to {to} this way.");
        }
        Sync(order);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Moves one grain / service on its own: Waiting → Grinding → Done → Delivered (handed over), or back a step to fix a mistake.
    /// Ready grains can be handed over while others are still being ground.
    /// </summary>
    public async Task SetLineStatusAsync(GrindingOrder order, int lineId, LineStatus to, string userId = "")
    {
        if (order.Status is OrderStatus.Cancelled) throw new BusinessRuleException("This order is cancelled.");
        var line = order.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new KeyNotFoundException();
        var forwardOk = to == line.Status + 1 || (line.Status == LineStatus.Waiting && to == LineStatus.Done);
        var backOk = to < line.Status;
        if (!forwardOk && !backOk) throw new BusinessRuleException("This step is not possible from here.");
        if (backOk) Audit(order, "line.step_back", $"line {line.Id} {line.Status} → {to}", userId);
        ApplyLine(line, to, DateTime.UtcNow);
        Sync(order);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Goes back to any earlier step (Delivered, Ready, Grinding or Received) to correct a mistake. A reason is required and logged.
    /// The bill and payments are not touched (use <see cref="UnbillAsync"/>), so money already recorded stays correct.
    /// </summary>
    public async Task StepBackToAsync(GrindingOrder order, OrderStatus target, string reason, string userId)
    {
        reason = RequireReason(reason);
        if (order.Status == OrderStatus.Cancelled) throw new BusinessRuleException("This order is cancelled.");
        if (target >= order.Status || target == OrderStatus.Cancelled) throw new BusinessRuleException("Choose an earlier step.");
        var cap = target switch { OrderStatus.Intake => LineStatus.Waiting, OrderStatus.Grinding => LineStatus.Grinding, _ => LineStatus.Done };
        var from = order.Status;
        foreach (var l in order.Lines.Where(l => l.Status > cap)) ApplyLine(l, cap, DateTime.UtcNow);
        Sync(order);
        Audit(order, "order.step_back", $"{from} → {order.Status}: {reason}", userId);
        await db.SaveChangesAsync();
    }

    public Task StepBackAsync(GrindingOrder order, string reason, string userId) =>
        order.Status == OrderStatus.Intake ? throw new BusinessRuleException("There is no earlier step.")
            : StepBackToAsync(order, order.Status - 1, reason, userId);

    /// <summary>
    /// Changes the customer, grains, weights or rates of an order sent back to Received. Only before billing and buyback,
    /// so nothing in the books has to change.
    /// </summary>
    public async Task UpdateAsync(GrindingOrder order, int customerId, IEnumerable<OrderLineInput> inputs, string? notes, string userId)
    {
        if (order.Status != OrderStatus.Intake) throw new BusinessRuleException("Go back to Received before changing the order.");
        if (order.BilledAt != null) throw new BusinessRuleException("Remove the bill before changing the order.");
        if (await db.Buybacks.AnyAsync(b => b.GrindingOrderId == order.Id)) throw new BusinessRuleException("This order has a buyback; it cannot be changed.");
        var customer = await db.Customers.FindAsync(customerId) ?? throw new BusinessRuleException("Customer not found.");
        var lines = await BuildLinesAsync(inputs);
        db.GrindingOrderLines.RemoveRange(order.Lines);
        order.Lines.Clear();
        order.Lines.AddRange(lines);
        order.CustomerId = customer.Id;
        order.Notes = notes;
        order.TaxTotal = order.Lines.Sum(l => l.TaxAmount);
        order.Total = order.Lines.Sum(l => l.LineTotal);
        Audit(order, "order.edit", $"{lines.Count} line(s), total {order.Total:0.00}", userId);
        await db.SaveChangesAsync();
    }

    private static void ApplyLine(GrindingOrderLine l, LineStatus to, DateTime now)
    {
        l.Status = to;
        l.DoneAt = to >= LineStatus.Done ? l.DoneAt ?? now : null;
        l.DeliveredAt = to == LineStatus.Delivered ? l.DeliveredAt ?? now : null;
    }

    /// <summary>The order status always follows its lines.</summary>
    private static void Sync(GrindingOrder order)
    {
        order.Status = DeriveStatus(order.Lines);
        order.DeliveredAt = order.Status == OrderStatus.Delivered ? order.Lines.Max(l => l.DeliveredAt) : null;
    }

    /// <summary>
    /// Removes a bill made by mistake: the bill journal is reversed (never deleted) and the order can be billed again.
    /// Only while nothing has been paid on it; pending QR / card requests are expired.
    /// </summary>
    public async Task UnbillAsync(GrindingOrder order, string reason, string userId)
    {
        reason = RequireReason(reason);
        if (order.BilledAt == null) throw new BusinessRuleException("This order is not billed.");
        if (order.Paid > 0 || order.Payments.Any(p => p.Status == SettlementStatus.Settled))
            throw new BusinessRuleException("Money has already been received on this bill. Return or reverse the payment first.");
        var bill = await db.JournalEntries.Where(j => j.Source == "order" && j.SourceId == order.Id && !j.IsReversed && j.ReversesId == null)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync();
        if (bill != null) await ledger.ReverseAsync(bill.Id, $"Bill {order.Ref} undone: {reason}", userId);
        foreach (var p in order.Payments.Where(p => p.Status == SettlementStatus.Pending)) p.Status = SettlementStatus.Expired;
        order.BilledAt = null;
        order.Forgiven = 0;
        order.PaymentState = PaymentState.Unbilled;
        Audit(order, "order.unbill", reason, userId);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Cancels an order that will not be collected (customer left, duplicate, wrong entry) at any stage before hand-over.
    /// A bill with no money on it is reversed first; an order with money received cannot be cancelled.
    /// </summary>
    public async Task CancelAsync(GrindingOrder order, string reason, string userId)
    {
        reason = RequireReason(reason);
        if (order.Lines.Any(l => l.Status == LineStatus.Delivered)) throw new BusinessRuleException("Some grain is already handed over. Undo the hand-over first.");
        if (order.Status is OrderStatus.Cancelled) throw new BusinessRuleException("This order is already cancelled.");
        if (order.BilledAt != null) await UnbillAsync(order, reason, userId);
        foreach (var p in order.Payments.Where(p => p.Status == SettlementStatus.Pending)) p.Status = SettlementStatus.Expired;
        order.Status = OrderStatus.Cancelled;
        order.CancelledAt = DateTime.UtcNow;
        order.CancelReason = reason;
        Audit(order, "order.cancel", reason, userId);
        await db.SaveChangesAsync();
    }

    private static string RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("Write a short reason so others know what happened.");
        return reason.Trim().Length > 200 ? reason.Trim()[..200] : reason.Trim();
    }

    private void Audit(GrindingOrder order, string action, string detail, string userId) =>
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = action, Detail = $"{order.Ref}: {detail}"[..Math.Min(300, order.Ref.Length + 2 + detail.Length)] });

    public static OrderStatus DeriveStatus(IReadOnlyCollection<GrindingOrderLine> lines) =>
        lines.Count == 0 ? OrderStatus.Intake
        : lines.All(l => l.Status == LineStatus.Delivered) ? OrderStatus.Delivered
        : lines.All(l => l.Status >= LineStatus.Done) ? OrderStatus.Ready
        : lines.All(l => l.Status == LineStatus.Waiting) ? OrderStatus.Intake
        : OrderStatus.Grinding;

    /// <summary>
    /// Bills the order. Forgiveness is decided at billing so it never enters Udhaar:
    ///   Dr Receivable (total − forgiven), Dr Goodwill (forgiven), Cr Grinding Income (total).
    /// </summary>
    public async Task BillAsync(GrindingOrder order, decimal forgiven, string userId)
    {
        if (order.BilledAt != null) throw new BusinessRuleException("This order is already billed.");
        if (order.Status == OrderStatus.Cancelled) throw new BusinessRuleException("A cancelled order cannot be billed.");
        forgiven = Money(forgiven);
        if (forgiven < 0 || forgiven > order.Total) throw new BusinessRuleException("Forgiven amount must be between 0 and the bill total.");
        order.Forgiven = forgiven;
        order.BilledAt = DateTime.UtcNow;
        await ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), $"Grinding bill {order.Ref}", "order", order.Id,
        [
            new(SystemAccounts.Receivable, order.Total - forgiven, 0, order.CustomerId),
            new(SystemAccounts.Goodwill, forgiven, 0),
            new(SystemAccounts.GrindingIncome, 0, order.Total - order.TaxTotal),
            new(SystemAccounts.TaxPayable, 0, order.TaxTotal),
        ], userId);
        RefreshPaymentState(order);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Cash for this order. The amount may be part of the bill (e.g. one grain), the whole bill, or more than the bill
    /// up to the customer's total Udhaar: the extra clears Old Udhaar first, then the oldest other bills.
    /// </summary>
    public async Task<Payment> CollectCashAsync(GrindingOrder order, decimal amount, string userId)
    {
        await EnsurePayableAsync(order, amount);
        var payment = new Payment
        {
            GrindingOrderId = order.Id, CustomerId = order.CustomerId, Method = PaymentMethod.Cash, Amount = Money(amount),
            Reference = PaymentReferences.New("CASH"), RecordedById = userId,
        };
        db.Payments.Add(payment);
        await SettleAsync(order, payment, null, userId);
        await db.SaveChangesAsync();
        return payment;
    }

    /// <summary>Most the customer can pay through this order: its outstanding plus any other Udhaar they owe.</summary>
    public async Task<decimal> MaxPayableAsync(GrindingOrder order) =>
        Math.Max(order.Outstanding, await ledger.CustomerUdhaarAsync(order.CustomerId));

    public async Task EnsurePayableAsync(GrindingOrder order, decimal amount)
    {
        if (order.BilledAt == null) throw new BusinessRuleException("Bill the order before taking payment.");
        if (amount <= 0) throw new BusinessRuleException("Amount must be more than zero.");
        var max = await MaxPayableAsync(order);
        if (Money(amount) > max) throw new BusinessRuleException($"Amount cannot exceed what the customer owes in total, ₹{max:0.00}.");
    }

    /// <summary>
    /// Dr Cash/Bank, Cr Receivable. The part up to this bill's outstanding goes to this order; any extra clears the
    /// customer's other Udhaar (FIFO). Idempotent: a settled payment is never settled twice.
    /// </summary>
    public async Task SettleAsync(GrindingOrder order, Payment payment, string? providerPaymentId, string? userId)
    {
        if (payment.Status == SettlementStatus.Settled) return;
        var toThisOrder = Math.Min(payment.Amount, Math.Max(0, order.Outstanding));
        var extra = payment.Amount - toThisOrder;
        if (extra > 0)
        {
            var otherUdhaar = await ledger.CustomerUdhaarAsync(order.CustomerId) - order.Outstanding;
            if (extra > otherUdhaar) throw new BusinessRuleException("Payment is larger than what the customer owes.");
        }
        var debit = payment.Method == PaymentMethod.Cash ? SystemAccounts.Cash : SystemAccounts.Bank;
        var memo = extra > 0 ? $"Payment {payment.Reference} for {order.Ref} + old Udhaar {extra:0.00}" : $"Payment {payment.Reference} for {order.Ref}";
        var journal = await ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), memo, "payment", order.Id,
        [
            new(debit, payment.Amount, 0),
            new(SystemAccounts.Receivable, 0, payment.Amount, order.CustomerId),
        ], userId);
        payment.Status = SettlementStatus.Settled;
        payment.SettledAt = DateTime.UtcNow;
        payment.ProviderPaymentId ??= providerPaymentId;
        payment.JournalEntry = journal;
        payment.OldUdhaarPart = extra;
        order.Paid += toThisOrder;
        RefreshPaymentState(order);
        if (extra > 0) await AllocateUdhaarAsync(order.CustomerId, extra, payment.Method, userId, order.Id);
    }

    public static void RefreshPaymentState(GrindingOrder order) =>
        order.PaymentState = order.BilledAt == null ? PaymentState.Unbilled
            : order.Outstanding <= 0 ? PaymentState.Paid
            : order.Paid > 0 ? PaymentState.Partial : PaymentState.Unpaid;

    /// <summary>
    /// Udhaar receipt against a customer: settles Old Udhaar (opening balances) first, then the oldest unpaid orders.
    /// </summary>
    public async Task<decimal> ReceiveUdhaarAsync(int customerId, decimal amount, string userId)
    {
        amount = Money(amount);
        if (amount <= 0) throw new BusinessRuleException("Amount must be more than zero.");
        var owed = await ledger.CustomerUdhaarAsync(customerId);
        if (amount > owed) throw new BusinessRuleException($"Amount cannot exceed the Udhaar of ₹{owed:0.00}.");
        await AllocateUdhaarAsync(customerId, amount, PaymentMethod.Cash, userId, null);
        await ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), "Udhaar receipt", "udhaar", customerId,
        [
            new(SystemAccounts.Cash, amount, 0),
            new(SystemAccounts.Receivable, 0, amount, customerId),
        ], userId);
        await db.SaveChangesAsync();
        return amount;
    }

    /// <summary>FIFO: opening balances (Old Udhaar) oldest first, then the oldest billed orders. Posts nothing; the caller posts the journal.</summary>
    private async Task AllocateUdhaarAsync(int customerId, decimal amount, PaymentMethod method, string? userId, int? skipOrderId)
    {
        var remaining = amount;
        var openings = (await db.OpeningBalances.Where(o => o.CustomerId == customerId).OrderBy(o => o.AsOf).ThenBy(o => o.Id).ToListAsync())
            .Where(o => o.Remaining > 0);
        foreach (var ob in openings)
        {
            var take = Math.Min(ob.Remaining, remaining);
            ob.Settled += take;
            remaining -= take;
            if (remaining == 0) return;
        }
        var orders = (await db.GrindingOrders.Where(o => o.CustomerId == customerId && o.BilledAt != null && o.Id != skipOrderId).OrderBy(o => o.BilledAt).ToListAsync())
            .Where(o => o.Outstanding > 0);
        foreach (var order in orders)
        {
            var take = Math.Min(order.Outstanding, remaining);
            order.Paid += take;
            RefreshPaymentState(order);
            db.Payments.Add(new Payment
            {
                GrindingOrderId = order.Id, CustomerId = customerId, Method = method, Amount = take, Status = SettlementStatus.Settled,
                SettledAt = DateTime.UtcNow, Reference = PaymentReferences.New("UDH"), RecordedById = userId,
            });
            remaining -= take;
            if (remaining == 0) return;
        }
    }
}

/// <summary>Tax arithmetic, kept in one place. Works for any tax the chakki sets up (GST, VAT…).</summary>
public static class Taxes
{
    /// <summary>Splits a priced amount into (taxable value, tax). Exclusive tax is added on top; inclusive tax is carved out.</summary>
    public static (decimal Net, decimal Tax) Split(decimal priced, TaxRate? tax)
    {
        if (tax == null || tax.Percent <= 0) return (priced, 0);
        if (tax.Inclusive)
        {
            var t = OrderService.Money(priced * tax.Percent / (100 + tax.Percent));
            return (priced - t, t);
        }
        return (priced, OrderService.Money(priced * tax.Percent / 100));
    }

    /// <summary>The two halves shown on a bill (e.g. CGST and SGST); the second takes any odd paisa.</summary>
    public static (decimal A, decimal B) Halves(decimal tax)
    {
        var a = Math.Floor(tax * 100 / 2) / 100;
        return (a, tax - a);
    }
}

public static class PaymentReferences
{
    public static string New(string prefix) =>
        $"{prefix}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10))}";
}
