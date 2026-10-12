using HuebRun.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Admin;

public sealed class AdminAuthService(
    AppDbContext db, UserManager<AdminUser> users, AdminTokenService tokens, AuditWriter audit,
    TimeProvider time)
{
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // กันเดาว่าอีเมลมีในระบบจากเวลาตอบ
            users.PasswordHasher.HashPassword(new AdminUser(), password);
            await RecordFailureAsync(email.Trim().ToLowerInvariant(), null, ct);
            throw AdminErrors.InvalidCredentials();
        }

        if (!user.IsActive || await users.IsLockedOutAsync(user))
        {
            await RecordFailureAsync(user.Id.ToString(), user.Id, ct);
            throw AdminErrors.InvalidCredentials();
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);   // ครบ MaxFailedAttempts จะล็อกเอง
            await RecordFailureAsync(user.Id.ToString(), user.Id, ct);
            throw AdminErrors.InvalidCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);
        user.LastLoginAt = time.GetUtcNow().UtcDateTime;
        await users.UpdateAsync(user);
        audit.Add("auth.login", "AdminUser", user.Id.ToString(), actingUserId: user.Id);
        await db.SaveChangesAsync(ct);

        var (token, expires) = tokens.Issue(user);
        return new LoginResult(token, expires, await AdminUserService.ToInfoAsync(db, users, user, ct));
    }

    public async Task<AdminUserInfo> MeAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Set<AdminUser>().Include(u => u.EventAccesses).SingleAsync(u => u.Id == userId, ct);
        return await AdminUserService.ToInfoAsync(db, users, user, ct);
    }

    public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AdminErrors.UserNotFound(userId);
        if (!await users.CheckPasswordAsync(user, currentPassword))
            throw AdminErrors.WrongPassword();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword);   // เปลี่ยน SecurityStamp → token เก่าใช้ไม่ได้
        if (!result.Succeeded) throw AdminErrors.WeakPassword(result.Errors.Select(e => e.Description));
        audit.Add("auth.change_password", "AdminUser", user.Id.ToString());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task RecordFailureAsync(string entityId, Guid? userId, CancellationToken ct)
    {
        audit.Add("auth.login_failed", "AdminUser", entityId, actingUserId: userId);
        await db.SaveChangesAsync(ct);
    }
}
