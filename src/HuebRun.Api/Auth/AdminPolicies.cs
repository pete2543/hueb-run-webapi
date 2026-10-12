using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;

namespace HuebRun.Api.Auth;

/// ตารางสิทธิ์ต่อบทบาท (docs/admin-api-plan.md หัวข้อ 1) เก็บที่นี่ที่เดียว; controller อ้างด้วย [Authorize(Policy = ...)]
public static class AdminPolicies
{
    public const string Any = "Admin";                   // แอดมินทุกบทบาทที่ login แล้ว
    public const string Overview = "AdminOverview";      // แดชบอร์ด ออเดอร์ นักวิ่ง
    public const string Payments = "AdminPayments";      // ตรวจสลิป คืนเงิน
    public const string CheckIn = "AdminCheckIn";        // เช็กอิน
    public const string EventSettings = "AdminEventSettings";   // โควตา ตั้งค่างาน โค้ดส่วนลด ประกาศ
    public const string Users = "AdminUsers";            // ผู้ใช้ audit

    private static readonly Dictionary<string, string[]> Roles = new()
    {
        [Any] = [.. AdminRoles.All],
        [Overview] = [AdminRoles.SuperAdmin, AdminRoles.Organizer, AdminRoles.Finance],
        [Payments] = [AdminRoles.SuperAdmin, AdminRoles.Finance],
        [CheckIn] = [AdminRoles.SuperAdmin, AdminRoles.Organizer, AdminRoles.Staff],
        [EventSettings] = [AdminRoles.SuperAdmin, AdminRoles.Organizer],
        [Users] = [AdminRoles.SuperAdmin],
    };

    public static void AddAdminPolicies(this AuthorizationOptions options)
    {
        foreach (var (name, roles) in Roles)
            options.AddPolicy(name, p => p.RequireAuthenticatedUser().RequireRole(roles));
    }
}
