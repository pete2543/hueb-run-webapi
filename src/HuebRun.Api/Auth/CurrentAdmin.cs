using System.Security.Claims;
using HuebRun.Infrastructure.Admin;

namespace HuebRun.Api.Auth;

public sealed class CurrentAdmin(IHttpContextAccessor accessor) : ICurrentAdmin
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("sub"), out var id) ? id : null;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
