using System.ComponentModel.DataAnnotations;
using HuebRun.Infrastructure.Line;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HuebRun.Api.Controllers;

public sealed record LinkLineRequest([Required] string IdToken);

[ApiController]
public sealed class LineController(LineWebhookService webhook, LineLinkService link) : ControllerBase
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
}
