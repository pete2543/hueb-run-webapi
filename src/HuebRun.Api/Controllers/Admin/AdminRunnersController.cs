using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

public sealed record TrackingRequest(string? TrackingNo);

[ApiController]
[Route("api/v1/admin")]
public sealed class AdminRunnersController(AdminRunnerService runners, AdminOrderService orders) : ControllerBase
{
    [HttpGet("events/{eventId:int}/registrations")]
    [Authorize(Policy = AdminPolicies.Overview)]
    public Task<Paged<AdminRunnerItem>> List(int eventId, int? distance, string? q, int? page, int? pageSize, CancellationToken ct) =>
        runners.ListAsync(eventId, distance, q, page, pageSize, ct);

    [HttpGet("events/{eventId:int}/orders/export")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public async Task<IActionResult> Export(int eventId, CancellationToken ct)
    {
        var file = await runners.ExportAsync(eventId, ct);
        return File(file.Content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.FileName);
    }

    [HttpPatch("orders/{id:guid}/tracking")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public async Task<IActionResult> Tracking(Guid id, TrackingRequest req, CancellationToken ct) =>
        Ok(new { trackingNo = await orders.SetTrackingAsync(id, req.TrackingNo, ct) });
}
