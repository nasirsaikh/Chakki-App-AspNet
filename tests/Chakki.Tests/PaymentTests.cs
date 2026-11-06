using System.Text;
using Chakki.Web.Domain;
using Chakki.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Chakki.Tests;

public class PaymentTests
{
    private const string Secret = "test-webhook-secret-for-unit-tests-only";

    private static async Task<(TestEnv Env, Payment Payment, GrindingOrder Order)> PendingQrAsync()
    {
        var env = await TestEnv.CreateAsync();
        using var _ = env.As(env.OrgA);
        var gw = new PaymentGateway { Name = "UPI", Provider = GatewayProvider.UpiQr, UpiVpa = "chakki@okdemo", SignatureHeader = "X-Chakki-Signature",
            IsActive = true, WebhookSecretProtected = env.Secrets.Protect(Secret) };
        env.Db.PaymentGateways.Add(gw);
        await env.Db.SaveChangesAsync();
        var order = await env.BilledOrderAsync(25); // ₹60
        var payment = await env.Payments.StartOnlineAsync(order, gw.Id, 60, PaymentMethod.UpiQr, TestEnv.User);
        env.Db.ChangeTracker.Clear();
        return (env, payment, order);
    }

    private static Task<WebhookResult> SendAsync(TestEnv env, string json, string secret, string header = "X-Chakki-Signature")
    {
        var body = Encoding.UTF8.GetBytes(json);
        var sig = PaymentService.Hmac(secret, body);
        return env.Payments.HandleWebhookAsync(body, h => h == header ? sig : null);
    }

    private static string Body(string reference, string amount, string status = "SUCCESS") =>
        $$"""{"payment_reference":"{{reference}}","status":"{{status}}","amount":{{amount}},"provider_payment_id":"UTR123"}""";

    [Fact]
    public async Task Valid_webhook_settles_the_payment_and_order()
    {
        var (env, p, order) = await PendingQrAsync();
        using (env)
        {
            // The webhook arrives with no signed-in user and therefore no tenant.
            var r = await SendAsync(env, Body(p.Reference, "\"60.00\""), Secret);
            Assert.Equal(200, r.StatusCode);
            using (env.As(env.OrgA))
            {
                var fresh = await env.Db.GrindingOrders.SingleAsync(o => o.Id == order.Id);
                Assert.Equal(PaymentState.Paid, fresh.PaymentState);
                Assert.Equal(0m, await env.Ledger.CustomerUdhaarAsync(order.CustomerId));
            }
        }
    }

    [Fact]
    public async Task Webhook_with_wrong_signature_is_refused()
    {
        var (env, p, _) = await PendingQrAsync();
        using (env)
        {
            var r = await SendAsync(env, Body(p.Reference, "60"), "some-other-secret-of-enough-length");
            Assert.Equal(401, r.StatusCode);
            using (env.As(env.OrgA)) Assert.Equal(SettlementStatus.Pending, (await env.Db.Payments.SingleAsync(x => x.Id == p.Id)).Status);
        }
    }

    [Fact]
    public async Task Webhook_with_wrong_amount_is_refused()
    {
        var (env, p, _) = await PendingQrAsync();
        using (env) Assert.Equal(422, (await SendAsync(env, Body(p.Reference, "6"), Secret)).StatusCode);
    }

    [Fact]
    public async Task Webhook_failure_status_is_ignored()
    {
        var (env, p, _) = await PendingQrAsync();
        using (env) Assert.Equal(202, (await SendAsync(env, Body(p.Reference, "60", "FAILED"), Secret)).StatusCode);
    }

    [Fact]
    public async Task Replayed_webhook_posts_only_once()
    {
        var (env, p, order) = await PendingQrAsync();
        using (env)
        {
            Assert.Equal("settled", (await SendAsync(env, Body(p.Reference, "60"), Secret)).Message);
            env.Db.ChangeTracker.Clear();
            Assert.Equal("already settled", (await SendAsync(env, Body(p.Reference, "60"), Secret)).Message);
            using (env.As(env.OrgA))
            {
                Assert.Equal(1, await env.Db.JournalEntries.CountAsync(j => j.Source == "payment"));
                var tb = await env.Ledger.TrialBalanceAsync();
                Assert.Equal(tb.Sum(a => a.Debit), tb.Sum(a => a.Credit));
            }
        }
    }

    [Fact]
    public async Task Unknown_reference_is_404()
    {
        using var env = await TestEnv.CreateAsync();
        Assert.Equal(404, (await SendAsync(env, Body("QR-NOPE", "1"), Secret)).StatusCode);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "sha256=abc", true)]
    [InlineData("abc", "ABC", true)]
    [InlineData("abc", "abd", false)]
    public void Signature_comparison(string expected, string provided, bool ok) => Assert.Equal(ok, PaymentService.SignatureMatches(expected, provided));

    [Fact]
    public async Task Upi_link_carries_vpa_amount_and_reference()
    {
        var (env, p, _) = await PendingQrAsync();
        using (env)
        {
            var gw = new PaymentGateway { UpiVpa = "chakki@okdemo", MerchantName = "Shri Ram" };
            var uri = PaymentService.UpiUri(gw, p, "Shri Ram");
            Assert.StartsWith("upi://pay?", uri);
            Assert.Contains("pa=chakki%40okdemo", uri.Replace("@", "%40"));
            Assert.Contains("am=60.00", uri);
            Assert.Contains(p.Reference, uri);
        }
    }
}
