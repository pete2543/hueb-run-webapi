using HuebRun.Domain;
using HuebRun.Infrastructure;

namespace HuebRun.Tests.Infrastructure;

public static class TestData
{
    public static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    // Event ที่ publish แล้ว เปิดรับ 2026-10-01 ถึง 2027-01-31 slug ไม่ซ้ำกันทุกครั้ง
    public static async Task<Event> CreateEventAsync(AppDbContext db, Action<Event>? configure = null)
    {
        var e = new Event
        {
            Name = "Test Run",
            Slug = "t-" + Guid.NewGuid().ToString("N")[..12],
            Year = 2027,
            EventDate = Utc(2027, 2, 14),
            RegistrationOpensAt = Utc(2026, 10, 1),
            RegistrationClosesAt = Utc(2027, 1, 31),
            PromptPayId = "0812345678",
            PromptPayAccountName = "SECRET ACCOUNT",
            IsPublished = true,
        };
        configure?.Invoke(e);
        db.Events.Add(e);
        await db.SaveChangesAsync();
        return e;
    }
}
