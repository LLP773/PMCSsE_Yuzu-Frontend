using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Yuzu_Frontend.Desktop.Models;

/// <summary>
/// DataGrid 列宽持久化存储器。
/// 将各页面中 DataGrid 控件的列宽按"页面键 → 列名 → 宽度"的结构
/// 序列化为 JSON 文件保存到 %AppData%/YuzuFrontend 目录下，
/// 以便在应用重启后恢复用户自定义的列宽布局。
/// </summary>
public static class ColumnWidthStorage
{
    // 持久化文件名，存放在 %AppData%/YuzuFrontend 目录下
    private const string FileName = "datagrid_column_widths.json";

    /// <summary>
    /// 单个页面的列宽配置项，记录页面标识与该页面下各列的宽度映射。
    /// ColumnWidthConfig 仅含 string 与 Dictionary&lt;string, double&gt;，均为 AOT 静态可达类型。
    /// </summary>
    public class ColumnWidthConfig
    {
        /// <summary>页面唯一标识，用于区分不同页面的列宽配置。</summary>
        public string PageKey { get; set; } = "";

        /// <summary>列名到列宽（像素）的映射表。</summary>
        public Dictionary<string, double> ColumnWidths { get; set; } = new();
    }

    /// <summary>
    /// 获取持久化文件的完整路径，若所在目录不存在则先创建。
    /// </summary>
    /// <returns>%AppData%/YuzuFrontend/datagrid_column_widths.json 的完整路径。</returns>
    public static string GetStoragePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuzuFrontend");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, FileName);
    }

    /// <summary>
    /// 从文件加载所有页面的列宽配置。
    /// 文件不存在或解析失败时返回空字典，保证调用方不会因 IO/格式错误而中断。
    /// </summary>
    /// <returns>页面键到列宽配置的映射表；加载失败时返回空表。</returns>
    /// <remarks>
    /// Native AOT：使用编译期生成的 <see cref="ColumnWidthJsonContext"/>，
    /// 不依赖运行时反射发值，无需任何裁剪/动态代码抑制。
    /// </remarks>
    public static Dictionary<string, ColumnWidthConfig> LoadAll()
    {
        try
        {
            string path = GetStoragePath();
            if (!File.Exists(path)) return new Dictionary<string, ColumnWidthConfig>();

            string json = File.ReadAllText(path);
            var typeInfo = ColumnWidthJsonContext.Default.GetTypeInfo(typeof(Dictionary<string, ColumnWidthConfig>));
            var configs = JsonSerializer.Deserialize(json, (JsonTypeInfo<Dictionary<string, ColumnWidthConfig>>)typeInfo!);
            return configs ?? new Dictionary<string, ColumnWidthConfig>();
        }
        catch (Exception)
        {
            // 读取或反序列化失败时回退为空表，不影响运行
            return new Dictionary<string, ColumnWidthConfig>();
        }
    }

    /// <summary>
    /// 加载指定页面的列宽配置。
    /// </summary>
    /// <param name="pageKey">页面唯一标识。</param>
    /// <returns>匹配的列宽配置；不存在时返回 <c>null</c>。</returns>
    public static ColumnWidthConfig? Load(string pageKey)
    {
        var allConfigs = LoadAll();
        return allConfigs.TryGetValue(pageKey, out var config) ? config : null;
    }

    /// <summary>
    /// 保存指定页面的列宽配置：先读取全部配置，更新对应页面的条目，再整体写回文件。
    /// </summary>
    /// <param name="pageKey">页面唯一标识。</param>
    /// <param name="columnWidths">列名到列宽的映射。</param>
    /// <remarks>Native AOT 说明同 <see cref="LoadAll"/>。</remarks>
    public static void Save(string pageKey, Dictionary<string, double> columnWidths)
    {
        try
        {
            // 先加载已有配置以保留其他页面的数据，再覆盖当前页面项
            var allConfigs = LoadAll();
            allConfigs[pageKey] = new ColumnWidthConfig
            {
                PageKey = pageKey,
                ColumnWidths = columnWidths
            };

            string path = GetStoragePath();
            string json = JsonSerializer.Serialize(allConfigs,
                (JsonTypeInfo<Dictionary<string, ColumnWidthConfig>>)ColumnWidthJsonContext.Default
                    .GetTypeInfo(typeof(Dictionary<string, ColumnWidthConfig>))!);
            File.WriteAllText(path, json);
        }
        catch (Exception)
        {
            // 持久化失败不影响运行时的列宽使用，静默忽略
        }
    }

    /// <summary>
    /// 清除指定页面的列宽配置。仅在确实移除了对应条目时才回写文件。
    /// </summary>
    /// <param name="pageKey">要清除的页面唯一标识。</param>
    /// <remarks>Native AOT 说明同 <see cref="LoadAll"/>。</remarks>
    public static void Clear(string pageKey)
    {
        try
        {
            var allConfigs = LoadAll();
            if (allConfigs.Remove(pageKey))
            {
                string path = GetStoragePath();
                string json = JsonSerializer.Serialize(allConfigs,
                    (JsonTypeInfo<Dictionary<string, ColumnWidthConfig>>)ColumnWidthJsonContext.Default
                        .GetTypeInfo(typeof(Dictionary<string, ColumnWidthConfig>))!);
                File.WriteAllText(path, json);
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 清除所有页面的列宽配置：直接删除持久化文件。
    /// </summary>
    public static void ClearAll()
    {
        try
        {
            string path = GetStoragePath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// 列宽持久化的 System.Text.Json 编译期序列化上下文（Native AOT 必需）。
/// 在编译期为 Dictionary&lt;string, ColumnWidthConfig&gt; 生成无反射的（反）序列化代码。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, ColumnWidthStorage.ColumnWidthConfig>))]
internal partial class ColumnWidthJsonContext : JsonSerializerContext
{
}
