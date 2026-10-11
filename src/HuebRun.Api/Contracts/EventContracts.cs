using HuebRun.Domain;

namespace HuebRun.Api.Contracts;

public sealed record EventResponse(
    int Id, string Slug, string Name, int Year, DateTime EventDate,
    DateTime RegistrationOpensAt, DateTime RegistrationClosesAt, bool RegistrationOpen);

public sealed record DistanceResponse(
    int Id, string Name, decimal DistanceKm, decimal Price, decimal RegularPrice,
    bool IsEarlyBird, DateTime? EarlyBirdEndsAt, int Quota, int Remaining);

public sealed record ShirtSizeResponse(int Id, string Size, string? Detail, int Remaining);

public sealed record ShippingOptionResponse(int Id, ShippingType Type, string Name, decimal Fee);
