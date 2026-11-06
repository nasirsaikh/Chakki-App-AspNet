using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public class OrderForm
{
    public int CustomerId { get; set; }
    public string? NewCustomerName { get; set; }
    public string? NewCustomerPhone { get; set; }
    public string? NewCustomerVillage { get; set; }
    public string? Notes { get; set; }
    public List<OrderLineInput> Lines { get; set; } = [];
}

public record OrderListModel(List<GrindingOrder> Orders, string? Q, string? Status);
public record OrderNewModel(List<Customer> Customers, List<RateCard> Rates, int? CustomerId, GrindingOrder? Editing = null);
public record OrderDetailModel(GrindingOrder Order, List<RateCard> Rates, List<PaymentGateway> Gateways, List<InventoryItem> BuybackItems, List<Buyback> Buybacks, decimal OtherUdhaar);

[Route("orders")]
public class OrdersController(AppDbContext db, OrderService orders, PaymentService payments, LedgerService ledger) : AppController
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? q, string? status)
    {
        var query = db.GrindingOrders.Include(o => o.Customer).Include(o => o.Lines).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().TrimStart('G', 'g', '-').TrimStart('0');
            query = int.TryParse(term, out var n)
                ? query.Where(o => o.Number == n || o.Customer!.Phone!.Contains(q))
                : query.Where(o => o.Customer!.Name.Contains(q) || (o.Customer.Village != null && o.Customer.Village.Contains(q)));
        }
        query = status switch
        {
            "open" => query.Where(o => o.Status == OrderStatus.Intake || o.Status == OrderStatus.Grinding || o.Status == OrderStatus.Ready),
            "unpaid" => query.Where(o => o.PaymentState == PaymentState.Unpaid || o.PaymentState == PaymentState.Partial),
            "delivered" => query.Where(o => o.Status == OrderStatus.Delivered),
            _ => query,
        };
        var list = await query.OrderByDescending(o => o.Id).Take(200).ToListAsync();
        var model = new OrderListModel(list, q, status);
        return IsHtmx && Request.Headers["HX-Target"] == "order-rows" ? PartialView("_Rows", model) : View(model);
    }

    [Authorize(Policy = Roles.Floor), HttpGet("new")]
    public async Task<IActionResult> New(int? customerId) =>
        View(new OrderNewModel(await db.Customers.Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync(),
            await db.RateCards.Include(r => r.TaxRate).Where(r => r.IsActive).OrderBy(r => r.Id).ToListAsync(), customerId));

    /// <summary>Change an order that was sent back to Received (customer, grains, weights, rates).</summary>
    [Authorize(Policy = Roles.Floor), HttpGet("{id:int}/edit")]
    public async Task<IActionResult> Edit(int id)
    {
        var order = await LoadAsync(id);
        if (order.Status != OrderStatus.Intake || order.BilledAt != null)
            throw new BusinessRuleException("Go back to Received (and remove the bill) before changing the order.");
        return View("New", new OrderNewModel(await db.Customers.Where(c => c.IsActive || c.Id == order.CustomerId).OrderBy(c => c.Name).ToListAsync(),
            await db.RateCards.Include(r => r.TaxRate).Where(r => r.IsActive || order.Lines.Select(l => l.RateCardId).Contains(r.Id)).OrderBy(r => r.Id).ToListAsync(),
            order.CustomerId, order));
    }

    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/edit"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, OrderForm form)
    {
        var order = await LoadAsync(id);
        var customerId = await CustomerFromFormAsync(form);
        await orders.UpdateAsync(order, customerId, form.Lines, form.Notes, UserId);
        Flash(T["Order {0} updated.", order.Ref]);
        return Redirect($"/orders/{id}");
    }

    private async Task<int> CustomerFromFormAsync(OrderForm form)
    {
        if (form.CustomerId != 0) return form.CustomerId;
        if (string.IsNullOrWhiteSpace(form.NewCustomerName)) throw new BusinessRuleException("Choose a customer or type a new customer's name.");
        var c = new Customer { Name = form.NewCustomerName.Trim(), Phone = form.NewCustomerPhone?.Trim(), Village = form.NewCustomerVillage?.Trim(), Kind = CustomerKind.Temporary };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    [Authorize(Policy = Roles.Floor), HttpPost("new"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(OrderForm form)
    {
        var customerId = await CustomerFromFormAsync(form);
        var order = await orders.CreateAsync(customerId, form.Lines, form.Notes, UserId);
        Flash(T["Order {0} created.", order.Ref]);
        return Redirect($"/orders/{order.Id}");
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id)
    {
        var order = await LoadAsync(id);
        return View(new OrderDetailModel(order, await db.RateCards.ToListAsync(), await payments.ActiveGatewaysAsync(),
            await db.InventoryItems.Where(i => i.BuybackRate > 0).OrderByDescending(i => i.BuybackRate).ThenBy(i => i.Name).ToListAsync(),
            await db.Buybacks.Include(b => b.Item).Where(b => b.GrindingOrderId == id).OrderBy(b => b.Id).ToListAsync(),
            await ledger.CustomerUdhaarAsync(order.CustomerId) - order.Outstanding));
    }

    /// <summary>Marks one grain / service as started or done; the order status follows its lines.</summary>
    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/lines/{lineId:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Line(int id, int lineId, LineStatus to)
    {
        var order = await LoadAsync(id);
        await orders.SetLineStatusAsync(order, lineId, to, UserId);
        return Redirect($"/orders/{id}");
    }

    /// <summary>Undo the last step (hand-over, ready, grinding) when it was marked by mistake.</summary>
    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/back"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Back(int id, string reason, OrderStatus? to)
    {
        var order = await LoadAsync(id);
        if (to is { } target) await orders.StepBackToAsync(order, target, reason, UserId);
        else await orders.StepBackAsync(order, reason, UserId);
        Flash(T["Moved back."]);
        return Redirect($"/orders/{id}");
    }

    /// <summary>Remove a bill made by mistake (only while nothing is paid). Owner or manager.</summary>
    [Authorize(Policy = Roles.People), HttpPost("{id:int}/unbill"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Unbill(int id, string reason)
    {
        var order = await LoadAsync(id);
        await orders.UnbillAsync(order, reason, UserId);
        Flash(T["Bill removed. You can change the order and bill it again."]);
        return Redirect($"/orders/{id}");
    }

    /// <summary>Customer left / duplicate / wrong entry. A billed order needs an owner or manager.</summary>
    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string reason)
    {
        var order = await LoadAsync(id);
        if (order.BilledAt != null && !(User.IsInRole(Roles.Owner) || User.IsInRole(Roles.Manager)))
            throw new BusinessRuleException("This order is billed. Ask the owner or manager to cancel it.");
        await orders.CancelAsync(order, reason, UserId);
        Flash(T["Order cancelled."]);
        return Redirect($"/orders/{id}");
    }

    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/status"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Status(int id, OrderStatus to)
    {
        var order = await LoadAsync(id);
        await orders.AdvanceAsync(order, to);
        return Redirect($"/orders/{id}");
    }

    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/bill"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Bill(int id, decimal forgiven, decimal? paidNow)
    {
        var order = await LoadAsync(id);
        if (forgiven > 0 && !(User.IsInRole(Roles.Owner) || User.IsInRole(Roles.Manager)) && forgiven > Math.Max(5, order.Total * 0.05m))
            throw new BusinessRuleException("Operators can forgive at most ₹5 or 5% of the bill. Ask a manager for more.");
        await orders.BillAsync(order, forgiven, UserId);
        if (paidNow is > 0) await orders.CollectCashAsync(order, paidNow.Value, UserId);
        Flash(T["Bill saved."]);
        return Redirect($"/orders/{id}");
    }

    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/cash"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cash(int id, decimal amount)
    {
        var order = await LoadAsync(id);
        await orders.CollectCashAsync(order, amount, UserId);
        Flash(T["Cash payment of {0} recorded.", Fmt.Rs(amount)]);
        return Redirect($"/orders/{id}");
    }

    /// <summary>Starts a UPI QR or card payment and opens the payment screen (counter or shareable link).</summary>
    [Authorize(Policy = Roles.Floor), HttpPost("{id:int}/online"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Online(int id, int gatewayId, decimal amount, PaymentMethod method)
    {
        var order = await LoadAsync(id);
        var payment = await payments.StartOnlineAsync(order, gatewayId, amount, method, UserId);
        return Redirect($"/pay/{payment.Reference}");
    }

    private async Task<GrindingOrder> LoadAsync(int id) =>
        await db.GrindingOrders.Include(o => o.Customer).Include(o => o.Lines).ThenInclude(l => l.RateCard).Include(o => o.Payments)
            .FirstOrDefaultAsync(o => o.Id == id) ?? throw new KeyNotFoundException();
}
