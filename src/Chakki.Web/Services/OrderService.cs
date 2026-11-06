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
        foreach (var input in lines)
        {
            var rate = await db.RateCards.Include(r => r.TaxRate).FirstOrDefaultAsync(r => r.Id == input.RateCardId) ?? throw new BusinessRuleException("Rate not found.");
            var applied = ValidateRate(rate, input.AppliedRate, input.OverrideReason);
            var tax = rate.TaxRate is { IsActive: true } t ? t : null;
            var (net, taxAmount) = Taxes.Split(Money(input.WeightKg * applied), tax);
            order.Lines.Add(new GrindingOrderLine
            {
                RateCardId = rate.Id, WeightKg = input.WeightKg, SystemRate = rate.SystemRate, AppliedRate = applied,
                OverrideReason = applied != rate.SystemRate ? input.OverrideReason?.Trim() : null,
                Amount = net, TaxAmount = taxAmount, TaxName = tax?.Name, TaxPercent = tax?.Percent ?? 0,
            });
        }
        order.TaxTotal = order.Lines.Sum(l => l.TaxAmount);
        order.Total = order.Lines.Sum(l => l.LineTotal);
        db.GrindingOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
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

    public async Task AdvanceAsync(GrindingOrder order, OrderStatus to)
    {
        var allowed = order.Status switch
        {
            OrderStatus.Intake => new[] { OrderStatus.Grinding },
            OrderStatus.Grinding => [OrderStatus.Ready],
            OrderStatus.Ready => [OrderStatus.Delivered],
            _ => [],
        };
        if (!allowed.Contains(to)) throw new BusinessRuleException($"An order that is {order.Status} cannot move to {to}.");
        if (to == OrderStatus.Cancelled && order.BilledAt != null) throw new BusinessRuleException("A billed order cannot be cancelled; reverse its journal instead.");
        order.Status = to;
        if (to == OrderStatus.Delivered) order.DeliveredAt = DateTime.UtcNow;
        // Whole-order buttons move every line that is behind.
        if (to == OrderStatus.Grinding)
            foreach (var l in order.Lines.Where(l => l.Status == LineStatus.Waiting)) l.Status = LineStatus.Grinding;
        if (to == OrderStatus.Ready)
            foreach (var l in order.Lines.Where(l => l.Status != LineStatus.Done)) { l.Status = LineStatus.Done; l.DoneAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Moves one grain / service on its own (Waiting → Grinding → Done, or back one step to correct a mistake).
    /// The order follows its lines: Received while all wait, Grinding while any is in work, Ready when all are done.
    /// </summary>
    public async Task SetLineStatusAsync(GrindingOrder order, int lineId, LineStatus to, string userId = "")
    {
        if (order.Status is OrderStatus.Delivered or OrderStatus.Cancelled) throw new BusinessRuleException("This order is closed.");
        var line = order.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new KeyNotFoundException();
        if (Math.Abs((int)to - (int)line.Status) > 1 && to != LineStatus.Done)
            throw new BusinessRuleException("Move one step at a time.");
        if (to < line.Status) Audit(order, "line.step_back", $"line {line.Id} {line.Status} → {to}", userId);
        line.Status = to;
        line.DoneAt = to == LineStatus.Done ? DateTime.UtcNow : null;
        order.Status = DeriveStatus(order.Lines);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Goes back one step to correct a mistake: Delivered → Ready → Grinding → Received. A reason is required and logged.
    /// The bill is not touched (use <see cref="UnbillAsync"/>), so money already recorded stays correct.
    /// </summary>
    public async Task StepBackAsync(GrindingOrder order, string reason, string userId)
    {
        reason = RequireReason(reason);
        var from = order.Status;
        switch (order.Status)
        {
            case OrderStatus.Delivered:
                order.Status = OrderStatus.Ready; order.DeliveredAt = null; break;
            case OrderStatus.Ready:
                foreach (var l in order.Lines) { l.Status = LineStatus.Grinding; l.DoneAt = null; }
                order.Status = OrderStatus.Grinding; break;
            case OrderStatus.Grinding:
                foreach (var l in order.Lines) { l.Status = LineStatus.Waiting; l.DoneAt = null; }
                order.Status = OrderStatus.Intake; break;
            default:
                throw new BusinessRuleException("There is no earlier step.");
        }
        Audit(order, "order.step_back", $"{from} → {order.Status}: {reason}", userId);
        await db.SaveChangesAsync();
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
        if (order.Status is OrderStatus.Delivered) throw new BusinessRuleException("This order is already handed over. Undo the hand-over first.");
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
        lines.All(l => l.Status == LineStatus.Done) ? OrderStatus.Ready
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

    public async Task<Payment> CollectCashAsync(GrindingOrder order, decimal amount, string userId)
    {
        EnsurePayable(order, amount);
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

    public static void EnsurePayable(GrindingOrder order, decimal amount)
    {
        if (order.BilledAt == null) throw new BusinessRuleException("Bill the order before taking payment.");
        if (amount <= 0) throw new BusinessRuleException("Amount must be more than zero.");
        if (Money(amount) > order.Outstanding) throw new BusinessRuleException($"Amount cannot exceed the outstanding ₹{order.Outstanding:0.00}.");
    }

    /// <summary>Dr Cash/Bank, Cr Receivable; updates the order. Idempotent: a settled payment is never settled twice.</summary>
    public async Task SettleAsync(GrindingOrder order, Payment payment, string? providerPaymentId, string? userId)
    {
        if (payment.Status == SettlementStatus.Settled) return;
        if (payment.Amount > order.Outstanding) throw new BusinessRuleException("Payment is larger than what is outstanding.");
        var debit = payment.Method == PaymentMethod.Cash ? SystemAccounts.Cash : SystemAccounts.Bank;
        var journal = await ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), $"Payment {payment.Reference} for {order.Ref}", "payment", order.Id,
        [
            new(debit, payment.Amount, 0),
            new(SystemAccounts.Receivable, 0, payment.Amount, order.CustomerId),
        ], userId);
        payment.Status = SettlementStatus.Settled;
        payment.SettledAt = DateTime.UtcNow;
        payment.ProviderPaymentId ??= providerPaymentId;
        payment.JournalEntry = journal;
        order.Paid += payment.Amount;
        RefreshPaymentState(order);
        if (order.PaymentState == PaymentState.Paid && order.Status == OrderStatus.Ready)
        {
            order.Status = OrderStatus.Delivered;
            order.DeliveredAt = DateTime.UtcNow;
        }
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

        var remaining = amount;
        var openings = (await db.OpeningBalances.Where(o => o.CustomerId == customerId).OrderBy(o => o.AsOf).ThenBy(o => o.Id).ToListAsync())
            .Where(o => o.Remaining > 0);
        foreach (var ob in openings)
        {
            var take = Math.Min(ob.Remaining, remaining);
            ob.Settled += take;
            remaining -= take;
            if (remaining == 0) break;
        }
        if (remaining > 0)
        {
            var orders = (await db.GrindingOrders.Where(o => o.CustomerId == customerId && o.BilledAt != null).OrderBy(o => o.BilledAt).ToListAsync())
                .Where(o => o.Outstanding > 0);
            foreach (var order in orders)
            {
                var take = Math.Min(order.Outstanding, remaining);
                order.Paid += take;
                RefreshPaymentState(order);
                db.Payments.Add(new Payment
                {
                    GrindingOrderId = order.Id, CustomerId = customerId, Method = PaymentMethod.Cash, Amount = take,
                    Status = SettlementStatus.Settled, SettledAt = DateTime.UtcNow, Reference = PaymentReferences.New("UDH"), RecordedById = userId,
                });
                remaining -= take;
                if (remaining == 0) break;
            }
        }
        await ledger.PostAsync(DateOnly.FromDateTime(DateTime.Today), "Udhaar receipt", "udhaar", customerId,
        [
            new(SystemAccounts.Cash, amount, 0),
            new(SystemAccounts.Receivable, 0, amount, customerId),
        ], userId);
        await db.SaveChangesAsync();
        return amount;
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
