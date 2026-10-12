using HuebRun.Api.Auth;
using HuebRun.Domain;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = AdminPolicies.Overview)]
public sealed class AdminOrdersController(AdminOrderService orders) : ControllerBase
{
    [HttpGet("events/{eventId:int}/orders")]
    public Task<Paged<AdminOrderItem>> List(
        int eventId, OrderStatus? status, string? q, int? page, int? pageSize, CancellationToken ct) =>
        orders.ListAsync(eventId, status, q, page, pageSize, ct);

    [HttpGet("orders/{id:guid}")]
    public Task<AdminOrderDetail> Get(Guid id, CancellationToken ct) => orders.GetAsync(id, ct);
}
