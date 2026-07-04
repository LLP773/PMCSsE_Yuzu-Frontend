using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;

namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// 通用分页视图模型。维护一个全量数据集合，并提供搜索、过滤、排序与分页能力。
/// 每当搜索文本、过滤谓词、排序函数、页大小或当前页发生变化时，
/// 都会重新执行"搜索 → 过滤 → 排序 → 取当前页"的管线并刷新派生属性。
/// </summary>
/// <typeparam name="T">数据项类型。</typeparam>
public class PaginationViewModel<T> : ReactiveObject
{
    // 全量数据集合（未经搜索/过滤/分页）
    private readonly RangeObservableCollection<T> _allItems = [];
    // 当前页展示的数据集合（经搜索/过滤/排序/分页后的结果）
    private readonly RangeObservableCollection<T> _pagedItems = [];

    private int _pageSize = 20;
    private int _currentPage = 1;
    private string? _searchText;
    private Func<T, bool>? _filterPredicate;
    private Func<IEnumerable<T>, IEnumerable<T>>? _sortFunc;

    /// <summary>全量数据集合（只读视图）。</summary>
    public ObservableCollection<T> AllItems => _allItems;
    /// <summary>当前页数据集合（只读视图）。</summary>
    public ObservableCollection<T> PagedItems => _pagedItems;

    /// <summary>每页条数，变更后自动重新分页。</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            this.RaiseAndSetIfChanged(ref _pageSize, value);
            ApplyPagination();
        }
    }

    /// <summary>当前页码（从 1 开始），设置时自动夹取到 [1, TotalPages] 区间。</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            // 防止越界：当前页夹取到 [1, TotalPages]
            var newValue = Math.Max(1, Math.Min(value, TotalPages));
            this.RaiseAndSetIfChanged(ref _currentPage, newValue);
            ApplyPagination();
        }
    }

    /// <summary>全量数据总条数。</summary>
    public int TotalItems => _allItems.Count;
    /// <summary>总页数 = 向上取整(总条数 / 每页条数)。</summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    /// <summary>当前页起始条序号（从 1 开始）。</summary>
    public int DisplayStartItem => (CurrentPage - 1) * PageSize + 1;
    /// <summary>当前页结束条序号（不超过总条数）。</summary>
    public int DisplayEndItem => Math.Min(CurrentPage * PageSize, TotalItems);

    /// <summary>是否存在上一页。</summary>
    public bool HasPreviousPage => CurrentPage > 1;
    /// <summary>是否存在下一页。</summary>
    public bool HasNextPage => CurrentPage < TotalPages;
    /// <summary>是否为第一页。</summary>
    public bool IsFirstPage => CurrentPage == 1;
    /// <summary>是否为最后一页。</summary>
    public bool IsLastPage => CurrentPage == TotalPages;
    /// <summary>分页信息文本（用于 UI 显示）。</summary>
    public string PageInfoText => TotalItems > 0 ? $"显示 {DisplayStartItem}-{DisplayEndItem} / 共 {TotalItems} 条" : "暂无数据";

    /// <summary>搜索文本。变更后回到第一页并重新分页。</summary>
    public string? SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            // 搜索条件变化时回到第一页，避免停留在不存在的页码
            CurrentPage = 1;
            ApplyPagination();
        }
    }

    /// <summary>自定义过滤谓词。变更后回到第一页并重新分页。</summary>
    public Func<T, bool>? FilterPredicate
    {
        get => _filterPredicate;
        set
        {
            _filterPredicate = value;
            CurrentPage = 1;
            ApplyPagination();
        }
    }

    /// <summary>自定义排序函数。变更后重新分页（页码不变）。</summary>
    public Func<IEnumerable<T>, IEnumerable<T>>? SortFunc
    {
        get => _sortFunc;
        set
        {
            _sortFunc = value;
            ApplyPagination();
        }
    }

    /// <summary>可选的每页条数选项。</summary>
    public List<int> PageSizeOptions => new() { 10, 20, 50, 100 };

    /// <summary>添加单个数据项并重新分页。</summary>
    /// <param name="item">要添加的数据项。</param>
    public void AddItem(T item)
    {
        _allItems.Add(item);
        ApplyPagination();
    }

    /// <summary>批量添加数据项并重新分页。</summary>
    /// <param name="items">要添加的数据项集合。</param>
    public void AddItems(IEnumerable<T> items)
    {
        // AddRange: 内部批量追加，只触发 1 次 Reset 通知
        _allItems.AddRange(items);
        ApplyPagination();
    }

    /// <summary>
    /// 根据匹配谓词更新全量集合中的某一项。
    /// </summary>
    /// <param name="item">新的数据项。</param>
    /// <param name="matchPredicate">用于定位待替换项的谓词。</param>
    public void UpdateItem(T item, Func<T, bool> matchPredicate)
    {
        var index = _allItems.ToList().FindIndex(x => matchPredicate(x));
        if (index >= 0)
        {
            _allItems[index] = item;
            ApplyPagination();
        }
    }

    /// <summary>
    /// 根据匹配谓词移除全量集合中的某一项。
    /// 若移除后当前页超出总页数，则自动回退到最后一页。
    /// </summary>
    /// <param name="matchPredicate">用于定位待移除项的谓词。</param>
    public void RemoveItem(Func<T, bool> matchPredicate)
    {
        var item = _allItems.FirstOrDefault(matchPredicate);
        if (item != null)
        {
            _allItems.Remove(item);
            // 删除后若当前页越界，回退到新的最后一页
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }
            ApplyPagination();
        }
    }

    /// <summary>清空全部数据并重置为第一页。</summary>
    public void ClearItems()
    {
        _allItems.Clear();
        _pagedItems.Clear();
        CurrentPage = 1;
        RefreshProperties();
    }

    /// <summary>跳转到第一页。</summary>
    public void NavigateToFirst()
    {
        CurrentPage = 1;
    }

    /// <summary>跳转到上一页（如存在）。</summary>
    public void NavigateToPrevious()
    {
        if (HasPreviousPage)
        {
            CurrentPage--;
        }
    }

    /// <summary>跳转到下一页（如存在）。</summary>
    public void NavigateToNext()
    {
        if (HasNextPage)
        {
            CurrentPage++;
        }
    }

    /// <summary>跳转到最后一页。</summary>
    public void NavigateToLast()
    {
        CurrentPage = TotalPages;
    }

    /// <summary>跳转到指定页码（会经过越界夹取）。</summary>
    /// <param name="page">目标页码。</param>
    public void NavigateToPage(int page)
    {
        CurrentPage = page;
    }

    /// <summary>
    /// 重新执行分页管线：搜索 → 过滤 → 排序 → 取当前页，并刷新派生属性。
    /// 这是整个分页模型的核心方法，所有变更最终都会触发它。
    /// </summary>
    private void ApplyPagination()
    {
        var filtered = _allItems.AsEnumerable();

        // 第一步：按搜索文本过滤（对 ToString() 结果做小写包含匹配）
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchLower = SearchText.ToLower();
            filtered = filtered.Where(item => 
                item?.ToString()?.ToLower().Contains(searchLower) == true);
        }

        // 第二步：应用自定义过滤谓词
        if (FilterPredicate != null)
        {
            filtered = filtered.Where(FilterPredicate);
        }

        // 第三步：应用自定义排序
        if (SortFunc != null)
        {
            filtered = SortFunc(filtered);
        }

        // 第四步：按当前页码与页大小截取当前页数据
        var paged = filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize);

        // 一次性批量替换：Clear + N Add → 1 次 Reset 通知
        _pagedItems.ReplaceAll(paged);

        RefreshProperties();
    }

    /// <summary>
    /// 手动触发派生属性（总数、页数、起止序号、翻页标志等）的变更通知，
    /// 以便 UI 绑定能够及时刷新。
    /// </summary>
    private void RefreshProperties()
    {
        this.RaisePropertyChanged(nameof(TotalItems));
        this.RaisePropertyChanged(nameof(TotalPages));
        this.RaisePropertyChanged(nameof(DisplayStartItem));
        this.RaisePropertyChanged(nameof(DisplayEndItem));
        this.RaisePropertyChanged(nameof(HasPreviousPage));
        this.RaisePropertyChanged(nameof(HasNextPage));
        this.RaisePropertyChanged(nameof(IsFirstPage));
        this.RaisePropertyChanged(nameof(IsLastPage));
    }
}