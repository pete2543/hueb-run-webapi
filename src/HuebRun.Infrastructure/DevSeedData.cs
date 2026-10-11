using HuebRun.Domain;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Infrastructure;

/// ข้อมูลงานตัวอย่างสำหรับ Development เท่านั้น ให้หน้าบ้านมีข้อมูลเรียกได้ทันที
public static class DevSeedData
{
    public const string Slug = "hueb-run-2027";

    public static async Task RunAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Events.AnyAsync(e => e.Slug == Slug, ct)) return;

        static DateTime Utc(int y, int m, int d, int h = 0) => new(y, m, d, h, 0, 0, DateTimeKind.Utc);
        var earlyBirdEnds = Utc(2026, 11, 30);

        db.Events.Add(new Event
        {
            Name = "Hueb Run 2027",
            Slug = Slug,
            Year = 2027,
            EventDate = Utc(2027, 2, 13, 22),          // 05:00 เวลาไทย 14 ก.พ. 2027
            RegistrationOpensAt = Utc(2026, 10, 1),
            RegistrationClosesAt = Utc(2027, 1, 31),
            PromptPayId = "0000000000",
            PromptPayAccountName = "บัญชีตัวอย่าง (dev)",
            IsPublished = true,
            Distances =
            {
                new() { Name = "Mini 3K", DistanceKm = 3, BibPrefix = "A", Quota = 300, Price = 450, EarlyBirdPrice = 390, EarlyBirdEndsAt = earlyBirdEnds },
                new() { Name = "Fun Run 5K", DistanceKm = 5, BibPrefix = "B", Quota = 400, Price = 550, EarlyBirdPrice = 490, EarlyBirdEndsAt = earlyBirdEnds },
                new() { Name = "Mini Marathon 10K", DistanceKm = 10, BibPrefix = "C", Quota = 300, Price = 650, EarlyBirdPrice = 590, EarlyBirdEndsAt = earlyBirdEnds },
            },
            ShirtSizes =
            {
                new() { Size = "XS", Detail = "รอบอก 34 นิ้ว", Stock = 80 },
                new() { Size = "S", Detail = "รอบอก 36 นิ้ว", Stock = 180 },
                new() { Size = "M", Detail = "รอบอก 38 นิ้ว", Stock = 250 },
                new() { Size = "L", Detail = "รอบอก 40 นิ้ว", Stock = 250 },
                new() { Size = "XL", Detail = "รอบอก 42 นิ้ว", Stock = 180 },
                new() { Size = "3XL", Detail = "รอบอก 46 นิ้ว", Stock = 80 },
            },
            ShippingOptions =
            {
                new() { Type = ShippingType.Pickup, Name = "รับเองหน้างาน", Fee = 0 },
                new() { Type = ShippingType.Post, Name = "ส่งไปรษณีย์ (EMS)", Fee = 60 },
            },
        });
        await db.SaveChangesAsync(ct);
    }
}
