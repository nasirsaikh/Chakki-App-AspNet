using System.Globalization;
using System.Security.Claims;
using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Services;
using Chakki.Web.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chakki.Web.Infrastructure;

/// <summary>Puts the user's organisation into the signed cookie. Inactive users or chakkis cannot sign in.</summary>
public class ChakkiClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole> roles, IOptions<IdentityOptions> options, AppDbContext db)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(TenantClaims.FullName, string.IsNullOrWhiteSpace(user.FullName) ? user.Email ?? "" : user.FullName));
        if (user.OrganizationId is { } orgId && user.IsActive)
        {
            var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orgId);
            if (org is { IsActive: true })
            {
                identity.AddClaim(new Claim(TenantClaims.OrganizationId, org.Id.ToString()));
                identity.AddClaim(new Claim(TenantClaims.OrganizationName, org.Name));
            }
        }
        return identity;
    }
}

public class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task Invoke(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "strict-origin-when-cross-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        // Razorpay Checkout is the only third-party script, used on the card payment screen.
        h["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' https://checkout.razorpay.com; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
            "font-src 'self'; connect-src 'self' https://api.razorpay.com https://lumberjack.razorpay.com; frame-src https://api.razorpay.com https://checkout.razorpay.com; " +
            "form-action 'self'; base-uri 'self'; frame-ancestors 'none'";
        return next(ctx);
    }
}

/// <summary>Business-rule failures become a friendly message (HTMX: inline 422; full page: flash + redirect back).</summary>
public class BusinessErrorFilter(ITempDataDictionaryFactory tempData) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is KeyNotFoundException)
        {
            context.Result = new NotFoundResult();
            context.ExceptionHandled = true;
            return;
        }
        if (context.Exception is not (BusinessRuleException or UnbalancedJournalException or CrossTenantWriteException)) return;
        var message = context.Exception is CrossTenantWriteException ? "That record is not available." : context.Exception.Message;
        var http = context.HttpContext;
        if (http.Request.Headers.ContainsKey("HX-Request"))
        {
            http.Response.Headers["HX-Retarget"] = "#ca-action-feedback";
            http.Response.Headers["HX-Reswap"] = "innerHTML";
            context.Result = new ContentResult { StatusCode = 422, Content = System.Net.WebUtility.HtmlEncode(Localize(http, message)), ContentType = "text/html; charset=utf-8" };
        }
        else
        {
            tempData.GetTempData(http)["Error"] = Localize(http, message);
            var back = http.Request.Headers.Referer.ToString();
            context.Result = new RedirectResult(Uri.TryCreate(back, UriKind.Absolute, out var u) && u.Host == http.Request.Host.Host ? u.PathAndQuery : "/");
        }
        context.ExceptionHandled = true;
    }

    private static string Localize(HttpContext http, string message) =>
        http.RequestServices.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Localization.Shared>>()[message];
}

public abstract class AppController : Controller
{
    protected string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    protected bool IsHtmx => Request.Headers.ContainsKey("HX-Request");
    protected bool Hindi => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hi";
    protected void Flash(string message) => TempData["Success"] = message;
    protected Microsoft.Extensions.Localization.IStringLocalizer T =>
        HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<Localization.Shared>>();
    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Full view on normal requests, the partial on HTMX requests.</summary>
    protected IActionResult Page(string view, object? model = null) => IsHtmx ? PartialView(view, model) : View(view, model);
}

public static class Fmt
{
    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");
    public static string Rs(decimal v) => (v < 0 ? "−₹" : "₹") + Math.Abs(v).ToString("#,##0.00", India);
    public static string Qty(decimal v) => v.ToString("#,##0.##", India);
    public static string Date(DateOnly d) => d.ToString("dd MMM yyyy", India);
    public static string Name(string en, string? hi) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hi" && !string.IsNullOrEmpty(hi) ? hi : en;
    public static string Name(InventoryItem? i) => i == null ? "" : Name(i.Name, i.NameHi);
    public static string Inv(decimal d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    public static string Date(DateTime d) => d.ToLocalTime().ToString("dd MMM yyyy, HH:mm", India);
}
