using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 配置文件持久化。保存已知服务器的 RSA 公钥指纹到 %AppData%/YuzuFrontend。
/// 用于在首次连接后记住服务器指纹，后续连接时进行比对以防范中间人攻击。
/// </summary>
public static class StaticConfigManagerClass
{
    /// <summary>应用数据根目录：%AppData%/YuzuFrontend。</summary>
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YuzuFrontend");

    /// <summary>主配置文件路径（预留）。</summary>
    public static string ConfigFilePath => Path.Combine(AppDataDir, "config.json");
    /// <summary>已知服务器指纹表文件路径。</summary>
    public static string KnownServersFilePath => Path.Combine(AppDataDir, "known_servers.json");

    /// <summary>已知服务器指纹表，key = "IP:Port", value = RSA 公钥指纹</summary>
    public static Dictionary<string, string> KnownServers { get; private set; } = new();

    static StaticConfigManagerClass()
    {
        LoadConfig();
    }

    /// <summary>
    /// 从磁盘加载已知服务器指纹表。文件不存在或解析失败时使用空表。
    /// </summary>
    /// <remarks>Native AOT：使用编译期生成的 <see cref="KnownServersJsonContext"/>，无运行时代码生成。</remarks>
    public static void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            if (File.Exists(KnownServersFilePath))
            {
                var raw = File.ReadAllText(KnownServersFilePath);
                var dict = JsonSerializer.Deserialize(raw, KnownServersJsonContext.Default.DictionaryStringString);
                if (dict != null) KnownServers = dict;
            }
        }
        catch
        {
            KnownServers = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 将当前已知服务器指纹表序列化写入磁盘（缩进格式）。
    /// IO 异常被静默忽略，不影响运行时功能。
    /// </summary>
    /// <remarks>Native AOT 说明同 <see cref="LoadConfig"/>。</remarks>
    public static void SaveKnownServers()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(KnownServers, KnownServersJsonContext.Default.DictionaryStringString);
            File.WriteAllText(KnownServersFilePath, json);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 查询指定地址+端口对应的已知服务器指纹。
    /// </summary>
    /// <param name="address">服务器地址。</param>
    /// <param name="port">服务器端口。</param>
    /// <returns>已记录的指纹；未记录返回 null。</returns>
    public static string? GetKnownFingerprint(string address, int port)
    {
        var key = $"{address}:{port}";
        return KnownServers.TryGetValue(key, out var fp) ? fp : null;
    }

    /// <summary>
    /// 记录或更新指定地址+端口的服务器指纹，并立即持久化。
    /// </summary>
    /// <param name="address">服务器地址。</param>
    /// <param name="port">服务器端口。</param>
    /// <param name="fingerprint">RSA 公钥指纹。</param>
    public static void SetKnownFingerprint(string address, int port, string fingerprint)
    {
        var key = $"{address}:{port}";
        KnownServers[key] = fingerprint;
        SaveKnownServers();
    }
}

/// <summary>
/// 已知服务器指纹表的 System.Text.Json 编译期序列化上下文（Native AOT 必需）。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class KnownServersJsonContext : JsonSerializerContext
{
}
