using System.Text.RegularExpressions;
using HuebRun.Api.Contracts;
using HuebRun.Api.Errors;
using HuebRun.Api.Geo;
using HuebRun.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/geo")]
public sealed partial class GeoController(AppDbContext db, GeoSearchIndex searchIndex) : ControllerBase
{
    public const int DefaultSearchLimit = 20;
    public const int MaxSearchLimit = 50;

    [HttpGet("provinces")]
    public async Task<IReadOnlyList<GeoItem>> GetProvinces(CancellationToken ct) =>
        await db.Provinces.AsNoTracking()
            .OrderBy(p => p.NameTh)
            .Select(p => new GeoItem(p.Id, p.NameTh, p.NameEn))
            .ToListAsync(ct);

    [HttpGet("provinces/{id}/districts")]
    public async Task<IReadOnlyList<GeoItem>> GetDistricts(int id, CancellationToken ct)
    {
        if (!await db.Provinces.AnyAsync(p => p.Id == id, ct))
            throw new AppException(StatusCodes.Status404NotFound, "PROVINCE_NOT_FOUND", "ไม่พบจังหวัด", new { id });
        return await db.Districts.AsNoTracking()
            .Where(d => d.ProvinceId == id)
            .OrderBy(d => d.NameTh)
            .Select(d => new GeoItem(d.Id, d.NameTh, d.NameEn))
            .ToListAsync(ct);
    }

    [HttpGet("districts/{id}/subdistricts")]
    public async Task<IReadOnlyList<SubdistrictItem>> GetSubdistricts(int id, CancellationToken ct)
    {
        if (!await db.Districts.AnyAsync(d => d.Id == id, ct))
            throw new AppException(StatusCodes.Status404NotFound, "DISTRICT_NOT_FOUND", "ไม่พบอำเภอ/เขต", new { id });
        return await db.Subdistricts.AsNoTracking()
            .Where(s => s.DistrictId == id)
            .OrderBy(s => s.NameTh)
            .Select(s => new SubdistrictItem(s.Id, s.NameTh, s.NameEn, s.ZipCode))
            .ToListAsync(ct);
    }

    [HttpGet("zip/{zipCode}")]
    public async Task<IReadOnlyList<ZipMatch>> GetByZip(string zipCode, CancellationToken ct)
    {
        if (!ZipPattern().IsMatch(zipCode))
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "รหัสไปรษณีย์ต้องเป็นตัวเลข 5 หลัก", new { zipCode });
        return await db.Subdistricts.AsNoTracking()
            .Where(s => s.ZipCode == zipCode)
            .OrderBy(s => s.District.Province.NameTh).ThenBy(s => s.District.NameTh).ThenBy(s => s.NameTh)
            .Select(s => new ZipMatch(
                new GeoRef(s.Id, s.NameTh),
                new GeoRef(s.District.Id, s.District.NameTh),
                new GeoRef(s.District.Province.Id, s.District.Province.NameTh),
                s.ZipCode))
            .ToListAsync(ct);
    }

    // ช่องค้นหาเดียว: ชื่อตำบล/อำเภอ/จังหวัด หรือรหัสไปรษณีย์ (บางส่วน) คืนที่อยู่ครบชุดให้เติมฟอร์มได้ทันที
    [HttpGet("search")]
    public async Task<IReadOnlyList<GeoSearchResult>> Search(string? q, CancellationToken ct, int limit = DefaultSearchLimit)
    {
        if (limit is < 1 or > MaxSearchLimit)
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"limit ต้องอยู่ระหว่าง 1 ถึง {MaxSearchLimit}", new { limit });
        var entries = await searchIndex.GetEntriesAsync(ct);
        return GeoSearch.Search(entries, q, limit)
            .Select(e => new GeoSearchResult(
                new GeoRef(e.SubdistrictId, e.SubdistrictTh),
                new GeoRef(e.DistrictId, e.DistrictTh),
                new GeoRef(e.ProvinceId, e.ProvinceTh),
                e.ZipCode, e.Label))
            .ToList();
    }

    // ใช้ [0-9] ไม่ใช้ \d เพราะ \d ยอมรับเลขไทย
    [GeneratedRegex("^[0-9]{5}$")]
    private static partial Regex ZipPattern();
}
