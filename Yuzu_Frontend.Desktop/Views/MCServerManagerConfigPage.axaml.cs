using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Material.Icons;
using System;
using System.Collections.Generic;
using System.Linq;
using Yuzu_Frontend.Desktop.Models;
using Yuzu_Frontend.Models;

namespace Yuzu_Frontend.Desktop.Views;

[Page("管理器设置", MaterialIconKind.Cog, Order = 3, IsCollection = false)]
public partial class MCServerManagerConfigPage : NavigatedPageBase
{
    public MCServerManagerConfigViewModel ViewModel { get; }

    public MCServerManagerConfigPage()
    {
        ViewModel = new MCServerManagerConfigViewModel();
        InitializeComponent();
        InitializeSectionIcons();
        ContentScrollViewer.ScrollChanged += OnScrollChanged;
    }

    private void InitializeSectionIcons()
    {
        var icons = new Dictionary<string, MaterialIconKind>
        {
            { "基本设置", MaterialIconKind.Settings },
            { "备份设置", MaterialIconKind.BackupRestore },
            { "备份排除", MaterialIconKind.FilterList },
            { "远程备份", MaterialIconKind.CloudUpload },
            { "在线聊天", MaterialIconKind.ChatBubble }
        };

        foreach (var item in SectionList.Items)
        {
            if (item is string sectionName && icons.TryGetValue(sectionName, out var kind))
            {
                var container = SectionList.ContainerFromItem(item) as ListBoxItem;
                if (container != null)
                {
                    var icon = container.FindDescendantOfType<Material.Icons.Avalonia.MaterialIcon>();
                    if (icon != null)
                    {
                        icon.Kind = kind;
                    }
                }
            }
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateActiveSection();
    }

    private void UpdateActiveSection()
    {
        var scrollViewer = ContentScrollViewer;
        if (scrollViewer == null) return;

        var scrollOffset = scrollViewer.Offset.Y;
        var viewportHeight = scrollViewer.Viewport.Height;
        var halfViewport = viewportHeight / 2;

        var sections = new List<Tuple<string, Control>>
        {
            Tuple.Create("基本设置", (Control)BasicSection),
            Tuple.Create("备份设置", (Control)BackupSection),
            Tuple.Create("备份排除", (Control)ExclusionSection),
            Tuple.Create("远程备份", (Control)RemoteBackupSection),
            Tuple.Create("在线聊天", (Control)ChatSection)
        };

        foreach (var section in sections)
        {
            var sectionOffset = section.Item2.Bounds.Top;
            var sectionHeight = section.Item2.Bounds.Height;

            if (scrollOffset + halfViewport >= sectionOffset &&
                scrollOffset + halfViewport <= sectionOffset + sectionHeight)
            {
                ViewModel.ActiveSection = section.Item1;
                return;
            }
        }
    }

    private async void BrowseServerDirectory(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择服务端目录"
        });

        if (result.Count >= 1)
        {
            ViewModel.MCServerDirectory = result[0].Path.LocalPath;
        }
    }

    private async void BrowseJavaPath(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择Java可执行文件",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java可执行文件") { Patterns = new[] { "*.exe", "*.bin" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
            }
        });

        if (result.Count >= 1)
        {
            ViewModel.JavaPath = result[0].Path.LocalPath;
        }
    }

    private async void BrowseBackupDirectory(object sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择备份目录"
        });

        if (result.Count >= 1)
        {
            ViewModel.BackupFileOutputDirectory = result[0].Path.LocalPath;
        }
    }

    private void RemoveExcludedFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string file)
        {
            ViewModel.RemoveExcludedFile(file);
        }
    }

    private void RemoveExcludedExtension_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string ext)
        {
            ViewModel.RemoveExcludedExtension(ext);
        }
    }

    private void RemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string folder)
        {
            ViewModel.RemoveExcludedFolder(folder);
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        InitializeSectionIcons();
    }

    public void ScrollToSection(string sectionName)
    {
        var targetSection = FindSectionByName(sectionName);
        if (targetSection != null)
        {
            targetSection.BringIntoView(new Rect(0, 0, targetSection.Bounds.Width, targetSection.Bounds.Height));
        }
    }

    private Control? FindSectionByName(string sectionName)
    {
        return sectionName switch
        {
            "基本设置" => BasicSection,
            "备份设置" => BackupSection,
            "备份排除" => ExclusionSection,
            "远程备份" => RemoteBackupSection,
            "在线聊天" => ChatSection,
            _ => null
        };
    }
}

public class BackupModeConverter : IValueConverter
{
    public static readonly BackupModeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is PMCSsE_Communicator.BackupTimingMode mode && parameter is string param)
        {
            if (param == "DayInterval_SpecificTime")
            {
                return mode == PMCSsE_Communicator.BackupTimingMode.DayInterval_SpecificTime;
            }
            else if (param == "FixedTimeInterval")
            {
                return mode == PMCSsE_Communicator.BackupTimingMode.FixedTimeInterval;
            }
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}