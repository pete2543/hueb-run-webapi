using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HuebRun.Api;

/// config `RateLimits` — กัน bot จองโควตาค้างรอบละ 30 นาทีและเผาโควตา SlipOK (นับต่อ IP ในหน้าต่างเวลาเดียวกัน)
/// ค่าเริ่มต้นเผื่อหลายคนใช้ Wi-Fi/NAT เดียวกัน (เช่น ออฟฟิศ หน้างาน)
public sealed class RateLimitOptions
{
    [Range(1, 100_000)] public int OrdersPerWindow { get; set; } = 10;
    [Range(1, 100_000)] public int SlipsPerWindow { get; set; } = 20;
    [Range(1, 1440)] public int WindowMinutes { get; set; } = 10;
}

public static class RateLimiting
{
    public const string Login = "admin-login";
    public const string CreateOrder = "create-order";
    public const string UploadSlip = "upload-slip";

    public static void AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>().BindConfiguration("RateLimits").ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>, IOptions<RateLimitOptions>>((rl, admin, limits) =>
        {
            var window = TimeSpan.FromMinutes(limits.Value.WindowMinutes);
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rl.AddPolicy(Login, http => PerIp(http, admin.Value.LoginRateLimitPerMinute, TimeSpan.FromMinutes(1)));
            rl.AddPolicy(CreateOrder, http => PerIp(http, limits.Value.OrdersPerWindow, window));
            rl.AddPolicy(UploadSlip, http => PerIp(http, limits.Value.SlipsPerWindow, window));
            rl.OnRejected = (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    ctx.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString();
                return new ValueTask(AdminAuthSetup.WriteAsync(ctx.HttpContext, 429, "TOO_MANY_REQUESTS", "ส่งคำขอถี่เกินไป กรุณาลองใหม่อีกครั้งในอีกสักครู่"));
            };
        });
        services.AddRateLimiter(_ => { });
    }

    // IP จริงมาจาก X-Forwarded-For (UseForwardedHeaders ใน Program.cs) — แยกถังตามชื่อ policy จึงไม่ปนกัน
    private static RateLimitPartition<string> PerIp(HttpContext http, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });
}
