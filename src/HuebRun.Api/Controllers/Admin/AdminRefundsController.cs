using HuebRun.Api.Auth;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

public sealed record RejectRefundRequest(string? Note);
public sealed record CompleteRefundRequest(string? ExternalRef, string? Note);

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AdminPolicies.Payments)]
public sealed class AdminRefundsController(AdminRefundService refunds) : ControllerBase
{
    [HttpGet("events/{eventId:int}/refunds")]
    public Task<Paged<AdminRefundItem>> List(int eventId, RefundStatus? status, int? page, int? pageSize, CancellationToken ct) =>
        refunds.ListAsync(eventId, status, page, pageSize, ct);

    [HttpPost("orders/{orderId:guid}/refunds")]
    public async Task<IActionResult> Create(Guid orderId, CreateRefundRequest req, CancellationToken ct)
    {
        var created = await refunds.CreateAsync(orderId, req, ct);
        return Created($"/api/v1/admin/refunds/{created.RefundId}", created);
    }

    [HttpPost("refunds/{id:int}/approve")]
    public Task<AdminRefundItem> Approve(int id, CancellationToken ct) => refunds.ApproveAsync(id, ct);

    [HttpPost("refunds/{id:int}/reject")]
    public Task<AdminRefundItem> Reject(int id, RejectRefundRequest req, CancellationToken ct) => refunds.RejectAsync(id, req.Note, ct);

    [HttpPost("refunds/{id:int}/complete")]
    public Task<AdminRefundItem> Complete(int id, CompleteRefundRequest req, CancellationToken ct) => refunds.CompleteAsync(id, req.ExternalRef, req.Note, ct);
}
