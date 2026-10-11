using HuebRun.Domain;

namespace HuebRun.Infrastructure.Orders;

// ทุกฟิลด์ nullable เพื่อให้ validate เองและตอบ error รูปแบบเดียวกันทุกฟิลด์
public sealed record CreateOrderRequest(
    ContactInput? Contact,
    List<RunnerInput>? Runners,
    int? ShippingOptionId,
    AddressInput? ShippingAddress,
    string? PromoCode,
    bool AcceptedPdpa);

public sealed record ContactInput(string? Name, string? Phone, string? Email);

public sealed record RunnerInput(
    int? DistanceId, int? ShirtSizeId,
    int? TitleId, string? FirstName, string? LastName, Gender? Gender,
    DateOnly? BirthDate, int? NationalityId, int? BloodTypeId,
    string? Phone, string? Email, string? TeamName,
    EmergencyContactInput? EmergencyContact, string? MedicalNote);

public sealed record EmergencyContactInput(int? RelationId, string? Name, string? Phone);

public sealed record AddressInput(string? Name, string? Phone, string? Line1, int? SubdistrictId, string? PostalCode);

public sealed record ValidatePromoRequest(string? Code, List<int>? DistanceIds);

public sealed record PromoQuote(string Code, decimal SubTotal, decimal DiscountAmount);

public sealed record CreatedOrder(Guid OrderId, string OrderNo, decimal Total, decimal PayableAmount, DateTime ExpiresAt);
