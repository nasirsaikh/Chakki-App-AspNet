using System.ComponentModel.DataAnnotations;
using Chakki.Web.Domain;
using Chakki.Web.Infrastructure;
using Chakki.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Chakki.Web.Controllers;

public class LoginInput
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool RememberMe { get; set; } = true;
}

public class RegisterInput
{
    [Required, StringLength(120, MinimumLength = 3)] public string ChakkiName { get; set; } = "";
    [Required, StringLength(120, MinimumLength = 2)] public string OwnerName { get; set; } = "";
    [Phone] public string? Phone { get; set; }
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    public string Language { get; set; } = "hi";
}

[AllowAnonymous]
[Route("account")]
public class AccountController(SignInManager<AppUser> signIn, UserManager<AppUser> users, OnboardingService onboarding) : AppController
{
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect("/");
        ViewData["ReturnUrl"] = returnUrl;
        return View(new LoginInput());
    }

    [HttpPost("login"), ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginInput input, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid) return View(input);
        var user = await users.FindByEmailAsync(input.Email.Trim());
        if (user is { IsActive: true })
        {
            var result = await signIn.PasswordSignInAsync(user, input.Password, input.RememberMe, lockoutOnFailure: true);
            if (result.Succeeded)
            {
                if (!string.IsNullOrEmpty(user.PreferredLanguage)) SetCulture(user.PreferredLanguage == "en" ? "en-IN" : "hi-IN");
                if (await users.IsInRoleAsync(user, Roles.PlatformAdmin) && user.OrganizationId == null) return LocalRedirect("/platform");
                return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
            }
            if (result.IsLockedOut)
            {
                ModelState.AddModelError("", T["Too many attempts. Try again in 10 minutes."]);
                return View(input);
            }
        }
        ModelState.AddModelError("", T["Email or password is not correct."]);
        return View(input);
    }

    [HttpGet("register")]
    public IActionResult Register() => View(new RegisterInput { Language = Hindi ? "hi" : "en" });

    [HttpPost("register"), ValidateAntiForgeryToken, EnableRateLimiting("login")]
    public async Task<IActionResult> Register(RegisterInput input)
    {
        if (!ModelState.IsValid) return View(input);
        if (await users.FindByEmailAsync(input.Email.Trim()) != null)
        {
            ModelState.AddModelError(nameof(input.Email), T["This email is already registered."]);
            return View(input);
        }
        try
        {
            var (_, owner) = await onboarding.RegisterAsync(input.ChakkiName, input.OwnerName, input.Email, input.Password, input.Language, input.Phone);
            await signIn.SignInAsync(owner, isPersistent: true);
            SetCulture(input.Language == "en" ? "en-IN" : "hi-IN");
            Flash(T["Your chakki is ready. Set your rates and payment details in Settings."]);
            return LocalRedirect("/");
        }
        catch (BusinessRuleException ex)
        {
            ModelState.AddModelError("", ex.Message);
            return View(input);
        }
    }

    [HttpPost("logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return LocalRedirect("/account/login");
    }

    [HttpGet("denied")]
    public IActionResult Denied() => View();

    [Authorize, HttpGet("password")]
    public IActionResult Password() => View();

    [Authorize, HttpPost("password"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Password(string current, string next)
    {
        var user = await users.GetUserAsync(User);
        if (user == null) return Challenge();
        var result = await users.ChangePasswordAsync(user, current ?? "", next ?? "");
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", string.Join(" ", result.Errors.Select(e => e.Description)));
            return View();
        }
        await signIn.RefreshSignInAsync(user);
        Flash(T["Password changed."]);
        return LocalRedirect("/");
    }

    private void SetCulture(string culture) =>
        Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true });
}

[AllowAnonymous]
public class LanguageController : Controller
{
    [HttpPost("/language"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(string culture, string? returnUrl, [FromServices] UserManager<AppUser> users)
    {
        culture = culture == "en-IN" ? "en-IN" : "hi-IN";
        Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true });
        if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } user)
        {
            user.PreferredLanguage = culture[..2];
            await users.UpdateAsync(user);
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}

[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("/error/{code:int?}")]
    public IActionResult Index(int? code) => View("Error", code ?? 500);
}
