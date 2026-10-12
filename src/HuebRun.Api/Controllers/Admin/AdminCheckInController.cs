using HuebRun.Api.Auth;
using HuebRun.Infrastructure.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HuebRun.Api.Controllers.Admin;

public sealed record ScanRequest(string? Token);
public sealed record ConfirmCheckInRequest(string? Station, string? Note);
public sealed record UndoCheckInRequest(string? Note);

[ApiController]
[Route("api/v1/admin")]
public sealed class AdminCheckInController(AdminCheckInService checkIn) : ControllerBase
{
    [HttpPost("checkin/scan")]
    [Authorize(Policy = AdminPolicies.CheckIn)]
    public Task<CheckInRunner> Scan(ScanRequest req, CancellationToken ct) => checkIn.ScanAsync(req.Token, ct);

    [HttpPost("checkin/{registrationId:int}/confirm")]
    [Authorize(Policy = AdminPolicies.CheckIn)]
    public Task<CheckInConfirmation> Confirm(int registrationId, ConfirmCheckInRequest? req, CancellationToken ct) =>
        checkIn.ConfirmAsync(registrationId, req?.Station, req?.Note, ct);

    // ยกเลิกเช็กอิน: Organizer/SuperAdmin เท่านั้น (Staff ทำไม่ได้)
    [HttpPost("checkin/{registrationId:int}/undo")]
    [Authorize(Policy = AdminPolicies.EventSettings)]
    public Task<CheckInRunner> Undo(int registrationId, UndoCheckInRequest req, CancellationToken ct) =>
        checkIn.UndoAsync(registrationId, req.Note, ct);

    [HttpGet("events/{eventId:int}/checkins")]
    [Authorize(Policy = AdminPolicies.CheckIn)]
    public Task<CheckInList> List(int eventId, bool? checkedIn, int? distance, string? q, int? page, int? pageSize, CancellationToken ct) =>
        checkIn.ListAsync(eventId, checkedIn, distance, q, page, pageSize, ct);
}
