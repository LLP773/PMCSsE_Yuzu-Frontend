using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace Yuzu_Frontend.Desktop.Models;

public class DisposableManager : IDisposable
{
    private readonly List<Action> _cleanupActions = new();
    private bool _isDisposed;

    public void Register(Action cleanup)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(DisposableManager));
        }

        _cleanupActions.Add(cleanup);
    }

    public void RegisterEvent<T>(T control, Action<T> subscribe, Action<T> unsubscribe)
        where T : class
    {
        subscribe(control);
        Register(() => unsubscribe(control));
    }

    public void RegisterDataPackSubscription(Action subscribe, Action unsubscribe)
    {
        subscribe();
        Register(unsubscribe);
    }

    public void RegisterDataGridSelectionChanged(DataGrid dataGrid, EventHandler<SelectionChangedEventArgs> handler)
    {
        dataGrid.SelectionChanged += handler;
        Register(() => dataGrid.SelectionChanged -= handler);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        for (var i = _cleanupActions.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanupActions[i]();
            }
            catch
            {
            }
        }

        _cleanupActions.Clear();
        _isDisposed = true;
    }
}