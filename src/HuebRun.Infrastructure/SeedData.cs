using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using HuebRun.Domain;

namespace HuebRun.Infrastructure;

/// เรียกตอนเริ่มแอป: await SeedData.RunAsync(db, SeedData.GeoDir);
/// - ตัวเลือกทั่วไป/roles/เทมเพลต: seed ถ้าตารางว่าง
/// - จังหวัด/อำเภอ/ตำบล: อ่านจาก Seed/geo/*.json (kongvut/thai-province-data api/v2) ถ้าตารางว่าง
public static class SeedData
{
    public static string GeoDir => Path.Combine(AppContext.BaseDirectory, "Seed", "geo");

    public static async Task RunAsync(AppDbContext db, string seedDir, CancellationToken ct = default)
    {
        await SeedLookupsAsync(db, ct);
        await SeedThaiGeoAsync(db, seedDir, ct);
        await SeedRolesAsync(db, ct);
        await SeedTemplatesAsync(db, ct);
    }

    private static async Task SeedRolesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(ct)) return;
        foreach (var name in new[] { "SuperAdmin", "Organizer", "Finance", "Staff" })
            db.Roles.Add(new IdentityRole<Guid>
            {
                Id = Guid.NewGuid(),
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });
        await db.SaveChangesAsync(ct);
    }

    // เทมเพลตเริ่มต้น (แก้ได้จากหลังบ้าน) placeholder เช่น {{Name}} {{OrderNo}} {{Total}} {{ExpiresAt}} {{EventName}}
    private static async Task SeedTemplatesAsync(AppDbContext db, CancellationToken ct)
    {
        if (await db.NotificationTemplates.AnyAsync(ct)) return;
        var now = DateTime.UtcNow;
        NotificationTemplate T(string key, string body) => new()
            { Key = key, Channel = NotificationChannel.Line, Body = body, UpdatedAt = now };

        db.NotificationTemplates.AddRange(
            T("order_created", "สวัสดีคุณ {{Name}} ได้รับใบสมัคร {{EventName}} แล้ว\nเลขที่ {{OrderNo}} ยอดชำระ {{Total}} บาท\nกรุณาชำระภายใน {{ExpiresAt}}"),
            T("order_paid", "ชำระเงินสำเร็จ ✅\nเลขที่ {{OrderNo}} ขอบคุณที่สมัคร {{EventName}}\nแสดง QR เช็กอินได้ที่ลิงก์: {{TicketUrl}}"),
            T("order_expiring", "ใบสมัคร {{OrderNo}} ใกล้หมดเวลาชำระเงิน ({{ExpiresAt}}) กรุณาชำระเพื่อรักษาสิทธิ์"),
            T("order_expired", "ใบสมัคร {{OrderNo}} หมดเวลาชำระเงินแล้ว หากยังสนใจสามารถสมัครใหม่ได้"),
            T("payment_rejected", "ไม่สามารถตรวจสอบสลิปของ {{OrderNo}} ได้: {{Reason}}\nกรุณาส่งสลิปใหม่หรือติดต่อทีมงาน"),
            T("shipping_sent", "จัดส่งชุดสมัคร {{OrderNo}} แล้ว เลขพัสดุ {{TrackingNo}}"),
            T("event_reminder", "ใกล้ถึงวันงาน {{EventName}} แล้ว! อย่าลืมนำ QR เช็กอินมาแสดงที่จุดรับบิบ"));
        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedLookupsAsync(AppDbContext db, CancellationToken ct)
    {
        if (!await db.Titles.AnyAsync(ct))
            db.Titles.AddRange(
                new Title { NameTh = "นาย", NameEn = "Mr.", SortOrder = 1 },
                new Title { NameTh = "นาง", NameEn = "Mrs.", SortOrder = 2 },
                new Title { NameTh = "นางสาว", NameEn = "Ms.", SortOrder = 3 },
                new Title { NameTh = "เด็กชาย", NameEn = "Master", SortOrder = 4 },
                new Title { NameTh = "เด็กหญิง", NameEn = "Miss", SortOrder = 5 });

        if (!await db.Nationalities.AnyAsync(ct))
            db.Nationalities.AddRange(
                new Nationality { NameTh = "ไทย", NameEn = "Thai", SortOrder = 1 },
                new Nationality { NameTh = "ลาว", NameEn = "Lao", SortOrder = 2 },
                new Nationality { NameTh = "เมียนมา", NameEn = "Myanmar", SortOrder = 3 },
                new Nationality { NameTh = "กัมพูชา", NameEn = "Cambodian", SortOrder = 4 },
                new Nationality { NameTh = "อื่น ๆ", NameEn = "Other", SortOrder = 99 });

        if (!await db.BloodTypes.AnyAsync(ct))
            db.BloodTypes.AddRange(
                new BloodType { NameTh = "A", NameEn = "A", SortOrder = 1 },
                new BloodType { NameTh = "B", NameEn = "B", SortOrder = 2 },
                new BloodType { NameTh = "AB", NameEn = "AB", SortOrder = 3 },
                new BloodType { NameTh = "O", NameEn = "O", SortOrder = 4 },
                new BloodType { NameTh = "ไม่ทราบ", NameEn = "Unknown", SortOrder = 99 });

        if (!await db.EmergencyRelations.AnyAsync(ct))
            db.EmergencyRelations.AddRange(
                new EmergencyRelation { NameTh = "พ่อ", NameEn = "Father", SortOrder = 1 },
                new EmergencyRelation { NameTh = "แม่", NameEn = "Mother", SortOrder = 2 },
                new EmergencyRelation { NameTh = "คู่สมรส", NameEn = "Spouse", SortOrder = 3 },
                new EmergencyRelation { NameTh = "พี่น้อง", NameEn = "Sibling", SortOrder = 4 },
                new EmergencyRelation { NameTh = "เพื่อน", NameEn = "Friend", SortOrder = 5 },
                new EmergencyRelation { NameTh = "อื่น ๆ", NameEn = "Other", SortOrder = 99 });

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedThaiGeoAsync(AppDbContext db, string seedDir, CancellationToken ct)
    {
        if (await db.Provinces.AnyAsync(ct)) return;

        // ข้าม record ที่ถูกลบ (deleted_at) และลูกที่แม่ไม่อยู่ในชุดข้อมูล เพื่อไม่ให้ FK พัง
        var provinces = (await ReadAsync<GeoDto>(Path.Combine(seedDir, "province.json"), ct))
            .Where(p => p.DeletedAt is null).ToList();
        var provinceIds = provinces.Select(p => p.Id).ToHashSet();
        var districts = (await ReadAsync<GeoDto>(Path.Combine(seedDir, "district.json"), ct))
            .Where(d => d.DeletedAt is null && d.ProvinceId is int pid && provinceIds.Contains(pid)).ToList();
        var districtIds = districts.Select(d => d.Id).ToHashSet();
        var subs = (await ReadAsync<GeoDto>(Path.Combine(seedDir, "sub_district.json"), ct))
            .Where(s => s.DeletedAt is null && s.DistrictId is int did && districtIds.Contains(did) && s.ZipCode is not null)
            .ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Provinces.AddRange(provinces.Select(p => new Province
            { Id = p.Id, NameTh = p.NameTh, NameEn = p.NameEn }));
        await db.SaveChangesAsync(ct);

        db.Districts.AddRange(districts.Select(d => new District
            { Id = d.Id, ProvinceId = d.ProvinceId!.Value, NameTh = d.NameTh, NameEn = d.NameEn }));
        await db.SaveChangesAsync(ct);

        // ตำบลเป็นหลักพันแถว แบ่งเป็นก้อนให้ SaveChanges ไม่หนักเกินไป
        foreach (var chunk in subs.Chunk(1000))
        {
            db.Subdistricts.AddRange(chunk.Select(s => new Subdistrict
            {
                Id = s.Id, DistrictId = s.DistrictId!.Value,
                NameTh = s.NameTh, NameEn = s.NameEn, ZipCode = s.ZipCode!.Value.ToString("D5")
            }));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<List<T>> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<T>>(fs, cancellationToken: ct) ?? new();
    }

    // ตรงกับ kongvut/thai-province-data api/v2 (zip_code เป็นตัวเลข)
    private sealed class GeoDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name_th")] public string NameTh { get; set; } = "";
        [JsonPropertyName("name_en")] public string? NameEn { get; set; }
        [JsonPropertyName("province_id")] public int? ProvinceId { get; set; }
        [JsonPropertyName("district_id")] public int? DistrictId { get; set; }
        [JsonPropertyName("zip_code")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public int? ZipCode { get; set; }
        [JsonPropertyName("deleted_at")] public string? DeletedAt { get; set; }
    }
}
