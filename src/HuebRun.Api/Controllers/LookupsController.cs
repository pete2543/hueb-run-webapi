using HuebRun.Api.Contracts;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/lookups")]
public sealed class LookupsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<LookupsResponse> Get(CancellationToken ct) => new(
        await ListAsync(db.Titles, ct),
        await ListAsync(db.Nationalities, ct),
        await ListAsync(db.BloodTypes, ct),
        await ListAsync(db.EmergencyRelations, ct));

    private static async Task<IReadOnlyList<LookupItem>> ListAsync<T>(IQueryable<T> source, CancellationToken ct)
        where T : LookupBase =>
        await source.AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => new LookupItem(x.Id, x.NameTh, x.NameEn))
            .ToListAsync(ct);
}
