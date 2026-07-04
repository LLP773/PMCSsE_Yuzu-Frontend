using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Yuzu_Frontend.ViewModels;

/// <summary>
/// 支持批量操作的 ObservableCollection 派生类。
/// 解决原生 ObservableCollection 每次 Add 都触发一次 CollectionChanged，
/// 导致 ItemsControl 进行 N 次布局/渲染的性能问题。
/// 公开属性类型仍可声明为 ObservableCollection&lt;T&gt;（里氏替换），XAML 绑定零改动。
/// </summary>
/// <typeparam name="T">集合元素类型。</typeparam>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// 批量追加元素：完成后仅触发 <b>一次</b> Reset 通知，ItemsControl 统一重建视图一次。
    /// </summary>
    /// <param name="items">要追加的元素序列。null 或空时静默忽略。</param>
    public void AddRange(IEnumerable<T>? items)
    {
        if (items == null) return;

        var buffered = new List<T>(items);
        if (buffered.Count == 0) return;

        foreach (var item in buffered)
        {
            Items.Add(item);
        }

        RaiseBatchNotifications();
    }

    /// <summary>
    /// 整体替换集合内容：清空 + 批量追加，完成后 <b>仅触发一次</b> Reset。
    /// 等价于原逻辑 Clear() + foreach Add，但通知次数从 N+1 降到 1。
    /// </summary>
    /// <param name="items">新的全部内容。null 时等价于清空。</param>
    public void ReplaceAll(IEnumerable<T>? items)
    {
        Items.Clear();

        if (items != null)
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        RaiseBatchNotifications();
    }

    /// <summary>
    /// 触发 Count / Item[] 属性变更 + 一次 Reset 级集合变更通知。
    /// </summary>
    private void RaiseBatchNotifications()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
