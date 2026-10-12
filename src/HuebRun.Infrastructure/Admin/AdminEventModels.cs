using HuebRun.Domain;

namespace HuebRun.Infrastructure.Admin;

public sealed record AdminEventInfo(
    int Id, string Name, string Slug, int Year, DateTime EventDate, DateTime RegistrationOpensAt, DateTime RegistrationClosesAt,
    string PromptPayId, string PromptPayAccountName, int ReservationMinutes, bool IsPublished);

/// warnings = สิ่งที่แอดมินต้องทำต่อนอกระบบ (เช่น แก้บัญชีใน SlipOK) ไม่ใช่ error
public sealed record EventUpdateResult(AdminEventInfo Event, IReadOnlyList<string> Warnings);

public sealed record CreateEventRequest(
    string Name, string Slug, int Year, DateTime EventDate, DateTime RegistrationOpensAt, DateTime RegistrationClosesAt,
    string PromptPayId, string PromptPayAccountName, int? ReservationMinutes);

public sealed record UpdateEventRequest(
    string Name, DateTime EventDate, DateTime RegistrationOpensAt, DateTime RegistrationClosesAt,
    string PromptPayId, string PromptPayAccountName, int ReservationMinutes, bool IsPublished);

public sealed record DistanceInput(
    int? Id, string Name, decimal DistanceKm, string BibPrefix, int Quota, decimal Price,
    decimal? EarlyBirdPrice, DateTime? EarlyBirdEndsAt);

public sealed record DistanceInfo(
    int Id, string Name, decimal DistanceKm, string BibPrefix, int Quota, int ReservedCount, int LastBibNumber,
    decimal Price, decimal? EarlyBirdPrice, DateTime? EarlyBirdEndsAt);

public sealed record ShirtSizeInput(int? Id, string Size, string? Detail, int Stock);
public sealed record ShirtSizeInfo(int Id, string Size, string? Detail, int Stock, int ReservedCount);

public sealed record ShippingOptionInput(int? Id, ShippingType Type, string Name, decimal Fee, bool IsActive);
public sealed record ShippingOptionInfo(int Id, ShippingType Type, string Name, decimal Fee, bool IsActive);
