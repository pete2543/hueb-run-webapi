using HuebRun.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Admin;

public static class AdminBootstrap
{
    /// ถ้ายังไม่มี SuperAdmin เลย และตั้ง Admin:BootstrapEmail/Password → สร้างให้หนึ่งคน
    public static async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var o = services.GetRequiredService<IOptions<AdminOptions>>().Value;
        if (string.IsNullOrWhiteSpace(o.BootstrapEmail) || string.IsNullOrEmpty(o.BootstrapPassword)) return;

        var users = services.GetRequiredService<UserManager<AdminUser>>();
        var logger = services.GetRequiredService<ILogger<AdminUserService>>();
        if ((await users.GetUsersInRoleAsync(AdminRoles.SuperAdmin)).Count > 0) return;

        var email = o.BootstrapEmail.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            logger.LogWarning("Admin bootstrap skipped: {Email} already exists but is not a SuperAdmin", email);
            return;
        }

        var db = services.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = new AdminUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = "Super Admin",
            IsActive = true, LockoutEnabled = true, CreatedAt = DateTime.UtcNow,
        };
        var created = await users.CreateAsync(user, o.BootstrapPassword);
        if (!created.Succeeded)
        {
            logger.LogError("Admin bootstrap failed: {Errors}", string.Join("; ", created.Errors.Select(e => e.Code)));
            return;
        }
        await users.AddToRoleAsync(user, AdminRoles.SuperAdmin);
        services.GetRequiredService<AuditWriter>().Add("user.bootstrap", "AdminUser", user.Id.ToString(), actingUserId: user.Id,
            newValues: new { email, role = AdminRoles.SuperAdmin });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogWarning("Created bootstrap SuperAdmin {Email}; remove Admin:Bootstrap* settings and change the password", email);
    }
}
