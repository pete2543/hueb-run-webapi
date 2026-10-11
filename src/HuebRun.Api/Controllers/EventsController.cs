using HuebRun.Api.Contracts;
using HuebRun.Domain;
using HuebRun.Infrastructure;
using HuebRun.Infrastructure.Orders;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HuebRun.Api.Controllers;

[ApiController]
[Route("api/v1/events/{slug}")]
public sealed class EventsController(AppDbContext db, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<EventResponse> Get(string slug, CancellationToken ct)
    {
        var e = await Published(slug).AsNoTracking().FirstOrDefaultAsync(ct) ?? throw OrderErrors.EventNotFound(slug);
        return new EventResponse(e.Id, e.Slug, e.Name, e.Year, e.EventDate,
            e.RegistrationOpensAt, e.RegistrationClosesAt, e.IsRegistrationOpen(Now));
    }

    [HttpGet("distances")]
    public async Task<IReadOnlyList<DistanceResponse>> GetDistances(string slug, CancellationToken ct)
    {
        var eventId = await GetEventIdAsync(slug, ct);
        var rows = await db.EventDistances.AsNoTracking()
            .Where(d => d.EventId == eventId)
            .OrderBy(d => d.DistanceKm).ThenBy(d => d.Id)
            .ToListAsync(ct);
        var now = Now;
        return rows.Select(d => new DistanceResponse(d.Id, d.Name, d.DistanceKm, d.GetEffectivePrice(now), d.Price,
            d.IsEarlyBird(now), d.EarlyBirdEndsAt, d.Quota, d.GetRemaining())).ToList();
    }

    [HttpGet("shirt-sizes")]
    public async Task<IReadOnlyList<ShirtSizeResponse>> GetShirtSizes(string slug, CancellationToken ct)
    {
        var eventId = await GetEventIdAsync(slug, ct);
        var rows = await db.EventShirtSizes.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        return rows.Select(s => new ShirtSizeResponse(s.Id, s.Size, s.Detail, s.GetRemaining())).ToList();
    }

    [HttpGet("shipping-options")]
    public async Task<IReadOnlyList<ShippingOptionResponse>> GetShippingOptions(string slug, CancellationToken ct)
    {
        var eventId = await GetEventIdAsync(slug, ct);
        return await db.ShippingOptions.AsNoTracking()
            .Where(s => s.EventId == eventId && s.IsActive)
            .OrderBy(s => s.Id)
            .Select(s => new ShippingOptionResponse(s.Id, s.Type, s.Name, s.Fee))
            .ToListAsync(ct);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    // slug เก็บเป็นตัวพิมพ์เล็กเสมอ
    private IQueryable<Event> Published(string slug)
    {
        var normalized = slug.ToLowerInvariant();
        return db.Events.Where(e => e.Slug == normalized && e.IsPublished);
    }

    private async Task<int> GetEventIdAsync(string slug, CancellationToken ct) =>
        await Published(slug).Select(e => (int?)e.Id).FirstOrDefaultAsync(ct) ?? throw OrderErrors.EventNotFound(slug);
}
