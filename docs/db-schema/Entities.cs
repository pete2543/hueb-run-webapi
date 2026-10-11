namespace RunEvent.Domain;

// ---------- Enums ----------
public enum OrderStatus { Pending, Paid, Expired, Cancelled, Refunded }
public enum PaymentStatus { Pending, Verifying, NeedsReview, Verified, Rejected }
public enum ShippingType { Pickup, Post }
public enum DiscountType { Percent, Fixed }
public enum Gender { Male, Female, Other }

// ---------- Event (1 ปี = 1 Event) ----------
public class Event
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";            // เช่น "fun-run-2027"
    public int Year { get; set; }
    public DateTime EventDate { get; set; }
    public DateTime RegistrationOpensAt { get; set; }
    public DateTime RegistrationClosesAt { get; set; }

    // บัญชีรับเงินแยกต่อ Event (เปลี่ยนได้ทีละปี ไม่ฝังโค้ด)
    public string PromptPayId { get; set; } = "";
    public string PromptPayAccountName { get; set; } = "";

    public int ReservationMinutes { get; set; } = 30;  // เวลาล็อกโควตารอจ่ายเงิน
    public bool IsPublished { get; set; }

    public List<EventDistance> Distances { get; set; } = new();
    public List<EventShirtSize> ShirtSizes { get; set; } = new();
    public List<ShippingOption> ShippingOptions { get; set; } = new();
    public List<PromoCode> PromoCodes { get; set; } = new();
    public List<Order> Orders { get; set; } = new();
}

// ---------- ระยะ 3 / 5 / 10 กม. ----------
public class EventDistance
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string Name { get; set; } = "";             // "Mini 3K"
    public decimal DistanceKm { get; set; }
    public string BibPrefix { get; set; } = "";        // "A", "B", "C"
    public int Quota { get; set; }
    public int ReservedCount { get; set; }             // รวมที่จองไว้ + จ่ายแล้ว (อัปเดตแบบ atomic)

    public decimal Price { get; set; }
    public decimal? EarlyBirdPrice { get; set; }
    public DateTime? EarlyBirdEndsAt { get; set; }

    public uint Version { get; set; }                  // Postgres xmin (concurrency token)
}

// ---------- ไซซ์เสื้อ + สต็อก ----------
public class EventShirtSize
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string Size { get; set; } = "";             // S, M, L, XL...
    public string? Detail { get; set; }                // "รอบอก 38 นิ้ว"
    public int Stock { get; set; }
    public int ReservedCount { get; set; }
    public uint Version { get; set; }
}

// ---------- วิธีรับของ: รับเอง / ไปรษณีย์ (ค่าส่งต่อ Order) ----------
public class ShippingOption
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public ShippingType Type { get; set; }
    public string Name { get; set; } = "";
    public decimal Fee { get; set; }
    public bool IsActive { get; set; } = true;
}

// ---------- โค้ดส่วนลด ----------
public class PromoCode
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public string Code { get; set; } = "";
    public DiscountType Type { get; set; }
    public decimal Value { get; set; }                 // % หรือจำนวนเงิน
    public decimal? MinOrderAmount { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int? MaxUses { get; set; }
    public int UsedCount { get; set; }
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

// ---------- Order = ใบสั่งซื้อ / ผู้จ่ายเงิน (1 Order มีนักวิ่งได้หลายคน) ----------
public class Order
{
    public Guid Id { get; set; }
    public string OrderNo { get; set; } = "";          // เลขอ่านง่าย เช่น "RUN27-000123"
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    // ผู้สั่ง (คนจ่ายเงิน/ติดต่อ)
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public int? LineAccountId { get; set; }
    public LineAccount? LineAccount { get; set; }            // สำหรับส่งแจ้งเตือนผ่าน LINE OA

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    // ยอดเงิน (เก็บ snapshot ไว้ ราคาเปลี่ยนทีหลังไม่กระทบ)
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal Total { get; set; }
    public decimal PayableAmount { get; set; }         // Total + เศษสตางค์ไม่ซ้ำ ใช้จับคู่ยอด

    public int? PromoCodeId { get; set; }
    public PromoCode? PromoCode { get; set; }

    public int ShippingOptionId { get; set; }
    public ShippingOption ShippingOption { get; set; } = null!;
    public string? ShipName { get; set; }
    public string? ShipPhone { get; set; }
    public string? ShipAddress { get; set; }           // บ้านเลขที่ ถนน หมู่บ้าน
    // FK ไว้ทำ dropdown/รายงาน + ข้อความ snapshot กัน master เปลี่ยนแล้วออเดอร์เก่าเพี้ยน
    public int? ShipSubdistrictId { get; set; }
    public Subdistrict? ShipSubdistrict { get; set; }
    public string? ShipSubdistrictText { get; set; }
    public string? ShipDistrictText { get; set; }
    public string? ShipProvinceText { get; set; }
    public string? ShipPostalCode { get; set; }
    public string? TrackingNo { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }            // หมดเวลา → Hangfire คืนโควตา
    public DateTime? PaidAt { get; set; }

    public List<Registration> Registrations { get; set; } = new();
    public List<Payment> Payments { get; set; } = new();
}

// ---------- Registration = นักวิ่ง 1 คน ----------
public class Registration
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;
    public int EventId { get; set; }                   // denormalize ไว้ทำ unique เลขบิบต่อ Event

    public int EventDistanceId { get; set; }
    public EventDistance EventDistance { get; set; } = null!;
    public int EventShirtSizeId { get; set; }
    public EventShirtSize EventShirtSize { get; set; } = null!;

    public decimal PricePaid { get; set; }             // snapshot ราคาตอนสมัคร

    // ข้อมูลนักวิ่ง
    public int? TitleId { get; set; }
    public Title? Title { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public Gender Gender { get; set; }
    public int NationalityId { get; set; }
    public Nationality Nationality { get; set; } = null!;
    public int? BloodTypeId { get; set; }
    public BloodType? BloodType { get; set; }
    public DateOnly BirthDate { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? IdCardOrPassport { get; set; }      // ควรเข้ารหัส/จำกัดสิทธิ์ (PDPA)
    public string? TeamName { get; set; }
    public int? EmergencyRelationId { get; set; }
    public EmergencyRelation? EmergencyRelation { get; set; }
    public string EmergencyContactName { get; set; } = "";
    public string EmergencyContactPhone { get; set; } = "";
    public string? MedicalNote { get; set; }

    // เช็กอิน
    public string? BibNumber { get; set; }             // ออกเลขหลังจ่ายเงินสำเร็จ
    public string CheckInToken { get; set; } = "";     // ใส่ใน QR
    public DateTime? CheckedInAt { get; set; }
    public string? CheckedInBy { get; set; }
    public bool IsCancelled { get; set; }
}

// ---------- Payment (รองรับหลาย provider: Slip / Gateway) ----------
public class Payment
{
    public int Id { get; set; }
    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public string Provider { get; set; } = "";         // "SlipVerify", "Omise", ...
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public decimal ExpectedAmount { get; set; }
    public decimal? PaidAmount { get; set; }

    public string? SlipImageUrl { get; set; }
    public string? SlipHash { get; set; }              // กันสลิปซ้ำ
    public string? SlipTransRef { get; set; }          // เลขอ้างอิงธุรกรรมจากสลิป (unique)
    public string? ExternalRef { get; set; }           // charge id ของ gateway (อนาคต)
    public string? ProviderResponse { get; set; }      // jsonb เก็บผลตรวจดิบ

    public string? RejectReason { get; set; }
    public string? ReviewedBy { get; set; }            // แอดมินที่ตรวจมือ
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
}
