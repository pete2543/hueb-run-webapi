using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using RunEvent.Domain;

namespace RunEvent.Data;

public class AppDbContext : IdentityDbContext<AdminUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventDistance> EventDistances => Set<EventDistance>();
    public DbSet<EventShirtSize> EventShirtSizes => Set<EventShirtSize>();
    public DbSet<ShippingOption> ShippingOptions => Set<ShippingOption>();
    public DbSet<PromoCode> PromoCodes => Set<PromoCode>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Registration> Registrations => Set<Registration>();
    public DbSet<Payment> Payments => Set<Payment>();

    // master data
    public DbSet<Province> Provinces => Set<Province>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<Subdistrict> Subdistricts => Set<Subdistrict>();
    public DbSet<Title> Titles => Set<Title>();
    public DbSet<Nationality> Nationalities => Set<Nationality>();
    public DbSet<BloodType> BloodTypes => Set<BloodType>();
    public DbSet<EmergencyRelation> EmergencyRelations => Set<EmergencyRelation>();

    // admin / แจ้งเตือน
    public DbSet<AdminEventAccess> AdminEventAccesses => Set<AdminEventAccess>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<CheckInLog> CheckInLogs => Set<CheckInLog>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<LineAccount> LineAccounts => Set<LineAccount>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);   // ตาราง Identity (AspNetUsers ฯลฯ)

        // เก็บ enum เป็น string อ่านง่ายใน DB
        b.Entity<Order>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Payment>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<ShippingOption>().Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Entity<PromoCode>().Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Entity<Registration>().Property(x => x.Gender).HasConversion<string>().HasMaxLength(10);

        // เงิน: numeric(12,2)
        foreach (var p in b.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            p.SetColumnType("numeric(12,2)");

        // Concurrency token ด้วย xmin ของ Postgres (กันโควตา/สต็อก/โค้ดถูกใช้เกินตอนแย่งกัน)
        b.Entity<EventDistance>().Property(x => x.Version).IsRowVersion();
        b.Entity<EventShirtSize>().Property(x => x.Version).IsRowVersion();
        b.Entity<PromoCode>().Property(x => x.Version).IsRowVersion();

        // Event
        b.Entity<Event>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<Event>().HasIndex(x => x.Year);

        // ระยะ / ไซซ์: ห้ามชื่อซ้ำใน Event เดียวกัน
        b.Entity<EventDistance>().HasIndex(x => new { x.EventId, x.Name }).IsUnique();
        b.Entity<EventDistance>().ToTable(t => t.HasCheckConstraint("ck_distance_reserved", "\"ReservedCount\" >= 0 AND \"ReservedCount\" <= \"Quota\""));
        b.Entity<EventShirtSize>().HasIndex(x => new { x.EventId, x.Size }).IsUnique();
        b.Entity<EventShirtSize>().ToTable(t => t.HasCheckConstraint("ck_shirt_reserved", "\"ReservedCount\" >= 0 AND \"ReservedCount\" <= \"Stock\""));

        // โปรโมชัน: โค้ดไม่ซ้ำใน Event เดียวกัน
        b.Entity<PromoCode>().HasIndex(x => new { x.EventId, x.Code }).IsUnique();

        // Order
        b.Entity<Order>().HasIndex(x => x.OrderNo).IsUnique();
        b.Entity<Order>().HasIndex(x => new { x.EventId, x.Status });
        b.Entity<Order>().HasIndex(x => x.ExpiresAt).HasFilter("\"Status\" = 'Pending'"); // ให้ job หาออเดอร์หมดเวลาเร็ว
        // ยอดชำระ (รวมเศษสตางค์) ต้องไม่ซ้ำในออเดอร์ที่ยังรอจ่าย → จับคู่สลิปได้
        b.Entity<Order>().HasIndex(x => new { x.EventId, x.PayableAmount })
            .IsUnique().HasFilter("\"Status\" = 'Pending'");
        b.Entity<Order>().HasOne(x => x.Event).WithMany(e => e.Orders)
            .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);

        // Registration
        b.Entity<Registration>().HasOne(x => x.Order).WithMany(o => o.Registrations)
            .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Registration>().HasIndex(x => new { x.EventId, x.BibNumber })
            .IsUnique().HasFilter("\"BibNumber\" IS NOT NULL");
        b.Entity<Registration>().HasIndex(x => x.CheckInToken).IsUnique();
        b.Entity<Registration>().HasIndex(x => x.OrderId);
        b.Entity<Registration>().HasIndex(x => new { x.EventId, x.LastName, x.FirstName });

        // ---- Master data ----
        // Id ของที่อยู่มาจากแหล่งข้อมูล ไม่ให้ DB สร้างเอง
        b.Entity<Province>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<District>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Subdistrict>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<District>().HasOne(x => x.Province).WithMany(p => p.Districts)
            .HasForeignKey(x => x.ProvinceId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Subdistrict>().HasOne(x => x.District).WithMany(d => d.Subdistricts)
            .HasForeignKey(x => x.DistrictId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<District>().HasIndex(x => x.ProvinceId);
        b.Entity<Subdistrict>().HasIndex(x => x.DistrictId);
        b.Entity<Subdistrict>().HasIndex(x => x.ZipCode);   // ค้นหาจากรหัสไปรษณีย์

        // ผูก Order/Registration กับ master แบบห้ามลบ master ที่ถูกใช้อยู่
        b.Entity<Order>().HasOne(x => x.ShipSubdistrict).WithMany()
            .HasForeignKey(x => x.ShipSubdistrictId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Registration>().HasOne(x => x.Title).WithMany()
            .HasForeignKey(x => x.TitleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Registration>().HasOne(x => x.Nationality).WithMany()
            .HasForeignKey(x => x.NationalityId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Registration>().HasOne(x => x.BloodType).WithMany()
            .HasForeignKey(x => x.BloodTypeId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Registration>().HasOne(x => x.EmergencyRelation).WithMany()
            .HasForeignKey(x => x.EmergencyRelationId).OnDelete(DeleteBehavior.Restrict);

        // ---- Admin ----
        b.Entity<AdminUser>().Property(x => x.DisplayName).HasMaxLength(100);
        b.Entity<AdminEventAccess>().HasKey(x => new { x.AdminUserId, x.EventId });
        b.Entity<AdminEventAccess>().HasOne(x => x.AdminUser).WithMany(u => u.EventAccesses)
            .HasForeignKey(x => x.AdminUserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AdminEventAccess>().HasOne(x => x.Event).WithMany()
            .HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);

        // ผู้ใช้แอดมินที่ถูกอ้างใน log ลบไม่ได้ (ใช้ IsActive ปิดแทน)
        b.Entity<AuditLog>().HasOne<AdminUser>().WithMany()
            .HasForeignKey(x => x.AdminUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CheckInLog>().HasOne<AdminUser>().WithMany()
            .HasForeignKey(x => x.StaffUserId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Broadcast>().HasOne<AdminUser>().WithMany()
            .HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Refund>().HasOne<AdminUser>().WithMany()
            .HasForeignKey(x => x.RequestedById).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Refund>().HasOne<AdminUser>().WithMany()
            .HasForeignKey(x => x.ApprovedById).OnDelete(DeleteBehavior.Restrict);

        b.Entity<AuditLog>().Property(x => x.OldValues).HasColumnType("jsonb");
        b.Entity<AuditLog>().Property(x => x.NewValues).HasColumnType("jsonb");
        b.Entity<AuditLog>().HasIndex(x => new { x.EntityType, x.EntityId });
        b.Entity<AuditLog>().HasIndex(x => x.CreatedAt);

        b.Entity<CheckInLog>().Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        b.Entity<CheckInLog>().HasIndex(x => x.RegistrationId);

        b.Entity<Refund>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Refund>().HasIndex(x => x.OrderId);

        // ---- LINE / แจ้งเตือน ----
        b.Entity<LineAccount>().HasIndex(x => x.LineUserId).IsUnique();
        b.Entity<Order>().HasOne(x => x.LineAccount).WithMany()
            .HasForeignKey(x => x.LineAccountId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<NotificationTemplate>().Property(x => x.Channel).HasConversion<string>().HasMaxLength(10);
        b.Entity<NotificationTemplate>().HasIndex(x => new { x.Key, x.Channel }).IsUnique();

        b.Entity<Notification>().Property(x => x.Channel).HasConversion<string>().HasMaxLength(10);
        b.Entity<Notification>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Notification>().Property(x => x.Payload).HasColumnType("jsonb");
        b.Entity<Notification>().HasIndex(x => new { x.Status, x.ScheduledAt });     // job หยิบข้อความที่ต้องส่ง
        b.Entity<Notification>().HasIndex(x => x.DedupeKey).IsUnique().HasFilter("\"DedupeKey\" IS NOT NULL");
        b.Entity<Notification>().HasIndex(x => x.OrderId);
        b.Entity<Notification>().HasOne(x => x.Order).WithMany()
            .HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<Broadcast>().Property(x => x.Channel).HasConversion<string>().HasMaxLength(10);
        b.Entity<Broadcast>().Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Entity<Broadcast>().HasIndex(x => new { x.EventId, x.Status });

        b.Entity<AppSetting>().HasKey(x => x.Key);

        // Payment: กันสลิปซ้ำ
        b.Entity<Payment>().HasIndex(x => x.SlipHash).IsUnique().HasFilter("\"SlipHash\" IS NOT NULL");
        b.Entity<Payment>().HasIndex(x => x.SlipTransRef).IsUnique().HasFilter("\"SlipTransRef\" IS NOT NULL");
        b.Entity<Payment>().HasIndex(x => new { x.OrderId, x.Status });
        b.Entity<Payment>().Property(x => x.ProviderResponse).HasColumnType("jsonb");
    }
}
