using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 配置文件持久化。保存已知服务器的 RSA 公钥指纹到 %AppData%/YuzuFrontend。
/// </summary>
public static class StaticConfigManagerClass
{
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YuzuFrontend");

    public static string ConfigFilePath => Path.Combine(AppDataDir, "config.json");
    public static string KnownServersFilePath => Path.Combine(AppDataDir, "known_servers.json");

    /// <summary>key = "IP:Port", value = RSA 公钥指纹</summary>
    public static Dictionary<string, string> KnownServers { get; private set; } = new();

    static StaticConfigManagerClass()
    {
        LoadConfig();
    }

    public static void LoadConfig()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            if (File.Exists(KnownServersFilePath))
            {
                var raw = File.ReadAllText(KnownServersFilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                if (dict != null) KnownServers = dict;
            }
        }
        catch
        {
            KnownServers = new Dictionary<string, string>();
        }
    }

    public static void SaveKnownServers()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var json = JsonSerializer.Serialize(KnownServers, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(KnownServersFilePath, json);
        }
        catch
        {
        }
    }

    public static string? GetKnownFingerprint(string address, int port)
    {
        var key = $"{address}:{port}";
        return KnownServers.TryGetValue(key, out var fp) ? fp : null;
    }

    public static void SetKnownFingerprint(string address, int port, string fingerprint)
    {
        var key = $"{address}:{port}";
        KnownServers[key] = fingerprint;
        SaveKnownServers();
    }
}
