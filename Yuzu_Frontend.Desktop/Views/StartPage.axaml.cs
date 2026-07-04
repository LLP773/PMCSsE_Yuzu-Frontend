using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
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

/// <summary>
/// 起始连接页面。负责管理后端连接的建立与断开、连接历史记录的持久化，
/// 并在已连接后端列表为空时显示空状态提示。
/// </summary>
[Page("连接", MaterialIconKind.LanConnect, Order = 0, IsCollection = false)]
public partial class StartPage : NavigatedPageBase
{
    /// <summary>连接历史记录集合，绑定到 UI 列表展示。</summary>
    public ObservableCollection<ConnectionHistoryItem> Items { get; } = new RangeObservableCollection<ConnectionHistoryItem>();

    /// <summary>
    /// 初始化 <see cref="StartPage"/> 的新实例：加载 XAML 并从磁盘加载历史连接记录。
    /// </summary>
    public StartPage()
    {
        InitializeComponent();
        LoadHistory();
    }


    /// <summary>
    /// 页面初始化入口：解绑旧的 Connection 事件订阅，注入新的 Connection（如未传入则创建新的），
    /// 重新订阅属性变化与连接成功事件，并刷新空状态提示。
    /// </summary>
    /// <param name="toastManager">全局 Toast 提示管理器。</param>
    /// <param name="dialogManager">全局对话框管理器。</param>
    /// <param name="connectionViewModel">当前活动连接的视图模型；为 null 时将创建新实例。</param>
    public override void InitializePage(
        ISukiToastManager toastManager,
        ISukiDialogManager dialogManager,
        ConnectionViewModel? connectionViewModel = null)
    {
        var oldVm = Connection;
        if (oldVm != null)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.Connected -= OnConnectionConnected;
        }

        base.InitializePage(toastManager, dialogManager, connectionViewModel ?? new ConnectionViewModel());

        SyncEmptyBackendsHint();

        if (Connection != null)
        {
            Connection.PropertyChanged += OnViewModelPropertyChanged;
            Connection.Connected += OnConnectionConnected;
        }
    }

    /// <summary>
    /// 监听 Connection 属性变化：当 HasConnectedBackends 变化时刷新空状态提示。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionViewModel.HasConnectedBackends))
            SyncEmptyBackendsHint();
    }

    /// <summary>连接成功回调：清空连接输入框中的敏感信息。</summary>
    private void OnConnectionConnected()
    {
        ClearConnectionInputs();
    }

    /// <summary>
    /// 根据当前是否已连接后端切换"空状态提示"与"后端表格"的可见性。
    /// </summary>
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

    /// <summary>
    /// "连接" 按钮点击事件：读取表单输入并向后端发起连接；
    /// 同时根据"保存密码"与"保存到历史"选项持久化历史记录。
    /// </summary>
    private void ButtonConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (Connection == null) return;

        var address = string.IsNullOrWhiteSpace(Connection.ConnectBackendAddress) ? "" : Connection.ConnectBackendAddress.Trim();
        var port = string.IsNullOrWhiteSpace(Connection.ConnectBackendPort) ? "" : Connection.ConnectBackendPort.Trim();
        var password = Connection.ConnectBackendPassword ?? "";

        if (Connection.IsConnecting)
            return;

        Connection.ConnectToBackend(address, port, password);

        // 仅在用户勾选"保存密码"时持久化密码，否则保存空字符串
        string? passwordToSave = Connection.SavePassword ? password : null;
        if (Connection.SaveToHistory)
        {
            AddToHistory(address, port, passwordToSave ?? "");
        }
    }

    /// <summary>清空连接表单中的地址、端口与密码输入。</summary>
    private void ClearConnectionInputs()
    {
        if (Connection == null) return;
        Connection.ConnectBackendAddress = "";
        Connection.ConnectBackendPort = "";
        Connection.ConnectBackendPassword = "";
    }

    /// <summary>"断开所选" 按钮点击事件：断开所有勾选的后端连接。</summary>
    private void ButtonDisconnectSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (Connection == null) return;

        var selected = Connection.ConnectedBackends.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) return;

        Connection.DisconnectBackends(selected);
    }

    /// <summary>历史记录项点击事件：将历史项填充到连接表单。</summary>
    private void HistoryItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ConnectionHistoryItem item)
        {
            SelectHistory(item);
        }
    }

    /// <summary>历史记录"删除"按钮点击事件：移除单条历史记录并保存。</summary>
    private void HistoryDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ConnectionHistoryItem item)
        {
            Items.Remove(item);
            SaveHistory();
        }
    }

    /// <summary>历史记录"清空"按钮点击事件：清空全部历史记录并保存。</summary>
    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        Items.Clear();
        SaveHistory();
    }

    /// <summary>
    /// 选中一条历史记录：将其字段回填到连接表单，并更新使用次数与时间戳。
    /// </summary>
    /// <param name="item">被选中的历史记录项。</param>
    private void SelectHistory(ConnectionHistoryItem item)
    {
        if (Connection != null)
        {
            Connection.ConnectBackendAddress = item.Address;
            Connection.ConnectBackendPort = item.Port;
            Connection.ConnectBackendPassword = item.Password ?? "";
        }

        item.UseCount++;
        item.Timestamp = DateTime.Now;
        SaveHistory();
    }

    /// <summary>
    /// 将一条连接信息添加到历史记录。
    /// 若已存在相同地址+端口的记录则更新其字段并置顶；否则新建并插入到首位。
    /// 列表长度上限为 20 条，超出则丢弃尾部记录。
    /// </summary>
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

        // 维持历史记录上限：超出 20 条时移除最末项
        while (Items.Count > 20) Items.RemoveAt(Items.Count - 1);
        SaveHistory();
    }

    /// <summary>
    /// 从 %AppData%/YuzuFrontend/connection_history.json 加载历史连接记录。
    /// 文件不存在或反序列化失败时加载一条空示例记录。
    /// </summary>
    /// <remarks>
    /// Native AOT：使用编译期生成的 <see cref="ConnectionHistoryJsonContext"/>，无运行时代码生成。
    /// </remarks>
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
            var items = JsonSerializer.Deserialize(json, ConnectionHistoryJsonContext.Default.ConnectionHistoryItemArray);
            if (items == null || items.Length == 0)
            {
                LoadSampleHistory();
                return;
            }

            // 一次性批量替换：Clear + N Add → 1 次 Reset 通知
            (Items as RangeObservableCollection<ConnectionHistoryItem>)?.ReplaceAll(
                items.OrderByDescending(x => x.Timestamp));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载历史记录失败: {ex.Message}");
            LoadSampleHistory();
        }
    }

    /// <summary>加载一条空示例历史记录，用于首次启动或历史文件损坏时占位。</summary>
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

    /// <summary>
    /// 将当前历史记录序列化为 JSON 写入 %AppData%/YuzuFrontend/connection_history.json。
    /// 目录不存在时会自动创建。
    /// </summary>
    /// <remarks>Native AOT 说明同 <see cref="LoadHistory"/>。</remarks>
    private void SaveHistory()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuzuFrontend");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "connection_history.json");
            string json = JsonSerializer.Serialize(Items.ToArray(), ConnectionHistoryJsonContext.Default.ConnectionHistoryItemArray);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"保存历史记录失败: {ex.Message}");
        }
    }
}

/// <summary>
/// 连接历史记录项。保存一次后端连接的地址、端口、密码及使用统计信息，
/// 供"连接"页面快速重连使用。
/// </summary>
public class ConnectionHistoryItem
{
    /// <summary>后端服务地址。</summary>
    public string Address { get; set; } = "";
    /// <summary>后端服务端口。</summary>
    public string Port { get; set; } = "";
    /// <summary>保存的连接密码（仅在用户勾选"保存密码"时持久化）。</summary>
    public string Password { get; set; } = "";
    /// <summary>由 <see cref="BackendIdManager"/> 分配的后端唯一 ID。</summary>
    public string BackendId { get; set; } = "";
    /// <summary>最近一次使用该历史记录的时间戳。</summary>
    public DateTime Timestamp { get; set; }
    /// <summary>该历史记录被使用的累计次数。</summary>
    public int UseCount { get; set; } = 1;

    /// <summary>获取 "地址:端口" 形式的展示名称（纯展示用，不参与 JSON 持久化）。</summary>
    [JsonIgnore]
    public string BackendName => $"{Address}:{Port}";

    /// <summary>
    /// 根据时间戳计算相对当前时间的友好显示文本（刚刚 / X 分钟前 / X 小时前 / X 天前 / yyyy-MM-dd）。
    /// 纯展示用，不参与 JSON 持久化。
    /// </summary>
    [JsonIgnore]
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

    /// <summary>
    /// 根据 UseCount 返回使用频率等级（未使用 / 偶尔使用 / 经常使用 / 频繁使用 / 常用）。
    /// 纯展示用，不参与 JSON 持久化。
    /// </summary>
    [JsonIgnore]
    public string FrequencyLevel => UseCount switch
    {
        <= 0 => "未使用",
        1 => "偶尔使用",
        >= 2 and <= 5 => "经常使用",
        >= 6 and <= 10 => "频繁使用",
        _ => "常用"
    };
}

/// <summary>
/// 连接历史记录的 System.Text.Json 编译期序列化上下文（Native AOT 必需）。
/// 在编译期为 <see cref="ConnectionHistoryItem"/>[] 生成无反射的（反）序列化代码。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ConnectionHistoryItem[]))]
internal partial class ConnectionHistoryJsonContext : JsonSerializerContext
{
}
