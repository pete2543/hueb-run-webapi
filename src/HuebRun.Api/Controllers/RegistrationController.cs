using HuebRun.Infrastructure.Orders;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/events/{slug}")]
public sealed class RegistrationController(OrderService orders) : ControllerBase
{
    [HttpPost("promo/validate")]
    public Task<PromoQuote> ValidatePromo(string slug, ValidatePromoRequest request, CancellationToken ct) =>
        orders.ValidatePromoAsync(slug, request, ct);

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder(string slug, CreateOrderRequest request, CancellationToken ct)
    {
        var created = await orders.CreateOrderAsync(slug, request, ct);
        return Created($"/api/v1/orders/{created.OrderId}", created);
    }
}
