using HuebRun.Api.Contracts;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/orders/{orderId}")]
public sealed class OrdersController(AppDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<OrderResponse> Get(Guid orderId, CancellationToken ct)
    {
        var o = await LoadAsync(orderId, ct);
        var isPost = o.ShippingOption.Type == ShippingType.Post;
        var payment = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == orderId).OrderByDescending(p => p.Id)
            .Select(p => new OrderPaymentResponse(p.Id, p.Status, p.RejectReason))
            .FirstOrDefaultAsync(ct);
        return new OrderResponse(
            o.Id, o.OrderNo, o.GetEffectiveStatus(time.GetUtcNow().UtcDateTime),
            o.SubTotal, o.DiscountAmount, o.ShippingFee, o.Total, o.PayableAmount,
            o.CreatedAt, o.ExpiresAt, o.PaidAt, o.PromoCode?.Code,
            new OrderContactResponse(o.ContactName, o.Phone, o.Email),
            new OrderShippingResponse(o.ShippingOption.Type, o.ShippingOption.Name,
                isPost
                    ? new OrderAddressResponse(o.ShipName, o.ShipPhone, o.ShipAddress, o.ShipSubdistrictText,
                        o.ShipDistrictText, o.ShipProvinceText, o.ShipPostalCode)
                    : null,
                o.TrackingNo),
            o.Registrations.OrderBy(r => r.Id).Select(r => new OrderRunnerResponse(
                r.Id, r.TitleId, r.FirstName, r.LastName,
                new IdName(r.EventDistance.Id, r.EventDistance.Name),
                new ShirtSizeRef(r.EventShirtSize.Id, r.EventShirtSize.Size),
                r.PricePaid, r.BibNumber)).ToList(), payment,
            new OrderLineResponse(o.LineAccountId != null, o.LineAccount?.IsFollowing == true));
    }

    [HttpGet("tickets")]
    public async Task<IReadOnlyList<TicketResponse>> GetTickets(Guid orderId, CancellationToken ct)
    {
        var o = await LoadAsync(orderId, ct);
        var status = o.GetEffectiveStatus(time.GetUtcNow().UtcDateTime);
        if (status != OrderStatus.Paid) throw OrderErrors.OrderNotPaid(status);
        return o.Registrations.Where(r => !r.IsCancelled).OrderBy(r => r.Id)
            .Select(r => new TicketResponse(r.Id, r.FirstName, r.LastName, r.EventDistance.Name, r.BibNumber!, r.CheckInToken))
            .ToList();
    }

    private async Task<Order> LoadAsync(Guid orderId, CancellationToken ct) =>
        await db.Orders.AsNoTracking()
            .Include(o => o.ShippingOption)
            .Include(o => o.PromoCode)
            .Include(o => o.LineAccount)
            .Include(o => o.Registrations).ThenInclude(r => r.EventDistance)
            .Include(o => o.Registrations).ThenInclude(r => r.EventShirtSize)
            .AsSplitQuery()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
        ?? throw OrderErrors.OrderNotFound(orderId);
}
