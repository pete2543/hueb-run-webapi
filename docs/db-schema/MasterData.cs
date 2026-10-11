namespace RunEvent.Domain;

// ---------- ที่อยู่ไทย (Id มาจากแหล่งข้อมูล ไม่ auto-generate) ----------
public class Province
{
    public int Id { get; set; }
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }
    public List<District> Districts { get; set; } = new();
}

public class District
{
    public int Id { get; set; }
    public int ProvinceId { get; set; }
    public Province Province { get; set; } = null!;
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }
    public List<Subdistrict> Subdistricts { get; set; } = new();
}

public class Subdistrict
{
    public int Id { get; set; }
    public int DistrictId { get; set; }
    public District District { get; set; } = null!;
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }
    public string ZipCode { get; set; } = "";
}

// ---------- ตัวเลือกทั่วไป (แก้/เพิ่มได้จากหลังบ้าน) ----------
public abstract class LookupBase
{
    public int Id { get; set; }
    public string NameTh { get; set; } = "";
    public string? NameEn { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Title : LookupBase { }               // นาย / นาง / นางสาว / Mr. / Ms.
public class Nationality : LookupBase { }         // ไทย / ลาว / ...
public class BloodType : LookupBase { }           // A / B / AB / O (+ Rh ถ้าต้องการ)
public class EmergencyRelation : LookupBase { }   // พ่อ / แม่ / คู่สมรส / เพื่อน / อื่น ๆ
