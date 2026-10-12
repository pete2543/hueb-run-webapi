using System.Text.Json;
using HuebRun.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure.Admin;

public sealed class AdminUserService(AppDbContext db, UserManager<AdminUser> users, AuditWriter audit, ICurrentAdmin current)
{
    public async Task<IReadOnlyList<AdminUserInfo>> ListAsync(CancellationToken ct = default)
    {
        var list = await db.Set<AdminUser>().Include(u => u.EventAccesses).OrderBy(u => u.DisplayName).ThenBy(u => u.Email).ToListAsync(ct);
        var result = new List<AdminUserInfo>(list.Count);
        foreach (var u in list) result.Add(await ToInfoAsync(db, users, u, ct));
        return result;
    }

    public async Task<AdminUserInfo> CreateAsync(CreateAdminUserRequest req, CancellationToken ct = default)
    {
        var name = req.Name?.Trim() ?? "";
        var email = req.Email?.Trim() ?? "";
        if (name.Length is 0 or > 100) throw AdminErrors.Validation("name", "ต้องมีชื่อ ไม่เกิน 100 ตัวอักษร");
        if (!IsEmail(email)) throw AdminErrors.Validation("email", "อีเมลไม่ถูกต้อง");
        if (!AdminRoles.All.Contains(req.Role)) throw AdminErrors.Validation("role", "บทบาทไม่ถูกต้อง");
        var eventIds = await ValidEventIdsAsync(req.Role, req.EventIds, ct);
        if (await users.FindByEmailAsync(email) is not null) throw AdminErrors.EmailTaken();

        var user = new AdminUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            DisplayName = name,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LockoutEnabled = true,
            EventAccesses = [.. eventIds.Select(id => new AdminEventAccess { EventId = id })],
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded) throw CreateFailure(created);
        var role = await users.AddToRoleAsync(user, req.Role);
        if (!role.Succeeded) throw CreateFailure(role);
        audit.Add("user.create", "AdminUser", user.Id.ToString(), newValues: new { name, email, role = req.Role, eventIds });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToInfoAsync(db, users, user, ct);
    }

    public async Task<AdminUserInfo> UpdateAsync(Guid id, UpdateAdminUserRequest req, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var user = await db.Set<AdminUser>().Include(u => u.EventAccesses).SingleOrDefaultAsync(u => u.Id == id, ct)
                   ?? throw AdminErrors.UserNotFound(id);
        var oldRole = (await users.GetRolesAsync(user)).Single();
        var before = new { name = user.DisplayName, role = oldRole, isActive = user.IsActive, eventIds = EventIds(user) };

        var newRole = req.Role ?? oldRole;
        var newActive = req.IsActive ?? user.IsActive;
        if (!AdminRoles.All.Contains(newRole)) throw AdminErrors.Validation("role", "บทบาทไม่ถูกต้อง");
        if (id == current.UserId && (newRole != oldRole || !newActive)) throw AdminErrors.CannotChangeSelf();
        if (oldRole == AdminRoles.SuperAdmin && user.IsActive && (newRole != oldRole || !newActive))
            await EnsureAnotherSuperAdminAsync(id, ct);

        if (req.Name is not null)
        {
            var name = req.Name.Trim();
            if (name.Length is 0 or > 100) throw AdminErrors.Validation("name", "ต้องมีชื่อ ไม่เกิน 100 ตัวอักษร");
            user.DisplayName = name;
        }
        user.IsActive = newActive;

        var revoke = newRole != oldRole || newActive != before.isActive;
        if (newRole != oldRole)
        {
            var removed = await users.RemoveFromRoleAsync(user, oldRole);
            if (!removed.Succeeded) throw CreateFailure(removed);
            var added = await users.AddToRoleAsync(user, newRole);
            if (!added.Succeeded) throw CreateFailure(added);
        }
        if (req.EventIds is not null || newRole != oldRole)
        {
            var ids = await ValidEventIdsAsync(newRole, req.EventIds ?? EventIds(user), ct);
            user.EventAccesses.Clear();
            user.EventAccesses.AddRange(ids.Select(e => new AdminEventAccess { AdminUserId = id, EventId = e }));
        }
        if (req.NewPassword is not null)
        {
            // ไม่ใช้ reset token (ไม่ได้เปิด token provider) — ตั้งรหัสใหม่ตรง ๆ โดยผ่าน password validator; ทั้งคู่เปลี่ยน SecurityStamp
            var removed = await users.RemovePasswordAsync(user);
            if (!removed.Succeeded) throw CreateFailure(removed);
            var reset = await users.AddPasswordAsync(user, req.NewPassword);
            if (!reset.Succeeded) throw AdminErrors.WeakPassword(reset.Errors.Select(e => e.Description));
            revoke = false;
        }
        if (revoke) await users.UpdateSecurityStampAsync(user);   // ปิดบัญชี/เปลี่ยนบทบาท → token เดิมใช้ไม่ได้ทันที
        if (req.NewPassword is not null) await users.ResetAccessFailedCountAsync(user);

        var result = await users.UpdateAsync(user);
        if (!result.Succeeded) throw CreateFailure(result);
        audit.Add("user.update", "AdminUser", id.ToString(), before,
            new { name = user.DisplayName, role = newRole, isActive = user.IsActive, eventIds = EventIds(user), passwordReset = req.NewPassword is not null });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await ToInfoAsync(db, users, user, ct);
    }

    internal static async Task<AdminUserInfo> ToInfoAsync(AppDbContext db, UserManager<AdminUser> users, AdminUser u, CancellationToken ct)
    {
        if (!db.Entry(u).Collection(x => x.EventAccesses).IsLoaded)
            await db.Entry(u).Collection(x => x.EventAccesses).LoadAsync(ct);
        var role = (await users.GetRolesAsync(u)).SingleOrDefault() ?? "";
        return new AdminUserInfo(u.Id, u.DisplayName, u.Email ?? "", role, u.IsActive, EventIds(u), u.CreatedAt, u.LastLoginAt);
    }

    private static List<int> EventIds(AdminUser u) => [.. u.EventAccesses.Select(a => a.EventId).Order()];

    // SuperAdmin เข้าได้ทุกงานอยู่แล้ว ไม่เก็บ access รายงาน
    private async Task<List<int>> ValidEventIdsAsync(string role, IReadOnlyList<int>? requested, CancellationToken ct)
    {
        if (role == AdminRoles.SuperAdmin || requested is not { Count: > 0 }) return [];
        var ids = requested.Distinct().Order().ToList();
        var found = await db.Events.CountAsync(e => ids.Contains(e.Id), ct);
        if (found != ids.Count) throw AdminErrors.Validation("eventIds", "มีงานที่ไม่พบในระบบ");
        return ids;
    }

    private async Task EnsureAnotherSuperAdminAsync(Guid excluding, CancellationToken ct)
    {
        var others = (await users.GetUsersInRoleAsync(AdminRoles.SuperAdmin)).Count(u => u.IsActive && u.Id != excluding);
        if (others == 0) throw AdminErrors.LastSuperAdmin();
    }

    private static bool IsEmail(string s) =>
        s.Length is > 3 and <= 256 && System.Net.Mail.MailAddress.TryCreate(s, out var a) && a.Address == s;

    private static AppException CreateFailure(IdentityResult r)
    {
        var codes = r.Errors.Select(e => e.Code).ToList();
        if (codes.Any(c => c.StartsWith("Password"))) return AdminErrors.WeakPassword(r.Errors.Select(e => e.Description));
        if (codes.Any(c => c.StartsWith("Duplicate"))) return AdminErrors.EmailTaken();
        return AdminErrors.Validation("user", string.Join("; ", r.Errors.Select(e => e.Description)));
    }
}
