namespace HuebRun.Infrastructure.Admin;

public sealed record AdminUserInfo(
    Guid Id, string Name, string Email, string Role, bool IsActive, IReadOnlyList<int> EventIds,
    DateTime CreatedAt, DateTime? LastLoginAt);

public sealed record LoginResult(string Token, DateTime ExpiresAt, AdminUserInfo User);

public sealed record CreateAdminUserRequest(string Name, string Email, string Password, string Role, IReadOnlyList<int>? EventIds);

/// ฟิลด์ที่เป็น null = ไม่แก้
public sealed record UpdateAdminUserRequest(
    string? Name, string? Role, bool? IsActive, IReadOnlyList<int>? EventIds, string? NewPassword);
