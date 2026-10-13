using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin/audit")]
[Authorize(Policy = AdminPolicies.Users)]
public sealed class AdminAuditController(AdminAuditService audit) : ControllerBase
{
    [HttpGet]
    public Task<Paged<AuditItem>> List(
        Guid? userId, string? action, string? entityType, string? entityId, DateTime? from, DateTime? to,
        int? page, int? pageSize, CancellationToken ct) =>
        audit.ListAsync(userId, action, entityType, entityId, from, to, page, pageSize, ct);
}
