using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ReactiveUI;

namespace Yuzu_Frontend.ViewModels;

public class PaginationViewModel<T> : ReactiveObject
{
    private readonly ObservableCollection<T> _allItems = new();
    private readonly ObservableCollection<T> _pagedItems = new();
    
    private int _pageSize = 20;
    private int _currentPage = 1;
    private string? _searchText;
    private Func<T, bool>? _filterPredicate;
    private Func<IEnumerable<T>, IEnumerable<T>>? _sortFunc;

    public ObservableCollection<T> AllItems => _allItems;
    public ObservableCollection<T> PagedItems => _pagedItems;

    public int PageSize
    {
        get => _pageSize;
        set
        {
            this.RaiseAndSetIfChanged(ref _pageSize, value);
            ApplyPagination();
        }
    }

    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            var newValue = Math.Max(1, Math.Min(value, TotalPages));
            this.RaiseAndSetIfChanged(ref _currentPage, newValue);
            ApplyPagination();
        }
    }

    public int TotalItems => _allItems.Count;
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);
    public int DisplayStartItem => (CurrentPage - 1) * PageSize + 1;
    public int DisplayEndItem => Math.Min(CurrentPage * PageSize, TotalItems);

    public bool HasPreviousPage => CurrentPage > 1;
    public bool HasNextPage => CurrentPage < TotalPages;
    public bool IsFirstPage => CurrentPage == 1;
    public bool IsLastPage => CurrentPage == TotalPages;
    public string PageInfoText => TotalItems > 0 ? $"显示 {DisplayStartItem}-{DisplayEndItem} / 共 {TotalItems} 条" : "暂无数据";

    public string? SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            CurrentPage = 1;
            ApplyPagination();
        }
    }

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

    public Func<IEnumerable<T>, IEnumerable<T>>? SortFunc
    {
        get => _sortFunc;
        set
        {
            _sortFunc = value;
            ApplyPagination();
        }
    }

    public List<int> PageSizeOptions => new() { 10, 20, 50, 100 };

    public void AddItem(T item)
    {
        _allItems.Add(item);
        ApplyPagination();
    }

    public void AddItems(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            _allItems.Add(item);
        }
        ApplyPagination();
    }

    public void UpdateItem(T item, Func<T, bool> matchPredicate)
    {
        var index = _allItems.ToList().FindIndex(x => matchPredicate(x));
        if (index >= 0)
        {
            _allItems[index] = item;
            ApplyPagination();
        }
    }

    public void RemoveItem(Func<T, bool> matchPredicate)
    {
        var item = _allItems.FirstOrDefault(matchPredicate);
        if (item != null)
        {
            _allItems.Remove(item);
            if (CurrentPage > TotalPages)
            {
                CurrentPage = TotalPages;
            }
            ApplyPagination();
        }
    }

    public void ClearItems()
    {
        _allItems.Clear();
        _pagedItems.Clear();
        CurrentPage = 1;
        RefreshProperties();
    }

    public void NavigateToFirst()
    {
        CurrentPage = 1;
    }

    public void NavigateToPrevious()
    {
        if (HasPreviousPage)
        {
            CurrentPage--;
        }
    }

    public void NavigateToNext()
    {
        if (HasNextPage)
        {
            CurrentPage++;
        }
    }

    public void NavigateToLast()
    {
        CurrentPage = TotalPages;
    }

    public void NavigateToPage(int page)
    {
        CurrentPage = page;
    }

    private void ApplyPagination()
    {
        _pagedItems.Clear();

        var filtered = _allItems.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var searchLower = SearchText.ToLower();
            filtered = filtered.Where(item => 
                item?.ToString()?.ToLower().Contains(searchLower) == true);
        }

        if (FilterPredicate != null)
        {
            filtered = filtered.Where(FilterPredicate);
        }

        if (SortFunc != null)
        {
            filtered = SortFunc(filtered);
        }

        var paged = filtered.Skip((CurrentPage - 1) * PageSize).Take(PageSize);
        
        foreach (var item in paged)
        {
            _pagedItems.Add(item);
        }

        RefreshProperties();
    }

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