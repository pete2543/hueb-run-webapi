namespace HuebRun.Infrastructure.Admin;

/// รูปแบบรายการแบ่งหน้าเดียวทุก endpoint รายการของ admin
public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}
