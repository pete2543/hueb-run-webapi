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

    // งานพร้อมรับสมัคร: 5K early bird 400 (ปกติ 500) ถึง 2026-11-30, 10K 700, เสื้อ M/L, รับเอง/EMS 60
    public static Task<Event> CreateRegistrationEventAsync(AppDbContext db, Action<Event>? configure = null) =>
        CreateEventAsync(db, e =>
        {
            e.Distances.Add(new EventDistance { Name = "5K", DistanceKm = 5, BibPrefix = "B", Quota = 100,
                Price = 500, EarlyBirdPrice = 400, EarlyBirdEndsAt = Utc(2026, 11, 30) });
            e.Distances.Add(new EventDistance { Name = "10K", DistanceKm = 10, BibPrefix = "C", Quota = 100, Price = 700 });
            e.ShirtSizes.Add(new EventShirtSize { Size = "M", Stock = 100 });
            e.ShirtSizes.Add(new EventShirtSize { Size = "L", Stock = 100 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Pickup, Name = "รับเอง", Fee = 0 });
            e.ShippingOptions.Add(new ShippingOption { Type = ShippingType.Post, Name = "EMS", Fee = 60 });
            configure?.Invoke(e);
        });
}
