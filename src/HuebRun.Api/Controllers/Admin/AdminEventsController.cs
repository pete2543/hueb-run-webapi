using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin/events")]
public sealed class AdminEventsController(AdminEventService events) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = AdminPolicies.EventCreate)]
    public async Task<IActionResult> Create(CreateEventRequest req, CancellationToken ct)
    {
        var created = await events.CreateAsync(req, ct);
        return Created($"/api/v1/admin/events/{created.Id}", created);
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<AdminEventInfo> Get(int id, CancellationToken ct) => events.GetAsync(id, ct);

    [HttpPut("{id:int}")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<EventUpdateResult> Update(int id, UpdateEventRequest req, CancellationToken ct) => events.UpdateAsync(id, req, ct);

    [HttpGet("{id:int}/distances")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<IReadOnlyList<DistanceInfo>> Distances(int id, CancellationToken ct) => events.GetDistancesAsync(id, ct);

    [HttpPut("{id:int}/distances")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<IReadOnlyList<DistanceInfo>> PutDistances(int id, List<DistanceInput> req, CancellationToken ct) => events.PutDistancesAsync(id, req, ct);

    [HttpGet("{id:int}/shirt-sizes")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<IReadOnlyList<ShirtSizeInfo>> ShirtSizes(int id, CancellationToken ct) => events.GetShirtSizesAsync(id, ct);

    [HttpPut("{id:int}/shirt-sizes")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<IReadOnlyList<ShirtSizeInfo>> PutShirtSizes(int id, List<ShirtSizeInput> req, CancellationToken ct) => events.PutShirtSizesAsync(id, req, ct);

    [HttpGet("{id:int}/shipping-options")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<IReadOnlyList<ShippingOptionInfo>> Shipping(int id, CancellationToken ct) => events.GetShippingOptionsAsync(id, ct);

    [HttpPut("{id:int}/shipping-options")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<IReadOnlyList<ShippingOptionInfo>> PutShipping(int id, List<ShippingOptionInput> req, CancellationToken ct) => events.PutShippingOptionsAsync(id, req, ct);
}
