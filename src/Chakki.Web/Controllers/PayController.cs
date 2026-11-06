using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Web.Controllers;

public record PayModel(Payment Payment, GrindingOrder Order, Organization Org, string? CustomerName, string? QrSvg, string? UpiUri, bool IsStaff);

/// <summary>
/// Payment screen. It works at the counter (staff signed in) and as a link the customer opens on their phone.
/// The reference is a random 80-bit token, and the page shows only what the customer needs to pay.
/// </summary>
[Route("pay")]
public class PayController(AppDbContext db, PaymentService payments, IConfiguration config) : AppController
{
    [AllowAnonymous, HttpGet("{reference}")]
    public async Task<IActionResult> Show(string reference)
    {
        var model = await LoadAsync(reference);
        return model == null ? NotFound() : View(model);
    }

    [AllowAnonymous, HttpGet("{reference}/status")]
    public async Task<IActionResult> Status(string reference)
    {
        var model = await LoadAsync(reference);
        if (model == null) return NotFound();
        if (model.Payment.Status == SettlementStatus.Settled) Response.Headers["HX-Trigger"] = "paymentSettled";
        return PartialView("_Status", model);
    }

    [AllowAnonymous, HttpPost("{reference}/razorpay"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Razorpay(string reference, string razorpay_order_id, string razorpay_payment_id, string razorpay_signature)
    {
        var payment = await db.Payments.IgnoreQueryFilters().Include(p => p.Gateway).FirstOrDefaultAsync(p => p.Reference == reference);
        if (payment == null) return NotFound();
        using var _ = db.Tenant.Use(payment.OrganizationId);
        var ok = await payments.VerifyRazorpayCheckoutAsync(payment, razorpay_order_id, razorpay_payment_id, razorpay_signature);
        if (!ok) TempData["Error"] = T["The payment could not be verified. If money was deducted it will be confirmed automatically."].Value;
        return Redirect($"/pay/{reference}");
    }

    /// <summary>
    /// Plain UPI QR (a VPA without a provider callback): an owner or manager confirms after seeing the money arrive,
    /// with the UPI transaction (UTR) number for the record.
    /// </summary>
    [Authorize(Policy = Roles.People), HttpPost("{reference}/confirm"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(string reference, string utr)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Reference == reference);
        if (payment == null) return NotFound();
        if (string.IsNullOrWhiteSpace(utr) || utr.Trim().Length < 6) throw new BusinessRuleException("Enter the UPI transaction (UTR) number from your bank app.");
        await payments.SettleAsync(payment, "UTR:" + utr.Trim(), UserId);
        Flash(T["Payment confirmed."]);
        return Redirect($"/orders/{payment.GrindingOrderId}");
    }

    [Authorize(Policy = Roles.Floor), HttpPost("{reference}/cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(string reference)
    {
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Reference == reference);
        if (payment == null) return NotFound();
        if (payment.Status == SettlementStatus.Pending) payment.Status = SettlementStatus.Expired;
        await db.SaveChangesAsync();
        return Redirect($"/orders/{payment.GrindingOrderId}");
    }

    /// <summary>Development only: pretend the bank confirmed the payment, to try the flow without real money.</summary>
    [Authorize(Policy = Roles.Floor), HttpPost("{reference}/simulate"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Simulate(string reference, [FromServices] IWebHostEnvironment env)
    {
        if (!env.IsDevelopment() && !config.GetValue<bool>("Payments:AllowSimulation")) return NotFound();
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.Reference == reference);
        if (payment == null) return NotFound();
        await payments.SettleAsync(payment, "SIMULATED", UserId);
        return Redirect($"/pay/{reference}");
    }

    private async Task<PayModel?> LoadAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 40) return null;
        var payment = await db.Payments.IgnoreQueryFilters().Include(p => p.Gateway).AsNoTracking().FirstOrDefaultAsync(p => p.Reference == reference);
        if (payment == null || payment.Method is not (PaymentMethod.UpiQr or PaymentMethod.Card)) return null;
        using var _ = db.Tenant.Use(payment.OrganizationId);
        var order = await db.GrindingOrders.Include(o => o.Customer).AsNoTracking().SingleAsync(o => o.Id == payment.GrindingOrderId);
        var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == payment.OrganizationId);
        // Staff of this chakki see the full screen; anyone else (the customer) sees only the payment.
        var isStaff = User.FindFirst(Tenancy.TenantClaims.OrganizationId)?.Value == payment.OrganizationId.ToString();
        string? uri = null, svg = null;
        if (payment.Status == SettlementStatus.Pending && payment.Gateway is { } gw && !string.IsNullOrWhiteSpace(gw.UpiVpa))
        {
            uri = PaymentService.UpiUri(gw, payment, org.Name);
            svg = PaymentService.QrSvg(uri);
        }
        var firstName = order.Customer?.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return new PayModel(payment, order, org, isStaff ? order.Customer?.Name : firstName, svg, uri, isStaff);
    }
}

[AllowAnonymous]
[ApiController]
[Route("api/v1/payments")]
[IgnoreAntiforgeryToken]
[EnableRateLimiting("webhook")]
public class PaymentWebhookController(PaymentService payments) : ControllerBase
{
    /// <summary>Bank / UPI provider callback. HMAC-SHA256 of the exact raw body, header configured per gateway.</summary>
    [HttpPost("qr-webhook")]
    public async Task<IActionResult> QrWebhook()
    {
        var body = await ReadBodyAsync();
        var result = await payments.HandleWebhookAsync(body, h => Request.Headers.TryGetValue(h, out var v) ? v.ToString() : null);
        return StatusCode(result.StatusCode, new { status = result.Message });
    }

    [HttpPost("razorpay-webhook")]
    public async Task<IActionResult> RazorpayWebhook()
    {
        var body = await ReadBodyAsync();
        try
        {
            var result = await payments.HandleRazorpayWebhookAsync(body, Request.Headers["X-Razorpay-Signature"].ToString() is { Length: > 0 } s ? s : null);
            return StatusCode(result.StatusCode, new { status = result.Message });
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { status = "unsupported payload" });
        }
    }

    private async Task<byte[]> ReadBodyAsync()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}
