using HuebRun.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Geo;

/// โหลดตำบลทั้งประเทศ (~7,400 แถว) เข้าหน่วยความจำครั้งเดียว ข้อมูลไม่เปลี่ยนระหว่างแอปรัน
public sealed class GeoSearchIndex(IServiceScopeFactory scopes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<GeoEntry>? _entries;

    public async Task<IReadOnlyList<GeoEntry>> GetEntriesAsync(CancellationToken ct)
    {
        if (_entries is { } cached) return cached;
        await _gate.WaitAsync(ct);
        try
        {
            if (_entries is { } again) return again;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = await db.Subdistricts.AsNoTracking()
                .Select(s => new GeoEntry(s.Id, s.NameTh, s.NameEn,
                    s.District.Id, s.District.NameTh, s.District.NameEn,
                    s.District.Province.Id, s.District.Province.NameTh, s.District.Province.NameEn,
                    s.ZipCode))
                .ToListAsync(ct);
            // ยังไม่ได้ seed → ไม่ cache ค่าว่าง ไว้โหลดใหม่ครั้งหน้า
            if (loaded.Count > 0) _entries = loaded;
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }
}
