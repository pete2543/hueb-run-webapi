using HuebRun.Domain;

namespace HuebRun.Infrastructure.Payments;

public sealed record PaymentSummary(int PaymentId, PaymentStatus Status, string? RejectReason, DateTime CreatedAt, string? Note = null);

public sealed record PaymentInfo(
    Guid OrderId, string OrderNo, decimal Amount, string AccountName, string QrPayload, DateTime ExpiresAt, PaymentSummary? Payment);

public sealed record SlipSubmission(int PaymentId, PaymentStatus Status, OrderStatus OrderStatus, DateTime? NextCheckAt, string? Reason);
