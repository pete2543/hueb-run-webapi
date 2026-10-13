using HuebRun.Api.Auth;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AdminPolicies.EventSettings)]
public sealed class AdminBroadcastsController(AdminBroadcastService broadcasts) : ControllerBase
{
    [HttpGet("events/{eventId:int}/broadcasts")]
    public Task<Paged<BroadcastInfo>> List(int eventId, BroadcastStatus? status, int? page, int? pageSize, CancellationToken ct) =>
        broadcasts.ListAsync(eventId, status, page, pageSize, ct);

    [HttpPost("events/{eventId:int}/broadcasts")]
    public async Task<IActionResult> Create(int eventId, BroadcastInput req, CancellationToken ct)
    {
        var created = await broadcasts.CreateAsync(eventId, req, ct);
        return Created($"/api/v1/admin/broadcasts/{created.Id}", created);
    }

    [HttpGet("broadcasts/{id:int}")]
    public Task<BroadcastInfo> Get(int id, CancellationToken ct) => broadcasts.GetAsync(id, ct);

    [HttpPut("broadcasts/{id:int}")]
    public Task<BroadcastInfo> Update(int id, BroadcastInput req, CancellationToken ct) => broadcasts.UpdateAsync(id, req, ct);

    [HttpPost("broadcasts/{id:int}/send")]
    public Task<BroadcastInfo> Send(int id, SendBroadcastRequest req, CancellationToken ct) => broadcasts.SendAsync(id, req, ct);

    [HttpPost("broadcasts/{id:int}/cancel")]
    public Task<BroadcastInfo> Cancel(int id, CancellationToken ct) => broadcasts.CancelAsync(id, ct);
}
