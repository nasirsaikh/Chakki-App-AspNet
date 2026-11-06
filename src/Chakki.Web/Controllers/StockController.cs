using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record StockLists(List<InventoryItem> Items, List<Customer> Customers, List<Supplier> Suppliers);

public class StockController(AppDbContext db, OperationsService ops, LedgerService ledger) : AppController
{
    private async Task<StockLists> ListsAsync() => new(
        await db.InventoryItems.OrderBy(i => i.Name).ToListAsync(),
        await db.Customers.Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(),
        await db.Suppliers.OrderBy(s => s.Name).ToListAsync());

    // ---------------------------------------------------------------- Inventory
    [HttpGet("/inventory")]
    public async Task<IActionResult> Inventory()
    {
        ViewBag.Movements = await db.StockMovements.Include(m => m.Item).OrderByDescending(m => m.Id).Take(30).ToListAsync();
        ViewBag.Taxes = await db.TaxRates.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
        return View(await db.InventoryItems.Include(i => i.TaxRate).OrderBy(i => i.Category).ThenBy(i => i.Name).ToListAsync());
    }

    [Authorize(Policy = Roles.People), HttpPost("/inventory/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveItem(int id, string name, string? nameHi, ItemCategory category, string unit, decimal salePrice, decimal buybackRate, decimal reorderLevel, int? taxRateId = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Name is required.");
        InventoryItem item;
        if (id == 0) { item = new InventoryItem(); db.InventoryItems.Add(item); }
        else item = await db.InventoryItems.FindAsync(id) ?? throw new KeyNotFoundException();
        item.Name = name.Trim(); item.NameHi = nameHi?.Trim(); item.Category = category; item.Unit = string.IsNullOrWhiteSpace(unit) ? "kg" : unit.Trim();
        item.SalePrice = salePrice; item.BuybackRate = buybackRate; item.ReorderLevel = reorderLevel;
        item.TaxRateId = taxRateId is > 0 ? taxRateId : null;
        await db.SaveChangesAsync();
        Flash(T["Item saved."]);
        return Redirect("/inventory");
    }

    // ---------------------------------------------------------------- Suppliers & purchases
    [Authorize(Policy = Roles.Money), HttpGet("/suppliers")]
    public async Task<IActionResult> Suppliers()
    {
        var suppliers = await db.Suppliers.OrderBy(s => s.Name).ToListAsync();
        var balances = new Dictionary<int, decimal>();
        foreach (var s in suppliers) balances[s.Id] = await ledger.SupplierPayableAsync(s.Id);
        ViewBag.Balances = balances;
        return View(suppliers);
    }

    [Authorize(Policy = Roles.Money), HttpPost("/suppliers/save"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSupplier(int id, string name, string? phone, string? address)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Name is required.");
        Supplier s;
        if (id == 0) { s = new Supplier(); db.Suppliers.Add(s); }
        else s = await db.Suppliers.FindAsync(id) ?? throw new KeyNotFoundException();
        s.Name = name.Trim(); s.Phone = phone?.Trim(); s.Address = address?.Trim();
        await db.SaveChangesAsync();
        Flash(T["Supplier saved."]);
        return Redirect("/suppliers");
    }

    [Authorize(Policy = Roles.Money), HttpPost("/suppliers/pay"), ValidateAntiForgeryToken]
    public async Task<IActionResult> PaySupplier(int supplierId, decimal amount, string? note)
    {
        await ops.PaySupplierAsync(supplierId, amount, note, UserId);
        Flash(T["Payment to supplier saved."]);
        return Redirect("/suppliers");
    }

    [Authorize(Policy = Roles.Money), HttpGet("/purchases")]
    public async Task<IActionResult> Purchases()
    {
        ViewBag.Lists = await ListsAsync();
        return View(await db.Purchases.Include(p => p.Supplier).Include(p => p.Item).OrderByDescending(p => p.Id).Take(100).ToListAsync());
    }

    [Authorize(Policy = Roles.Money), HttpPost("/purchases"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Purchase(int supplierId, int itemId, decimal quantity, decimal rate, bool paidNow, DateOnly? date)
    {
        await ops.PurchaseAsync(supplierId, itemId, quantity, rate, paidNow, date, UserId);
        Flash(T["Purchase saved and stock updated."]);
        return Redirect("/purchases");
    }

    // ---------------------------------------------------------------- Sales
    [HttpGet("/sales")]
    public async Task<IActionResult> Sales()
    {
        ViewBag.Lists = await ListsAsync();
        return View(await db.Sales.Include(s => s.Item).Include(s => s.Customer).OrderByDescending(s => s.Id).Take(100).ToListAsync());
    }

    [HttpPost("/sales"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Sell(int itemId, decimal quantity, decimal? rate, int? customerId, bool onUdhaar)
    {
        await ops.SellAsync(itemId, quantity, rate, customerId is > 0 ? customerId : null, onUdhaar, UserId);
        Flash(T["Sale saved."]);
        return Redirect("/sales");
    }

    // ---------------------------------------------------------------- Buyback
    [HttpGet("/buyback")]
    public async Task<IActionResult> Buyback()
    {
        ViewBag.Lists = await ListsAsync();
        ViewBag.Orders = await db.GrindingOrders.Include(o => o.Customer).Where(o => o.BilledAt != null && o.PaymentState != PaymentState.Paid).OrderByDescending(o => o.Id).Take(100).ToListAsync();
        return View(await db.Buybacks.Include(b => b.Customer).Include(b => b.Item).OrderByDescending(b => b.Id).Take(100).ToListAsync());
    }

    [HttpPost("/buyback"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DoBuyback(int customerId, int itemId, decimal quantity, decimal? rate, string? reason, int? orderId, bool fromOrder = false)
    {
        if (orderId is > 0)
        {
            var order = await db.GrindingOrders.FindAsync(orderId) ?? throw new KeyNotFoundException();
            customerId = order.CustomerId;
        }
        var bb = await ops.BuybackAsync(customerId, itemId, quantity, rate, reason, orderId is > 0 ? orderId : null, UserId);
        Flash(T["Buyback saved: value {0}, fee adjusted {1}, cash to customer {2}.", Fmt.Rs(bb.Value), Fmt.Rs(bb.FeeAdjusted), Fmt.Rs(bb.CashPaid)]);
        return Redirect(fromOrder && orderId is > 0 ? $"/orders/{orderId}" : "/buyback");
    }

    // ---------------------------------------------------------------- Production & wastage
    [Authorize(Policy = Roles.Floor), HttpGet("/production")]
    public async Task<IActionResult> Production()
    {
        ViewBag.Lists = await ListsAsync();
        return View(await db.ProductionRuns.Include(p => p.InputItem).Include(p => p.OutputItem).Include(p => p.ByProductItem).OrderByDescending(p => p.Id).Take(100).ToListAsync());
    }

    [Authorize(Policy = Roles.Floor), HttpPost("/production"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Produce(int inputId, decimal inputQty, int outputId, decimal outputQty, int? byId, decimal byQty)
    {
        await ops.ProduceAsync(inputId, inputQty, outputId, outputQty, byId is > 0 ? byId : null, byQty);
        Flash(T["Production saved."]);
        return Redirect("/production");
    }

    [Authorize(Policy = Roles.Floor), HttpGet("/wastage")]
    public async Task<IActionResult> Wastage()
    {
        ViewBag.Lists = await ListsAsync();
        return View(await db.WastageLogs.Include(w => w.Item).OrderByDescending(w => w.Id).Take(100).ToListAsync());
    }

    [Authorize(Policy = Roles.Floor), HttpPost("/wastage"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Waste(int? itemId, string kind, decimal quantity, string? note)
    {
        await ops.WastageAsync(itemId is > 0 ? itemId : null, string.IsNullOrWhiteSpace(kind) ? "Milling loss" : kind, quantity, note, UserId);
        Flash(T["Wastage saved."]);
        return Redirect("/wastage");
    }
}
