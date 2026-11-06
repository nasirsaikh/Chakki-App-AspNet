using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.EntityFrameworkCore;
using static Chakki.Web.Services.OrderService;

namespace Chakki.Web.Services;

/// <summary>Stock, purchases, sales, buyback, production, wastage, utilities, payroll and maintenance — each with its posting.</summary>
public class OperationsService(AppDbContext db, LedgerService ledger)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private async Task<InventoryItem> ItemAsync(int id) => await db.InventoryItems.FindAsync(id) ?? throw new BusinessRuleException("Item not found.");

    private void Move(InventoryItem item, MovementKind kind, decimal qty, decimal unitCost, string? note, DateOnly date)
    {
        if (qty > 0)
        {
            var value = item.Quantity * item.AvgCost + qty * unitCost;
            item.Quantity += qty;
            item.AvgCost = item.Quantity == 0 ? 0 : Math.Round(value / item.Quantity, 4);
        }
        else
        {
            if (item.Quantity + qty < 0) throw new BusinessRuleException($"Only {item.Quantity:0.##} {item.Unit} of {item.Name} in stock.");
            item.Quantity += qty;
        }
        db.StockMovements.Add(new StockMovement { ItemId = item.Id, Kind = kind, Quantity = qty, UnitCost = unitCost, Note = note, Date = date });
    }

    public async Task PurchaseAsync(int supplierId, int itemId, decimal qty, decimal rate, bool paidNow, DateOnly? date, string userId)
    {
        if (qty <= 0 || rate < 0) throw new BusinessRuleException("Quantity and rate must be positive.");
        var supplier = await db.Suppliers.FindAsync(supplierId) ?? throw new BusinessRuleException("Supplier not found.");
        var item = await ItemAsync(itemId);
        var d = date ?? Today;
        var amount = Money(qty * rate);
        var purchase = new Purchase { SupplierId = supplier.Id, ItemId = item.Id, Quantity = qty, Rate = rate, Amount = amount, PaidNow = paidNow, Date = d };
        db.Purchases.Add(purchase);
        Move(item, MovementKind.Purchase, qty, rate, $"From {supplier.Name}", d);
        await ledger.PostAsync(d, $"Purchase of {qty:0.##} {item.Unit} {item.Name} from {supplier.Name}", "purchase", null,
        [
            new(SystemAccounts.Inventory, amount, 0),
            paidNow ? new(SystemAccounts.Cash, 0, amount) : new(SystemAccounts.Payable, 0, amount, SupplierId: supplier.Id),
        ], userId);
        await db.SaveChangesAsync();
    }

    public async Task PaySupplierAsync(int supplierId, decimal amount, string? note, string userId)
    {
        var supplier = await db.Suppliers.FindAsync(supplierId) ?? throw new BusinessRuleException("Supplier not found.");
        amount = Money(amount);
        var owed = await ledger.SupplierPayableAsync(supplier.Id);
        if (amount <= 0 || amount > owed) throw new BusinessRuleException($"Amount must be between ₹0 and the payable ₹{owed:0.00}.");
        db.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, Amount = amount, Date = Today, Note = note });
        await ledger.PostAsync(Today, $"Payment to {supplier.Name}", "supplier-payment", supplier.Id,
        [
            new(SystemAccounts.Payable, amount, 0, SupplierId: supplier.Id),
            new(SystemAccounts.Cash, 0, amount),
        ], userId);
        await db.SaveChangesAsync();
    }

    /// <summary>Retail sale: Dr Cash/Receivable, Cr Sales Income; Dr COGS, Cr Inventory at average cost.</summary>
    public async Task SellAsync(int itemId, decimal qty, decimal? rate, int? customerId, bool onUdhaar, string userId)
    {
        if (qty <= 0) throw new BusinessRuleException("Quantity must be positive.");
        var item = await db.InventoryItems.Include(i => i.TaxRate).FirstOrDefaultAsync(i => i.Id == itemId) ?? throw new BusinessRuleException("Item not found.");
        Customer? customer = null;
        if (customerId != null) customer = await db.Customers.FindAsync(customerId) ?? throw new BusinessRuleException("Customer not found.");
        if (onUdhaar && customer == null) throw new BusinessRuleException("Choose the customer for an Udhaar sale.");
        var price = rate ?? item.SalePrice;
        var tax = item.TaxRate is { IsActive: true } t ? t : null;
        var (amount, taxAmount) = Taxes.Split(Money(qty * price), tax);
        var gross = amount + taxAmount;
        var cost = Money(qty * item.AvgCost);
        Move(item, MovementKind.Sale, -qty, item.AvgCost, customer?.Name, Today);
        db.Sales.Add(new Sale { ItemId = item.Id, CustomerId = customer?.Id, Quantity = qty, Rate = price, Amount = amount, TaxAmount = taxAmount,
            TaxName = tax?.Name, TaxPercent = tax?.Percent ?? 0, CostAmount = cost, OnUdhaar = onUdhaar, Date = Today });
        List<PostLine> lines =
        [
            onUdhaar ? new(SystemAccounts.Receivable, gross, 0, customer!.Id) : new(SystemAccounts.Cash, gross, 0),
            new(SystemAccounts.SalesIncome, 0, amount),
            new(SystemAccounts.TaxPayable, 0, taxAmount),
        ];
        if (cost > 0) lines.AddRange([new(SystemAccounts.Cogs, cost, 0), new(SystemAccounts.Inventory, 0, cost)]);
        await ledger.PostAsync(Today, $"Sale of {qty:0.##} {item.Unit} {item.Name}", "sale", null, lines, userId);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Customer sells by-product (e.g. Khal) to the chakki. The value first settles the linked order's fee; the rest is paid in cash.
    ///   Dr Inventory (value), Cr Buyback Payable (value); Dr Buyback Payable (fee), Cr Receivable (fee); Dr Buyback Payable (rest), Cr Cash (rest).
    /// </summary>
    public async Task<Buyback> BuybackAsync(int customerId, int itemId, decimal qty, decimal? appliedRate, string? reason, int? orderId, string userId)
    {
        if (qty <= 0) throw new BusinessRuleException("Quantity must be positive.");
        var customer = await db.Customers.FindAsync(customerId) ?? throw new BusinessRuleException("Customer not found.");
        var item = await ItemAsync(itemId);
        var rate = appliedRate ?? item.BuybackRate;
        if (rate <= 0) throw new BusinessRuleException("Set a buyback rate for this item first.");
        if (rate != item.BuybackRate && string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("A reason is required when the buyback rate is changed.");
        var value = Money(qty * rate);

        GrindingOrder? order = null;
        decimal fee = 0;
        if (orderId != null)
        {
            order = await db.GrindingOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customer.Id) ?? throw new BusinessRuleException("Order not found for this customer.");
            if (order.BilledAt == null) throw new BusinessRuleException("Bill the order before adjusting it against buyback.");
            fee = Math.Min(order.Outstanding, value);
        }
        var cash = value - fee;
        var bb = new Buyback
        {
            CustomerId = customer.Id, ItemId = item.Id, GrindingOrderId = order?.Id, Quantity = qty, SystemRate = item.BuybackRate, AppliedRate = rate,
            OverrideReason = rate != item.BuybackRate ? reason : null, Value = value, FeeAdjusted = fee, CashPaid = cash, Date = Today,
        };
        db.Buybacks.Add(bb);
        Move(item, MovementKind.Buyback, qty, rate, $"Buyback from {customer.Name}", Today);

        List<PostLine> lines = [new(SystemAccounts.Inventory, value, 0), new(SystemAccounts.BuybackPayable, 0, value)];
        if (fee > 0) lines.AddRange([new(SystemAccounts.BuybackPayable, fee, 0), new(SystemAccounts.Receivable, 0, fee, customer.Id)]);
        if (cash > 0) lines.AddRange([new(SystemAccounts.BuybackPayable, cash, 0), new(SystemAccounts.Cash, 0, cash)]);
        await ledger.PostAsync(Today, $"Buyback {qty:0.##} {item.Unit} {item.Name} from {customer.Name}", "buyback", null, lines, userId);

        if (order != null && fee > 0)
        {
            db.Payments.Add(new Payment
            {
                GrindingOrderId = order.Id, CustomerId = customer.Id, Method = PaymentMethod.Buyback, Amount = fee, Status = SettlementStatus.Settled,
                SettledAt = DateTime.UtcNow, Reference = PaymentReferences.New("BB"), RecordedById = userId,
            });
            order.Paid += fee;
            RefreshPaymentState(order);
        }
        await db.SaveChangesAsync();
        return bb;
    }

    /// <summary>Raw input becomes output + by-product; the input's cost is carried into the outputs so stock value is preserved.</summary>
    public async Task ProduceAsync(int inputId, decimal inputQty, int outputId, decimal outputQty, int? byId, decimal byQty)
    {
        if (inputQty <= 0 || outputQty <= 0 || byQty < 0) throw new BusinessRuleException("Quantities must be positive.");
        if (outputQty + byQty > inputQty) throw new BusinessRuleException("Output cannot be more than the input.");
        var input = await ItemAsync(inputId);
        var output = await ItemAsync(outputId);
        var by = byId != null ? await ItemAsync(byId.Value) : null;
        var cost = input.AvgCost * inputQty;
        var producedQty = outputQty + (by != null ? byQty : 0);
        var unitCost = producedQty == 0 ? 0 : cost / producedQty;
        Move(input, MovementKind.ProductionOut, -inputQty, input.AvgCost, "Production", Today);
        Move(output, MovementKind.ProductionIn, outputQty, unitCost, "Production", Today);
        if (by != null && byQty > 0) Move(by, MovementKind.ProductionIn, byQty, unitCost, "Production by-product", Today);
        var loss = inputQty - outputQty - (by != null ? byQty : 0);
        db.ProductionRuns.Add(new ProductionRun
        {
            InputItemId = input.Id, InputQty = inputQty, OutputItemId = output.Id, OutputQty = outputQty, ByProductItemId = by?.Id, ByProductQty = by != null ? byQty : 0,
            LossQty = loss, YieldPercent = Math.Round(outputQty / inputQty * 100, 2), Date = Today,
        });
        await db.SaveChangesAsync();
    }

    public async Task WastageAsync(int? itemId, string kind, decimal qty, string? note, string userId)
    {
        if (qty <= 0) throw new BusinessRuleException("Quantity must be positive.");
        decimal cost = 0;
        if (itemId != null)
        {
            var item = await ItemAsync(itemId.Value);
            cost = Money(qty * item.AvgCost);
            Move(item, MovementKind.Wastage, -qty, item.AvgCost, kind, Today);
            if (cost > 0)
                await ledger.PostAsync(Today, $"Wastage: {qty:0.##} {item.Unit} {item.Name} ({kind})", "wastage", null,
                    [new(SystemAccounts.Wastage, cost, 0), new(SystemAccounts.Inventory, 0, cost)], userId);
        }
        db.WastageLogs.Add(new WastageLog { ItemId = itemId, Kind = kind, Quantity = qty, CostAmount = cost, Note = note, Date = Today });
        await db.SaveChangesAsync();
    }

    public async Task UtilityAsync(string kind, decimal amount, decimal? units, string? note, DateOnly? date, string userId)
    {
        amount = Money(amount);
        if (amount <= 0) throw new BusinessRuleException("Amount must be positive.");
        var d = date ?? Today;
        db.UtilityExpenses.Add(new UtilityExpense { Kind = kind, Amount = amount, Units = units, Note = note, Date = d });
        await ledger.PostAsync(d, $"{kind} expense", "utility", null, [new(SystemAccounts.Utility, amount, 0), new(SystemAccounts.Cash, 0, amount)], userId);
        await db.SaveChangesAsync();
    }

    /// <summary>Daily-wage: present days × day rate. Monthly: salary × worked days ÷ days in period (half day = 0.5).</summary>
    public async Task<PayrollRun> RunPayrollAsync(int employeeId, DateOnly from, DateOnly to, string userId)
    {
        if (to < from) throw new BusinessRuleException("Period end is before its start.");
        var emp = await db.Employees.FindAsync(employeeId) ?? throw new BusinessRuleException("Employee not found.");
        if (await db.PayrollRuns.AnyAsync(p => p.EmployeeId == emp.Id && p.PeriodStart <= to && p.PeriodEnd >= from))
            throw new BusinessRuleException("Payroll already exists for part of this period.");
        var att = await db.Attendances.Where(a => a.EmployeeId == emp.Id && a.Date >= from && a.Date <= to).ToListAsync();
        var days = att.Sum(a => a.Status switch { AttendanceStatus.Present => 1m, AttendanceStatus.HalfDay => 0.5m, _ => 0m });
        var periodDays = to.DayNumber - from.DayNumber + 1;
        var gross = Money(emp.WageType == WageType.Daily ? days * emp.WageRate : emp.WageRate * days / periodDays);
        var run = new PayrollRun { EmployeeId = emp.Id, PeriodStart = from, PeriodEnd = to, DaysWorked = days, Gross = gross };
        db.PayrollRuns.Add(run);
        if (gross > 0)
        {
            var j = await ledger.PostAsync(to, $"Wages {emp.Name} {from:dd MMM}–{to:dd MMM}", "payroll", emp.Id,
                [new(SystemAccounts.Wages, gross, 0), new(SystemAccounts.Cash, 0, gross)], userId);
            await db.SaveChangesAsync();
            run.JournalEntryId = j.Id;
        }
        await db.SaveChangesAsync();
        return run;
    }

    public async Task CompleteMaintenanceAsync(int taskId, decimal cost, string? note, string userId)
    {
        var task = await db.MaintenanceTasks.FindAsync(taskId) ?? throw new BusinessRuleException("Task not found.");
        if (task.Status == MaintenanceStatus.Done) throw new BusinessRuleException("Already completed.");
        task.Status = MaintenanceStatus.Done;
        task.DoneOn = Today;
        task.Cost = Money(cost);
        task.Note = note ?? task.Note;
        if (task.Cost > 0)
            await ledger.PostAsync(Today, $"Maintenance: {task.Title}", "maintenance", task.Id,
                [new(SystemAccounts.Maintenance, task.Cost, 0), new(SystemAccounts.Cash, 0, task.Cost)], userId);
        if (task.RepeatDays is > 0)
            db.MaintenanceTasks.Add(new MaintenanceTask { Title = task.Title, Kind = task.Kind, DueOn = Today.AddDays(task.RepeatDays.Value), RepeatDays = task.RepeatDays });
        await db.SaveChangesAsync();
    }

    public async Task<OpeningBalance> AddOpeningBalanceAsync(int customerId, decimal amount, DateOnly asOf, string? note, string userId)
    {
        var customer = await db.Customers.FindAsync(customerId) ?? throw new BusinessRuleException("Customer not found.");
        amount = Money(amount);
        if (amount <= 0) throw new BusinessRuleException("Amount must be positive.");
        var ob = new OpeningBalance { CustomerId = customer.Id, Amount = amount, AsOf = asOf, Note = note };
        db.OpeningBalances.Add(ob);
        var j = await ledger.PostAsync(asOf, $"Old Udhaar of {customer.Name}", "opening", customer.Id,
            [new(SystemAccounts.Receivable, amount, 0, customer.Id), new(SystemAccounts.OpeningEquity, 0, amount)], userId);
        await db.SaveChangesAsync();
        ob.JournalEntryId = j.Id;
        await db.SaveChangesAsync();
        return ob;
    }
}
