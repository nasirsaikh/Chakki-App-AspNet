using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chakki.Web.Data;
using Chakki.Web.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Chakki.Web.Services;

/// <summary>Encrypts gateway secrets at rest. Keys live in the Data Protection key ring (keep it stable and backed up).</summary>
public class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _p = provider.CreateProtector("Chakki.PaymentGateway.Secrets.v1");
    public string Protect(string plain) => _p.Protect(plain);
    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return _p.Unprotect(cipher); } catch (CryptographicException) { return null; }
    }
}

public record WebhookResult(int StatusCode, string Message, Payment? Payment = null);

public class PaymentService(AppDbContext db, OrderService orders, SecretProtector secrets, IHttpClientFactory http, ILogger<PaymentService> log)
{
    public static string UpiUri(PaymentGateway gw, Payment p, string orgName) =>
        "upi://pay?pa=" + Uri.EscapeDataString(gw.UpiVpa ?? "") +
        "&pn=" + Uri.EscapeDataString(gw.MerchantName ?? orgName) +
        "&am=" + p.Amount.ToString("0.00", CultureInfo.InvariantCulture) +
        "&cu=INR&tr=" + Uri.EscapeDataString(p.Reference) +
        "&tn=" + Uri.EscapeDataString("Chakki " + p.Reference);

    public static string QrSvg(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(6);
    }

    public Task<List<PaymentGateway>> ActiveGatewaysAsync() =>
        db.PaymentGateways.Where(g => g.IsActive).OrderByDescending(g => g.IsDefault).ThenBy(g => g.Name).ToListAsync();

    /// <summary>Creates a pending online payment (UPI QR or card) for part or all of the outstanding amount.</summary>
    public async Task<Payment> StartOnlineAsync(GrindingOrder order, int gatewayId, decimal amount, PaymentMethod method, string? userId)
    {
        OrderService.EnsurePayable(order, amount);
        var gw = await db.PaymentGateways.FirstOrDefaultAsync(g => g.Id == gatewayId && g.IsActive)
                 ?? throw new BusinessRuleException("Choose an active payment gateway (Settings → Payments).");
        if (method == PaymentMethod.UpiQr && string.IsNullOrWhiteSpace(gw.UpiVpa) && gw.Provider != GatewayProvider.Razorpay)
            throw new BusinessRuleException("This gateway has no UPI ID configured.");
        if (method == PaymentMethod.Card && gw.Provider != GatewayProvider.Razorpay)
            throw new BusinessRuleException("Card payments need a Razorpay gateway.");

        var payment = new Payment
        {
            GrindingOrderId = order.Id, CustomerId = order.CustomerId, Method = method, Amount = OrderService.Money(amount),
            GatewayId = gw.Id, ProviderSnapshot = $"{gw.Provider}:{gw.Name}", Reference = PaymentReferences.New("QR"), RecordedById = userId,
        };
        if (gw.Provider == GatewayProvider.Razorpay)
            payment.ProviderOrderId = await CreateRazorpayOrderAsync(gw, payment);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }

    private async Task<string?> CreateRazorpayOrderAsync(PaymentGateway gw, Payment p)
    {
        var secret = secrets.Unprotect(gw.ApiSecretProtected);
        if (string.IsNullOrEmpty(gw.KeyId) || string.IsNullOrEmpty(secret))
            throw new BusinessRuleException("Razorpay Key ID and Key Secret are not configured.");
        var client = http.CreateClient("razorpay");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{gw.KeyId}:{secret}")));
        req.Content = JsonContent.Create(new { amount = (long)(p.Amount * 100), currency = "INR", receipt = p.Reference[..Math.Min(40, p.Reference.Length)], notes = new { reference = p.Reference } });
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("Razorpay order creation failed: {Status}", res.StatusCode);
            throw new BusinessRuleException("The payment provider did not accept the request. Check the gateway keys.");
        }
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetString();
    }

    /// <summary>Razorpay Checkout success callback: verify HMAC_SHA256(order_id|payment_id, key_secret).</summary>
    public async Task<bool> VerifyRazorpayCheckoutAsync(Payment payment, string orderId, string paymentId, string signature)
    {
        var gw = payment.Gateway ?? await db.PaymentGateways.FindAsync(payment.GatewayId);
        var secret = secrets.Unprotect(gw?.ApiSecretProtected);
        if (gw == null || secret == null || payment.ProviderOrderId != orderId) return false;
        if (!SignatureMatches(Hmac(secret, Encoding.UTF8.GetBytes($"{orderId}|{paymentId}")), signature)) return false;
        await SettleAsync(payment, paymentId, null);
        return true;
    }

    public async Task SettleAsync(Payment payment, string? providerPaymentId, string? userId)
    {
        var order = await db.GrindingOrders.SingleAsync(o => o.Id == payment.GrindingOrderId);
        await orders.SettleAsync(order, payment, providerPaymentId, userId);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Generic signed webhook (bank / UPI aggregator / custom provider).
    /// The gateway — and therefore the secret — is resolved from the payment record, never from the request,
    /// and the tenant is switched to the payment's organisation only after the signature is verified.
    /// </summary>
    public async Task<WebhookResult> HandleWebhookAsync(byte[] rawBody, Func<string, string?> header)
    {
        WebhookPayload? payload;
        try { payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return new(400, "invalid json"); }
        if (payload?.PaymentReference is not { Length: > 0 } reference) return new(400, "payment_reference missing");

        var payment = await db.Payments.IgnoreQueryFilters().Include(p => p.Gateway).FirstOrDefaultAsync(p => p.Reference == reference);
        if (payment?.Gateway == null) return new(404, "unknown payment");
        var gw = payment.Gateway;
        if (!gw.IsActive) return new(403, "gateway disabled");
        var secret = secrets.Unprotect(gw.WebhookSecretProtected);
        if (secret == null) return new(403, "gateway has no webhook secret");
        var sig = header(gw.SignatureHeader);
        if (sig == null || !SignatureMatches(Hmac(secret, rawBody), sig)) return new(401, "invalid signature");

        using var scope = db.Tenant.Use(payment.OrganizationId);
        if (payment.Status == SettlementStatus.Settled) return new(200, "already settled", payment);
        var status = (payload.Status ?? "").ToUpperInvariant();
        if (status is not ("SUCCESS" or "SETTLED" or "PAID" or "CAPTURED")) return new(202, "ignored status");
        if (ParseAmount(payload.Amount) is not { } amount || amount != payment.Amount)
            return new(422, "amount mismatch");

        await using var tx = await db.Database.BeginTransactionAsync();
        var fresh = await db.Payments.SingleAsync(p => p.Id == payment.Id);
        if (fresh.Status == SettlementStatus.Settled) return new(200, "already settled", fresh);
        await SettleAsync(fresh, payload.ProviderPaymentId, null);
        await tx.CommitAsync();
        return new(200, "settled", fresh);
    }

    /// <summary>Razorpay server webhook (payment.captured / order.paid) — signature is HMAC of the raw body with the webhook secret.</summary>
    public async Task<WebhookResult> HandleRazorpayWebhookAsync(byte[] rawBody, string? signature)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var entity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
        var orderId = entity.GetProperty("order_id").GetString();
        var paymentId = entity.GetProperty("id").GetString();
        var amountPaise = entity.GetProperty("amount").GetInt64();
        var payment = await db.Payments.IgnoreQueryFilters().Include(p => p.Gateway).FirstOrDefaultAsync(p => p.ProviderOrderId == orderId);
        if (payment?.Gateway == null) return new(404, "unknown order");
        var secret = secrets.Unprotect(payment.Gateway.WebhookSecretProtected);
        if (secret == null || signature == null || !SignatureMatches(Hmac(secret, rawBody), signature)) return new(401, "invalid signature");
        using var scope = db.Tenant.Use(payment.OrganizationId);
        if (payment.Status == SettlementStatus.Settled) return new(200, "already settled", payment);
        if (amountPaise != (long)(payment.Amount * 100)) return new(422, "amount mismatch");
        var fresh = await db.Payments.SingleAsync(p => p.Id == payment.Id);
        await SettleAsync(fresh, paymentId, null);
        return new(200, "settled", fresh);
    }

    private static decimal? ParseAmount(JsonElement? e) => e?.ValueKind switch
    {
        JsonValueKind.Number => e.Value.GetDecimal(),
        JsonValueKind.String when decimal.TryParse(e.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public static string Hmac(string secret, byte[] body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    public static bool SignatureMatches(string expectedHex, string provided)
    {
        provided = provided.Trim();
        if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) provided = provided[7..];
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHex), Encoding.ASCII.GetBytes(provided.ToLowerInvariant()));
    }

    public record WebhookPayload(
        [property: System.Text.Json.Serialization.JsonPropertyName("payment_reference")] string? PaymentReference,
        string? Status, JsonElement? Amount,
        [property: System.Text.Json.Serialization.JsonPropertyName("provider_payment_id")] string? ProviderPaymentId);
}
