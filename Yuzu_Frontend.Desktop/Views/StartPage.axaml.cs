using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Material.Icons;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;
using Yuzu_Frontend.Modules;
using Yuzu_Frontend.ViewModels;

namespace Yuzu_Frontend.Desktop.Views;

[Page("连接", MaterialIconKind.LanConnect, Order = 0, IsCollection = false)]
public partial class StartPage : NavigatedPageBase
{
    public ObservableCollection<ConnectionHistoryItem> Items { get; } = new();

    public StartPage()
    {
        InitializeComponent();
        LoadHistory();
    }


    public override void InitializePage(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel? connectionViewModel = null)
    {
        var oldVm = Connection;
        if (oldVm != null)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        base.InitializePage(toastManager, dialogManager, connectionViewModel ?? new ConnectionViewModel());

        SyncEmptyBackendsHint();

        if (Connection != null)
            Connection.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.HasConnectedBackends))
            SyncEmptyBackendsHint();
    }

    private void SyncEmptyBackendsHint()
    {
        var header = this.FindControl<Border>("BackendsTableHeader");
        var scroll = this.FindControl<ScrollViewer>("BackendsTableScrollViewer");
        var empty = this.FindControl<Border>("EmptyBackendsHint");
        var btnDisconnect = this.FindControl<Button>("ButtonDisconnectSelected");

        bool hasData = Connection != null && Connection.HasConnectedBackends;

        if (header != null) header.IsVisible = hasData;
        if (scroll != null) scroll.IsVisible = hasData;
        if (empty != null) empty.IsVisible = !hasData;
        if (btnDisconnect != null) btnDisconnect.IsVisible = hasData;
    }

    private async void ButtonConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (Connection == null) return;

        if (this.FindControl<TextBox>("AddressBox") is { } addrBox)
            Connection.BackendAddress = string.IsNullOrWhiteSpace(addrBox.Text) ? "localhost" : addrBox.Text.Trim();
        if (this.FindControl<TextBox>("PortBox") is { } portBox)
            Connection.BackendPort = string.IsNullOrWhiteSpace(portBox.Text) ? "20000" : portBox.Text.Trim();
        if (this.FindControl<TextBox>("PasswordBox") is { } pwBox)
            Connection.BackendPassword = pwBox.Text ?? "";

        if (Connection.IsConnected || Connection.IsConnecting)
            return;

        bool ok = await Connection.ConnectAsync();
        if (ok)
        {
            string? passwordToSave = Connection.SavePassword ? Connection.BackendPassword : null;
            if (Connection.SaveToHistory)
            {
                AddToHistory(Connection.BackendAddress ?? "", Connection.BackendPort ?? "", passwordToSave ?? "");
            }
        }
    }

    private void ButtonDisconnectSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Connection == null) return;

        var selected = Connection.ConnectedBackends.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) return;

        Connection.DisconnectBackends(selected);
    }

    private void HistoryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ConnectionHistoryItem item)
        {
            SelectHistory(item);
        }
    }

    private void HistoryDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ConnectionHistoryItem item)
        {
            Items.Remove(item);
            SaveHistory();
        }
    }

    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        Items.Clear();
        SaveHistory();
    }

    private void SelectHistory(ConnectionHistoryItem item)
    {
        if (Connection != null)
        {
            Connection.BackendAddress = item.Address;
            Connection.BackendPort = item.Port;
            Connection.BackendPassword = item.Password ?? "";
        }

        if (this.FindControl<TextBox>("AddressBox") is { } addrBox) addrBox.Text = item.Address;
        if (this.FindControl<TextBox>("PortBox") is { } portBox) portBox.Text = item.Port;
        if (this.FindControl<TextBox>("PasswordBox") is { } pwBox) pwBox.Text = item.Password ?? "";

        item.UseCount++;
        item.Timestamp = DateTime.Now;
        SaveHistory();
    }

    private void AddToHistory(string address, string port, string password)
    {
        var existing = Items.FirstOrDefault(x =>
            string.Equals(x.Address, address, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Port, port, StringComparison.Ordinal));

        if (existing != null)
        {
            existing.Timestamp = DateTime.Now;
            existing.UseCount++;
            existing.Password = password ?? "";
            existing.BackendId = BackendIdManager.GetOrAssignId(address, port);
            Items.Remove(existing);
            Items.Insert(0, existing);
        }
        else
        {
            Items.Insert(0, new ConnectionHistoryItem
            {
                Address = address,
                Port = port,
                Password = password ?? "",
                BackendId = BackendIdManager.GetOrAssignId(address, port),
                Timestamp = DateTime.Now,
                UseCount = 1
            });
        }

        while (Items.Count > 20) Items.RemoveAt(Items.Count - 1);
        SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YuzuFrontend",
                "connection_history.json");

            if (!File.Exists(path))
            {
                LoadSampleHistory();
                return;
            }

            string json = File.ReadAllText(path);
            var items = JsonSerializer.Deserialize<ConnectionHistoryItem[]>(json);
            if (items == null || items.Length == 0)
            {
                LoadSampleHistory();
                return;
            }

            foreach (var item in items.OrderByDescending(x => x.Timestamp))
                Items.Add(item);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载历史记录失败: {ex.Message}");
            LoadSampleHistory();
        }
    }

    private void LoadSampleHistory()
    {
        Items.Add(new ConnectionHistoryItem
        {
            Address = "",
            Port = "",
            Password = "",
            BackendId = "",
            Timestamp = DateTime.Now,
            UseCount = 1
        });
    }

    private void SaveHistory()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuzuFrontend");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "connection_history.json");
            string json = JsonSerializer.Serialize(Items.ToArray(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存历史记录失败: {ex.Message}");
        }
    }
}

public class ConnectionHistoryItem
{
    public string Address { get; set; } = "";
    public string Port { get; set; } = "";
    public string Password { get; set; } = "";
    public string BackendId { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public int UseCount { get; set; } = 1;

    public string BackendName => $"{Address}:{Port}";

    public string FriendlyTime
    {
        get
        {
            var diff = DateTime.Now - Timestamp;
            if (diff.TotalMinutes < 1) return "刚刚";
            if (diff.TotalHours < 1) return $"{diff.Minutes}分钟前";
            if (diff.TotalDays < 1) return $"{diff.Hours}小时前";
            return diff.TotalDays < 7 ? $"{diff.Days}天前" : Timestamp.ToString("yyyy-MM-dd");
        }
    }

    public string FrequencyLevel => UseCount switch
    {
        <= 0 => "未使用",
        1 => "偶尔使用",
        >= 2 and <= 5 => "经常使用",
        >= 6 and <= 10 => "频繁使用",
        _ => "常用"
    };
}
