namespace HuebRun.Infrastructure.Admin;

public static class AdminRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Organizer = "Organizer";
    public const string Finance = "Finance";
    public const string Staff = "Staff";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Organizer, Finance, Staff];
}
