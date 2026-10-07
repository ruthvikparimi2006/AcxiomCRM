using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.ViewModels;

// Non-generic view of a page, so the shared _Pagination partial works for every list (GEN-07).
public interface IPagedList
{
    int Page { get; }
    int PageSize { get; }
    int TotalCount { get; }
    int TotalPages { get; }
}

public class PagedList<T> : IPagedList
{
    public const int DefaultPageSize = 20;

    public List<T> Items { get; init; } = [];
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = DefaultPageSize;
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    // Out-of-range page numbers are clamped rather than returning an empty page.
    public static async Task<PagedList<T>> CreateAsync(IQueryable<T> query, int page, int pageSize = DefaultPageSize)
    {
        var total = await query.CountAsync();
        var lastPage = Math.Max(1, (total + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, lastPage);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedList<T> { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
    }

    public static PagedList<T> FromList(List<T> all, int page, int pageSize = DefaultPageSize)
    {
        var lastPage = Math.Max(1, (all.Count + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, lastPage);
        return new PagedList<T> { Items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), Page = page, PageSize = pageSize, TotalCount = all.Count };
    }
}

// A sortable column header for the shared _SortHeader partial. Reads/writes ?sort=&desc= on the current URL.
public record SortHeader(string Label, string Field);
