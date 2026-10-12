using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

[ApiController]
[Route("api/v1/admin/events/{eventId:int}/promos")]
[Authorize(Policy = AdminPolicies.EventSettings)]
public sealed class AdminPromosController(AdminPromoService promos) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<PromoInfo>> List(int eventId, CancellationToken ct) => promos.ListAsync(eventId, ct);

    [HttpPost]
    public async Task<IActionResult> Create(int eventId, PromoInput req, CancellationToken ct)
    {
        var created = await promos.CreateAsync(eventId, req, ct);
        return Created($"/api/v1/admin/events/{eventId}/promos/{created.Id}", created);
    }

    [HttpPut("{promoId:int}")]
    public Task<PromoInfo> Update(int eventId, int promoId, PromoInput req, CancellationToken ct) =>
        promos.UpdateAsync(eventId, promoId, req, ct);
}
