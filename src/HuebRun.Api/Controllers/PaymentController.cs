using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/orders/{orderId}/payment")]
public sealed class PaymentController(PaymentService payments, IOptions<MinioOptions> storage) : ControllerBase
{
    [HttpGet]
    public Task<PaymentInfo> Get(Guid orderId, CancellationToken ct) =>
        payments.GetPaymentInfoAsync(orderId, ct);

    [HttpPost("slip")]
    [EnableRateLimiting(RateLimiting.UploadSlip)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    public async Task<IActionResult> UploadSlip(Guid orderId, IFormFile? file, CancellationToken ct)
    {
        if (file is null) throw PaymentErrors.InvalidFile("กรุณาแนบรูปสลิป");
        await using var stream = file.OpenReadStream();
        var image = await SlipFile.ReadAsync(file.FileName, file.Length, stream, storage.Value, ct);
        var result = await payments.SubmitSlipAsync(orderId, image, ct);
        return result.Status == PaymentStatus.Verified ? Ok(result) : Accepted(result);
    }
}
