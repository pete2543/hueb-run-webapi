using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin/users")]
[Authorize(Policy = AdminPolicies.Users)]
public sealed class AdminUsersController(AdminUserService users) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AdminUserInfo>> List(CancellationToken ct) => users.ListAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create(CreateAdminUserRequest req, CancellationToken ct)
    {
        var created = await users.CreateAsync(req, ct);
        return Created($"/api/v1/admin/users/{created.Id}", created);
    }

    [HttpPatch("{id:guid}")]
    public Task<AdminUserInfo> Update(Guid id, UpdateAdminUserRequest req, CancellationToken ct) =>
        users.UpdateAsync(id, req, ct);
}
