namespace FellowCore.Application.Common.Models;

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize
)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNext => Page < TotalPages;
    public bool HasPrevious => Page > 1;

    public static (int Skip, int Take, int NormalizedPage) Normalize(int page, int pageSize)
    {
        int normalizedPage = Math.Max(page, 1);
        int take = Math.Clamp(pageSize, 1, 100);
        int skip = (normalizedPage - 1) * take;
        return (skip, take, normalizedPage);
    }
}
