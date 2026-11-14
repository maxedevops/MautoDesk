namespace MautoDesk.SharedKernel;

/// <summary>One page of results.</summary>
/// <remarks>
/// <para>
/// Lives in the SharedKernel because paging is not any one module's idea. It was
/// originally declared in <c>Inventory.Contracts</c>, which worked only for as
/// long as Inventory was the only module with a list — the alternative to moving
/// it was Sales taking a reference on Inventory's public surface to borrow a
/// paging record, which would couple two modules for no reason and make
/// extracting either one harder later.
/// </para>
/// <para>
/// <see cref="TotalPages"/> uses <see cref="double"/> deliberately and safely: it
/// is a page count for a pager control, never money, and it is the one place in
/// this assembly where binary floating point is acceptable. The architecture test
/// that bans <see cref="double"/> covers the domain and contract assemblies where
/// amounts live, not this.
/// </para>
/// </remarks>
/// <typeparam name="T">What is being paged.</typeparam>
/// <param name="Items">This page's rows.</param>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="TotalCount">Rows in the whole result.</param>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    /// <summary>How many pages the result spans.</summary>
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
