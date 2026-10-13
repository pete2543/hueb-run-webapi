using System.ComponentModel.DataAnnotations;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Line;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

public sealed record LinkLineRequest([Required] string IdToken);

/// ออเดอร์ที่ผูกกับ LINE ของนักวิ่ง (หน้า "ตั๋วของฉัน") — ตั๋ว/QR ดึงต่อจาก GET /orders/{orderId}/tickets
public sealed record LineOrderResponse(
    Guid OrderId, string OrderNo, string EventName, OrderStatus Status, decimal Total,
    int RunnerCount, ShippingType DeliveryType, string? TrackingNo, DateTime CreatedAt);

[ApiController]
public sealed class LineController(
    LineWebhookService webhook, LineLinkService link, ILineIdentityVerifier verifier, AppDbContext db, TimeProvider time) : ControllerBase
{
    /// webhook ของ LINE OA (follow/unfollow) — ตั้ง URL นี้ใน LINE Developers Console; ตรวจ X-Line-Signature
    [HttpPost("api/v1/line/webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        Request.EnableBuffering();
        string body;
        using (var reader = new StreamReader(Request.Body, leaveOpen: true))
            body = await reader.ReadToEndAsync(ct);
        if (!webhook.IsSignatureValid(body, Request.Headers["X-Line-Signature"].FirstOrDefault()))
            return Unauthorized();
        await webhook.HandleAsync(body, ct);
        return Ok();
    }

    /// นักวิ่งล็อกอิน LINE (LIFF) แล้วส่ง ID token มาผูกกับออเดอร์ — ใช้ orderId (GUID) เป็นตัวยืนยันตามสัญญาฝั่งนักวิ่ง
    [HttpPost("api/v1/orders/{orderId:guid}/line")]
    [EnableRateLimiting(RateLimiting.UploadSlip)]
    public async Task<IActionResult> LinkOrder(Guid orderId, LinkLineRequest req, CancellationToken ct)
    {
        await link.LinkOrderAsync(orderId, req.IdToken, ct);
        return NoContent();
    }

    /// ออเดอร์ทั้งหมดที่ผูกกับ LINE คนนี้ (ID token จาก LIFF) ใหม่สุดก่อน ไม่รวมที่หมดเวลา/ยกเลิก
    [HttpPost("api/v1/line/me/orders")]
    [EnableRateLimiting(RateLimiting.UploadSlip)]
    public async Task<IReadOnlyList<LineOrderResponse>> MyOrders(LinkLineRequest req, CancellationToken ct)
    {
        var identity = string.IsNullOrWhiteSpace(req.IdToken) ? null : await verifier.VerifyIdTokenAsync(req.IdToken.Trim(), ct);
        if (identity is null) throw LineErrors.InvalidToken();

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.LineAccount!.LineUserId == identity.UserId)
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.OrderNo)   // เลขออเดอร์รันต่อกัน ใช้ตัดสินเมื่อเวลาเท่ากัน
            .Select(o => new
            {
                o.Id, o.OrderNo, EventName = o.Event.Name, o.Status, o.ExpiresAt, o.Total,
                RunnerCount = o.Registrations.Count(r => !r.IsCancelled),
                o.ShippingOption.Type, o.TrackingNo, o.CreatedAt,
            })
            .ToListAsync(ct);

        var now = time.GetUtcNow().UtcDateTime;
        return orders
            .Select(o => new LineOrderResponse(o.Id, o.OrderNo, o.EventName,
                o.Status == OrderStatus.Pending && now >= o.ExpiresAt ? OrderStatus.Expired : o.Status,
                o.Total, o.RunnerCount, o.Type, o.TrackingNo, o.CreatedAt))
            .Where(o => o.Status is not (OrderStatus.Expired or OrderStatus.Cancelled))
            .ToList();
    }
}
