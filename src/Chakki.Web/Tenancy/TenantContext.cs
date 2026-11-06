using System.Security.Claims;

namespace Chakki.Web.Tenancy;

public static class TenantClaims
{
    public const string OrganizationId = "chakki:org";
    public const string OrganizationName = "chakki:org_name";
    public const string FullName = "chakki:name";
}

/// <summary>
/// The organisation (chakki) the current unit of work belongs to. For web requests it comes only from the signed
/// authentication cookie, never from the URL, a form field or a header, so one chakki cannot address another's data.
/// Webhooks and background work set it explicitly with <see cref="Use"/> after resolving a record server-side.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    IDisposable Use(Guid organizationId);
}

public sealed class TenantContext(IHttpContextAccessor http) : ITenantContext
{
    private Guid? _override;

    public Guid? OrganizationId
    {
        get
        {
            if (_override.HasValue) return _override;
            var value = http.HttpContext?.User.FindFirstValue(TenantClaims.OrganizationId);
            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    public IDisposable Use(Guid organizationId)
    {
        var previous = _override;
        _override = organizationId;
        return new Restore(() => _override = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>Fixed tenant for tests and command-line seeding.</summary>
public sealed class FixedTenantContext(Guid? organizationId = null) : ITenantContext
{
    public Guid? OrganizationId { get; private set; } = organizationId;

    public IDisposable Use(Guid organizationId)
    {
        var previous = OrganizationId;
        OrganizationId = organizationId;
        return new Restore(() => OrganizationId = previous);
    }

    private sealed class Restore(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

public class CrossTenantWriteException(string message) : InvalidOperationException(message);
