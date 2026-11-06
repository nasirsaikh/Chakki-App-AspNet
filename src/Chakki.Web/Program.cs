using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Chakki.Web.Data;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Localization;
using Chakki.Web.Services;
using Chakki.Web.Services.Chat;
using Chakki.Web.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
var services = builder.Services;
var config = builder.Configuration;

// ---------------------------------------------------------------- Data & tenancy
services.AddHttpContextAccessor();
services.AddScoped<ITenantContext, TenantContext>();
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);
services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(config.GetConnectionString("Default") ?? $"Data Source={Path.Combine(dataDir, "chakki.db")}"));

// Payment gateway secrets are encrypted with these keys: keep the folder stable and backed up.
services.AddDataProtection()
    .SetApplicationName("Chakki")
    .PersistKeysToFileSystem(new DirectoryInfo(config["DataProtection:KeysPath"] ?? Path.Combine(dataDir, "keys")));

// ---------------------------------------------------------------- Identity
services.AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<ChakkiClaimsFactory>();
services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.LogoutPath = "/account/logout";
    o.AccessDeniedPath = "/account/denied";
    o.Cookie.Name = "chakki.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
});

services.AddAuthorization(o =>
{
    // Every tenant policy also requires the organisation claim, so a platform admin can never open a chakki's books.
    void Tenant(string name, params string[] roles) => o.AddPolicy(name, p => p.RequireAuthenticatedUser().RequireClaim(TenantClaims.OrganizationId).RequireRole(roles));
    Tenant("Member", Roles.TenantRoles);
    Tenant(Roles.Floor, Roles.Owner, Roles.Manager, Roles.Operator);
    Tenant(Roles.Money, Roles.Owner, Roles.Manager, Roles.Accountant);
    Tenant(Roles.Books, Roles.Owner, Roles.Accountant);
    Tenant(Roles.People, Roles.Owner, Roles.Manager);
    Tenant(Roles.Admin, Roles.Owner);
    o.AddPolicy(Roles.PlatformAdmin, p => p.RequireRole(Roles.PlatformAdmin));
    o.FallbackPolicy = o.GetPolicy("Member");
});

// ---------------------------------------------------------------- Localisation (English / Hindi)
services.AddSingleton<IStringLocalizerFactory, JsonStringLocalizerFactory>();
services.AddLocalization();
var cultures = new[] { new CultureInfo("en-IN"), new CultureInfo("hi-IN") };
services.Configure<RequestLocalizationOptions>(o =>
{
    o.DefaultRequestCulture = new RequestCulture("hi-IN");
    o.SupportedCultures = cultures;
    o.SupportedUICultures = cultures;
    o.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});

// ---------------------------------------------------------------- App services
services.AddScoped<LedgerService>();
services.AddScoped<OrderService>();
services.AddScoped<OperationsService>();
services.AddScoped<PaymentService>();
services.AddScoped<OnboardingService>();
services.AddScoped<DemoSeeder>();
services.AddScoped<ChatTools>();
services.AddScoped<ChatAssistant>();
services.AddSingleton<SecretProtector>();
services.AddSingleton(config.GetSection("Ai").Get<AiOptions>() ?? new AiOptions());
services.AddHttpClient("razorpay", c => c.Timeout = TimeSpan.FromSeconds(20));

services.AddControllersWithViews(o => o.Filters.Add<BusinessErrorFilter>())
    .AddViewLocalization();
services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "x",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("webhook", _ => RateLimitPartition.GetFixedWindowLimiter("webhook", _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// ---------------------------------------------------------------- Database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DemoSeeder>().RunAsync();
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRequestLocalization();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

public partial class Program;
