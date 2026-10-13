using HuebRun.Domain;

namespace HuebRun.Api.Contracts;

public sealed record OrderResponse(
    Guid OrderId, string OrderNo, OrderStatus Status,
    decimal SubTotal, decimal DiscountAmount, decimal ShippingFee, decimal Total, decimal PayableAmount,
    DateTime CreatedAt, DateTime ExpiresAt, DateTime? PaidAt, string? PromoCode,
    OrderContactResponse Contact, OrderShippingResponse Shipping, IReadOnlyList<OrderRunnerResponse> Runners,
    OrderPaymentResponse? Payment, OrderLineResponse Line);

/// สถานะ LINE ของออเดอร์ (ไม่ส่ง LINE userId ออกไป): ได้รับแจ้งเตือนเมื่อ Linked และ Following
public sealed record OrderLineResponse(bool Linked, bool Following);

public sealed record OrderPaymentResponse(int PaymentId, PaymentStatus Status, string? RejectReason);

public sealed record OrderContactResponse(string Name, string Phone, string? Email);

public sealed record OrderShippingResponse(ShippingType Type, string Name, OrderAddressResponse? Address, string? TrackingNo);

public sealed record OrderAddressResponse(
    string? Name, string? Phone, string? Line1, string? Subdistrict, string? District, string? Province, string? PostalCode);

public sealed record OrderRunnerResponse(
    int RegistrationId, int? TitleId, string FirstName, string LastName,
    IdName Distance, ShirtSizeRef ShirtSize, decimal PricePaid, string? BibNumber);

public sealed record IdName(int Id, string Name);

public sealed record ShirtSizeRef(int Id, string Size);

public sealed record TicketResponse(
    int RegistrationId, string FirstName, string LastName, string DistanceName, string BibNumber, string CheckInToken);
