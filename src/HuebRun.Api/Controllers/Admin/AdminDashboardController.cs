using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin/events/{eventId:int}/dashboard")]
[Authorize(Policy = AdminPolicies.Overview)]
public sealed class AdminDashboardController(AdminDashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public Task<Dashboard> Get(int eventId, CancellationToken ct) => dashboard.GetAsync(eventId, ct);
}
