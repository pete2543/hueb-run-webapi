using HuebRun.Api.Auth;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

public sealed record RejectPaymentRequest(string? Note);

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AdminPolicies.Payments)]
public sealed class AdminPaymentsController(AdminPaymentService payments) : ControllerBase
{
    [HttpGet("events/{eventId:int}/payments")]
    public Task<Paged<AdminPaymentItem>> List(
        int eventId, PaymentStatus? status, int? page, int? pageSize, CancellationToken ct) =>
        payments.ListAsync(eventId, status, page, pageSize, ct);

    [HttpGet("payments/{id:int}/slip")]
    public Task<SlipLink> Slip(int id, CancellationToken ct) => payments.GetSlipLinkAsync(id, ct);

    [HttpPost("payments/{id:int}/approve")]
    public Task<AdminPaymentItem> Approve(int id, CancellationToken ct) => payments.ApproveAsync(id, ct);

    [HttpPost("payments/{id:int}/reject")]
    public Task<AdminPaymentItem> Reject(int id, RejectPaymentRequest req, CancellationToken ct) =>
        payments.RejectAsync(id, req.Note, ct);

    [HttpPost("payments/{id:int}/recheck")]
    public Task<AdminPaymentItem> Recheck(int id, CancellationToken ct) => payments.RecheckAsync(id, ct);
}
