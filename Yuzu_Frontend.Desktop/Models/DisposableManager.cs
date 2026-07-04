using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// 资源释放管理器。集中登记页面/控件生命周期内的清理动作（取消事件订阅、
/// 断开数据包通道、释放订阅等），并在 <see cref="Dispose"/> 时按"后进先出"（LIFO）
/// 的顺序统一执行，避免散落在各处的手动释放逻辑遗漏或重复。
/// </summary>
public class DisposableManager : IDisposable
{
    // 已登记的清理动作列表，按注册顺序存储；释放时逆序执行
    private readonly List<Action> _cleanupActions = new();
    // 标记当前实例是否已释放，防止重复释放或释放后再注册
    private bool _isDisposed;

    /// <summary>
    /// 登记一个清理动作，该方法将在 <see cref="Dispose"/> 时被逆序调用。
    /// </summary>
    /// <param name="cleanup">释放时执行的无参委托。</param>
    /// <exception cref="ObjectDisposedException">当实例已释放时再次注册。</exception>
    public void Register(Action cleanup)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(DisposableManager));
        }

        _cleanupActions.Add(cleanup);
    }

    /// <summary>
    /// 登记一个实现了 <see cref="IDisposable"/> 的订阅/资源对象，在释放时调用其 Dispose。
    /// </summary>
    /// <param name="disposable">要登记的可释放资源。</param>
    /// <exception cref="ObjectDisposedException">当实例已释放时再次注册。</exception>
    public void Register(IDisposable disposable)
    {
        if (disposable == null) return;
        Register(disposable.Dispose);
    }

    /// <summary>
    /// 清空当前所有已登记的清理动作并立即逆序执行（先释放后注册的），
    /// 之后该实例可重新用于登记新动作。
    /// 用于 Attach 时解绑旧订阅（DisposableManager 本身未被 Dispose，仅重置内容）。
    /// </summary>
    public void Clear()
    {
        for (var i = _cleanupActions.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanupActions[i]();
            }
            catch
            {
                // 单个清理失败不阻断后续清理
            }
        }
        _cleanupActions.Clear();
    }

    /// <summary>
    /// 登记一个事件订阅及其对应的取消订阅动作：立即执行订阅，并将取消订阅登记为清理动作。
    /// </summary>
    /// <typeparam name="T">事件源类型。</typeparam>
    /// <param name="control">事件源对象。</param>
    /// <param name="subscribe">订阅事件的方法。</param>
    /// <param name="unsubscribe">取消订阅事件的方法，将在释放时执行。</param>
    public void RegisterEvent<T>(T control, Action<T> subscribe, Action<T> unsubscribe)
        where T : class
    {
        subscribe(control);
        Register(() => unsubscribe(control));
    }

    /// <summary>
    /// 登记数据包订阅：立即执行订阅，并将取消订阅登记为清理动作。
    /// </summary>
    /// <param name="subscribe">订阅数据包通道的方法。</param>
    /// <param name="unsubscribe">取消订阅数据包通道的方法，将在释放时执行。</param>
    public void RegisterDataPackSubscription(Action subscribe, Action unsubscribe)
    {
        subscribe();
        Register(unsubscribe);
    }

    /// <summary>
    /// 登记 DataGrid 的 SelectionChanged 事件订阅，并在释放时自动取消订阅。
    /// </summary>
    /// <param name="dataGrid">订阅目标 DataGrid。</param>
    /// <param name="handler">选择变更事件处理器。</param>
    public void RegisterDataGridSelectionChanged(DataGrid dataGrid, EventHandler<SelectionChangedEventArgs> handler)
    {
        dataGrid.SelectionChanged += handler;
        Register(() => dataGrid.SelectionChanged -= handler);
    }

    /// <summary>
    /// 执行所有已登记的清理动作。采用逆序遍历（LIFO）模拟正常的"析构"顺序，
    /// 单个清理动作抛出的异常会被捕获并忽略，以保证其余清理动作仍能继续执行。
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        // 逆序执行：后注册的依赖通常依赖先注册的资源，应先释放
        for (var i = _cleanupActions.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanupActions[i]();
            }
            catch
            {
                // 单个清理失败不阻断后续清理
            }
        }

        _cleanupActions.Clear();
        _isDisposed = true;
    }
}