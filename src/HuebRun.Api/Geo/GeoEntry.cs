namespace HuebRun.Api.Geo;

/// ตำบล 1 แถวพร้อมอำเภอ/จังหวัด เตรียมข้อความสำหรับค้นหาไว้ล่วงหน้า
public sealed class GeoEntry
{
    public GeoEntry(int subdistrictId, string subdistrictTh, string? subdistrictEn,
        int districtId, string districtTh, string? districtEn,
        int provinceId, string provinceTh, string? provinceEn, string zipCode)
    {
        SubdistrictId = subdistrictId; SubdistrictTh = subdistrictTh;
        DistrictId = districtId; DistrictTh = districtTh;
        ProvinceId = provinceId; ProvinceTh = provinceTh;
        ZipCode = zipCode;
        Label = $"{subdistrictTh} » {districtTh} » {provinceTh} {zipCode}";

        string[] all = [subdistrictTh, subdistrictEn ?? "", districtTh, districtEn ?? "", provinceTh, provinceEn ?? ""];
        Haystack = string.Join('|', all).ToLowerInvariant();
        SubdistrictNames = Names(subdistrictTh, subdistrictEn);
        DistrictNames = Names(districtTh, districtEn);
        ProvinceNames = Names(provinceTh, provinceEn);
    }

    public int SubdistrictId { get; }
    public string SubdistrictTh { get; }
    public int DistrictId { get; }
    public string DistrictTh { get; }
    public int ProvinceId { get; }
    public string ProvinceTh { get; }
    public string ZipCode { get; }
    public string Label { get; }

    internal string Haystack { get; }
    internal string[] SubdistrictNames { get; }
    internal string[] DistrictNames { get; }
    internal string[] ProvinceNames { get; }

    // ชื่อตัดคำนำหน้า (เขต/แขวง/Khet ...) ไว้ใช้จัดอันดับแบบ "ขึ้นต้นด้วย"
    private static string[] Names(string th, string? en) =>
        en is null ? [GeoSearch.StripPrefix(th.ToLowerInvariant())]
                   : [GeoSearch.StripPrefix(th.ToLowerInvariant()), GeoSearch.StripPrefix(en.ToLowerInvariant())];
}
