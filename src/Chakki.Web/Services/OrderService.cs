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
            var rate = await db.RateCards.FindAsync(input.RateCardId) ?? throw new BusinessRuleException("Rate not found.");
            var applied = ValidateRate(rate, input.AppliedRate, input.OverrideReason);
            order.Lines.Add(new GrindingOrderLine
            {
                RateCardId = rate.Id, WeightKg = input.WeightKg, SystemRate = rate.SystemRate, AppliedRate = applied,
                OverrideReason = applied != rate.SystemRate ? input.OverrideReason?.Trim() : null,
                Amount = Money(input.WeightKg * applied),
            });
        }
        order.Total = order.Lines.Sum(l => l.Amount);
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
            OrderStatus.Intake => new[] { OrderStatus.Grinding, OrderStatus.Cancelled },
            OrderStatus.Grinding => [OrderStatus.Ready],
            OrderStatus.Ready => [OrderStatus.Delivered],
            _ => [],
        };
        if (!allowed.Contains(to)) throw new BusinessRuleException($"An order that is {order.Status} cannot move to {to}.");
        if (to == OrderStatus.Cancelled && order.BilledAt != null) throw new BusinessRuleException("A billed order cannot be cancelled; reverse its journal instead.");
        order.Status = to;
        if (to == OrderStatus.Delivered) order.DeliveredAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

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
            new(SystemAccounts.GrindingIncome, 0, order.Total),
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

public static class PaymentReferences
{
    public static string New(string prefix) =>
        $"{prefix}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(10))}";
}
