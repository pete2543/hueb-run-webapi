using System.ComponentModel.DataAnnotations;
using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HuebRun.Api.Controllers.Admin;

public sealed record LoginRequest([Required] string Email, [Required] string Password);
public sealed record ChangePasswordRequest([Required] string CurrentPassword, [Required] string NewPassword);

[ApiController]
[Route("api/v1/admin/auth")]
public sealed class AdminAuthController(AdminAuthService auth, ICurrentAdmin current) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting(RateLimiting.Login)]
    public Task<LoginResult> Login(LoginRequest req, CancellationToken ct) =>
        auth.LoginAsync(req.Email, req.Password, ct);

    [HttpGet("me")]
    [Authorize(Policy = AdminPolicies.Any)]
    public Task<AdminUserInfo> Me(CancellationToken ct) => auth.MeAsync(current.UserId!.Value, ct);

    [HttpPost("change-password")]
    [Authorize(Policy = AdminPolicies.Any)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest req, CancellationToken ct)
    {
        await auth.ChangePasswordAsync(current.UserId!.Value, req.CurrentPassword, req.NewPassword, ct);
        return NoContent();
    }
}
