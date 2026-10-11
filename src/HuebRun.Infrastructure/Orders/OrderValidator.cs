using System.Net.Mail;
using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

/// ข้อมูลอ้างอิงของงานที่ใช้ตรวจคำขอ (โหลดครั้งเดียวต่อคำขอ)
public sealed class OrderReferenceData
{
    public required IReadOnlyDictionary<int, EventDistance> Distances { get; init; }
    public required IReadOnlyDictionary<int, EventShirtSize> ShirtSizes { get; init; }
    public required IReadOnlyDictionary<int, ShippingOption> ShippingOptions { get; init; }   // active เท่านั้น
    public required IReadOnlySet<int> TitleIds { get; init; }
    public required IReadOnlySet<int> NationalityIds { get; init; }
    public required IReadOnlySet<int> BloodTypeIds { get; init; }
    public required IReadOnlySet<int> RelationIds { get; init; }
    public Subdistrict? ShipSubdistrict { get; init; }                                         // รวม District.Province
}

public static class OrderValidator
{
    public const int MaxRunners = 10;

    /// คืน map ฟิลด์ → ข้อความ (ว่าง = ผ่าน)
    public static Dictionary<string, string[]> Validate(CreateOrderRequest request, OrderReferenceData data, DateTime utcNow)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list)) errors[field] = list = [];
            list.Add(message);
        }

        if (!request.AcceptedPdpa) Add("acceptedPdpa", "ต้องยอมรับนโยบายข้อมูลส่วนบุคคล");

        if (request.Contact is not { } contact) Add("contact", "กรุณากรอกข้อมูลผู้ติดต่อ");
        else
        {
            if (!HasLength(contact.Name, 1, 200)) Add("contact.name", "กรุณากรอกชื่อผู้ติดต่อ");
            if (NormalizePhone(contact.Phone) is null) Add("contact.phone", "เบอร์โทรต้องเป็นตัวเลข 9–10 หลัก");
            if (!IsBlank(contact.Email) && !IsEmail(contact.Email!)) Add("contact.email", "รูปแบบอีเมลไม่ถูกต้อง");
        }

        var runners = request.Runners ?? [];
        if (runners.Count is < 1 or > MaxRunners) Add("runners", $"ต้องมีนักวิ่ง 1–{MaxRunners} คน");
        var today = DateOnly.FromDateTime(utcNow);
        for (var i = 0; i < runners.Count && i < MaxRunners; i++)
        {
            var r = runners[i];
            var p = $"runners[{i}]";
            if (r.DistanceId is not { } d || !data.Distances.ContainsKey(d)) Add($"{p}.distanceId", "ไม่พบระยะนี้ในงาน");
            if (r.ShirtSizeId is not { } s || !data.ShirtSizes.ContainsKey(s)) Add($"{p}.shirtSizeId", "ไม่พบไซซ์เสื้อนี้ในงาน");
            if (!HasLength(r.FirstName, 1, 100)) Add($"{p}.firstName", "กรุณากรอกชื่อ (ไม่เกิน 100 ตัวอักษร)");
            if (!HasLength(r.LastName, 1, 100)) Add($"{p}.lastName", "กรุณากรอกนามสกุล (ไม่เกิน 100 ตัวอักษร)");
            if (r.Gender is null) Add($"{p}.gender", "กรุณาเลือกเพศ");
            if (r.BirthDate is not { } birth || birth > today) Add($"{p}.birthDate", "วันเกิดไม่ถูกต้อง");
            if (r.NationalityId is not { } n || !data.NationalityIds.Contains(n)) Add($"{p}.nationalityId", "กรุณาเลือกสัญชาติ");
            if (r.TitleId is { } t && !data.TitleIds.Contains(t)) Add($"{p}.titleId", "ไม่พบคำนำหน้านี้");
            if (r.BloodTypeId is { } b && !data.BloodTypeIds.Contains(b)) Add($"{p}.bloodTypeId", "ไม่พบกรุ๊ปเลือดนี้");
            if (!IsBlank(r.Phone) && NormalizePhone(r.Phone) is null) Add($"{p}.phone", "เบอร์โทรต้องเป็นตัวเลข 9–10 หลัก");
            if (!IsBlank(r.Email) && !IsEmail(r.Email!)) Add($"{p}.email", "รูปแบบอีเมลไม่ถูกต้อง");
            if (r.EmergencyContact is not { } ec || !HasLength(ec.Name, 1, 200) || NormalizePhone(ec.Phone) is null)
                Add($"{p}.emergencyContact", "กรุณากรอกชื่อและเบอร์ผู้ติดต่อฉุกเฉิน");
            else if (ec.RelationId is { } rel && !data.RelationIds.Contains(rel))
                Add($"{p}.emergencyContact.relationId", "ไม่พบความสัมพันธ์นี้");
        }

        if (request.ShippingOptionId is not { } so || !data.ShippingOptions.TryGetValue(so, out var option))
            Add("shippingOptionId", "กรุณาเลือกวิธีรับของ");
        else if (option.Type == ShippingType.Post)
        {
            if (request.ShippingAddress is not { } a) Add("shippingAddress", "กรุณากรอกที่อยู่จัดส่ง");
            else
            {
                if (!HasLength(a.Name, 1, 200)) Add("shippingAddress.name", "กรุณากรอกชื่อผู้รับ");
                if (NormalizePhone(a.Phone) is null) Add("shippingAddress.phone", "เบอร์โทรต้องเป็นตัวเลข 9–10 หลัก");
                if (!HasLength(a.Line1, 1, 300)) Add("shippingAddress.line1", "กรุณากรอกที่อยู่");
                if (data.ShipSubdistrict is null) Add("shippingAddress.subdistrictId", "ไม่พบตำบลนี้");
                else if (a.PostalCode?.Trim() != data.ShipSubdistrict.ZipCode)
                    Add("shippingAddress.postalCode", "รหัสไปรษณีย์ไม่ตรงกับตำบล");
            }
        }

        return errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    /// ตัด "-" และช่องว่าง แล้วต้องเหลือตัวเลข 9–10 หลัก ไม่งั้นคืน null
    public static string? NormalizePhone(string? raw)
    {
        if (raw is null) return null;
        var digits = raw.Replace("-", "").Replace(" ", "");
        return digits.Length is 9 or 10 && digits.All(c => c is >= '0' and <= '9') ? digits : null;
    }

    internal static string? Clean(string? value) => IsBlank(value) ? null : value!.Trim();

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool HasLength(string? value, int min, int max)
    {
        var length = value?.Trim().Length ?? 0;
        return length >= min && length <= max;
    }

    private static bool IsEmail(string value) =>
        MailAddress.TryCreate(value.Trim(), out var address) && address.Address == value.Trim();
}
