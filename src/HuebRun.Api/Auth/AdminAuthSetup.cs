using System.Security.Claims;
using HuebRun.Api.Errors;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

namespace HuebRun.Api.Auth;

public static class AdminAuthSetup
{
    public const string LoginRateLimit = "admin-login";

    public static void AddAdminAuth(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<HuebRun.Infrastructure.Admin.ICurrentAdmin, CurrentAdmin>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonAuthorizationResultHandler>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.Events = new JwtBearerEvents
            {
                OnTokenValidated = ValidateAgainstDatabaseAsync,
                OnChallenge = c =>
                {
                    c.HandleResponse();
                    return WriteAsync(c.HttpContext, 401, "UNAUTHORIZED", "ต้องเข้าสู่ระบบ");
                },
            };
        });
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AdminOptions>, TimeProvider>((jwt, admin, time) =>
            {
                var a = admin.Value;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = a.Issuer,
                    ValidAudience = a.Issuer,
                    IssuerSigningKey = AdminTokenService.SigningKey(a),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromMinutes(1),
                    // ใช้ TimeProvider เดียวกับตอนออก token (test ใช้เวลาปลอมได้)
                    LifetimeValidator = (notBefore, expires, _, p) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        return expires is not null
                               && expires.Value >= now - p.ClockSkew
                               && (notBefore is null || notBefore.Value <= now + p.ClockSkew);
                    },
                    NameClaimType = "sub",
                    RoleClaimType = ClaimTypes.Role,
                };
            });

        services.AddAuthorization(o => o.AddAdminPolicies());
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<AdminOptions>>((rl, admin) =>
        {
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            rl.AddPolicy(LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = admin.Value.LoginRateLimitPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));
            rl.OnRejected = (ctx, _) => new ValueTask(WriteAsync(ctx.HttpContext, 429, "TOO_MANY_REQUESTS", "ลองใหม่อีกครั้งในอีกสักครู่"));
        });
        services.AddRateLimiter(_ => { });
    }

    // token ถูกต้องแล้วยังต้องตรง DB: บัญชีเปิดอยู่, SecurityStamp ตรง (ปิดบัญชี/เปลี่ยนรหัส/บทบาท = ใช้ token เดิมไม่ได้ทันที)
    // บทบาทและสิทธิ์รายงานโหลดจาก DB ทุก request
    private static async Task ValidateAgainstDatabaseAsync(TokenValidatedContext ctx)
    {
        var principal = ctx.Principal;
        var sub = principal?.FindFirstValue("sub");
        var stamp = principal?.FindFirstValue(AdminTokenService.StampClaim);
        if (!Guid.TryParse(sub, out var id) || stamp is null) { ctx.Fail("invalid token"); return; }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Set<AdminUser>().AsNoTracking().Include(u => u.EventAccesses)
            .SingleOrDefaultAsync(u => u.Id == id, ctx.HttpContext.RequestAborted);
        if (user is null || !user.IsActive || user.SecurityStamp != stamp) { ctx.Fail("revoked"); return; }

        var role = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == id select r.Name)
            .SingleOrDefaultAsync(ctx.HttpContext.RequestAborted);
        var identity = (ClaimsIdentity)principal!.Identity!;
        if (role is not null) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        foreach (var a in user.EventAccesses)
            identity.AddClaim(new Claim("event", a.EventId.ToString()));
    }

    internal static Task WriteAsync(HttpContext http, int status, string code, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ErrorResponse(code, message, null));
    }
}

/// 403 ใช้ error body กลางเหมือน endpoint อื่น (401 ถูกจัดการที่ JwtBearer OnChallenge)
public sealed class JsonAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && context.User.Identity?.IsAuthenticated == true)
            return AdminAuthSetup.WriteAsync(context, 403, "FORBIDDEN", "ไม่มีสิทธิ์ทำรายการนี้");
        return _default.HandleAsync(next, context, policy, result);
    }
}
