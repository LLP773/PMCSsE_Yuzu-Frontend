using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yuzu_Frontend.Modules;

/// <summary>
/// 后端服务 ID 管理器。为每个成功连接的后端服务分配唯一的 Base62 短随机码作为标识 ID，
/// 并将"地址:端口 → ID"的映射关系持久化到本地文件系统。
/// </summary>
public static class BackendIdManager
{
    private const string Base62Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int IdLength = 8;

    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = true };

    /// <summary>映射表文件路径：%AppData%/YuzuFrontend/backend_ids.json</summary>
    public static string IdMapFilePath => Path.Combine(
        StaticConfigManagerClass.AppDataDir, "backend_ids.json");

    /// <summary>key = "address:port", value = Base62 ID</summary>
    private static Dictionary<string, string> _idMap = new();

    static BackendIdManager()
    {
        LoadIdMap();
    }

    /// <summary>
    /// 根据后端地址和端口获取或分配唯一 ID。
    /// 如果该地址+端口已存在映射，则返回已有 ID；否则生成新 ID 并持久化。
    /// </summary>
    public static string GetOrAssignId(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(port))
            return string.Empty;

        var key = $"{address}:{port}";

        lock (_lock)
        {
            if (_idMap.TryGetValue(key, out var existingId))
                return existingId;

            var newId = GenerateUniqueId();
            _idMap[key] = newId;
            SaveIdMap();
            return newId;
        }
    }

    /// <summary>
    /// 根据后端地址和端口查询已分配的 ID，不存在则返回 null。
    /// </summary>
    public static string? GetId(string address, string port)
    {
        var key = $"{address}:{port}";
        lock (_lock)
        {
            return _idMap.TryGetValue(key, out var id) ? id : null;
        }
    }

    /// <summary>
    /// 根据 ID 反查后端地址和端口，不存在则返回 null。
    /// </summary>
    public static (string Address, string Port)? GetAddressPort(string id)
    {
        lock (_lock)
        {
            foreach (var kvp in _idMap)
            {
                if (kvp.Value == id)
                {
                    var parts = kvp.Key.Split(':', 2);
                    if (parts.Length == 2)
                        return (parts[0], parts[1]);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 移除指定后端的 ID 映射记录。
    /// </summary>
    public static void RemoveId(string address, string port)
    {
        var key = $"{address}:{port}";
        lock (_lock)
        {
            if (_idMap.Remove(key))
                SaveIdMap();
        }
    }

    /// <summary>
    /// 从文件加载映射表。文件不存在或格式错误时使用空表。
    /// </summary>
    public static void LoadIdMap()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(StaticConfigManagerClass.AppDataDir);
                if (File.Exists(IdMapFilePath))
                {
                    var raw = File.ReadAllText(IdMapFilePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                    if (dict != null) _idMap = dict;
                }
            }
            catch
            {
                _idMap = new Dictionary<string, string>();
            }
        }
    }

    /// <summary>
    /// 将映射表持久化到文件。使用"先写临时文件再替换"的原子写入策略保证数据一致性。
    /// </summary>
    public static void SaveIdMap()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(StaticConfigManagerClass.AppDataDir);
                var json = JsonSerializer.Serialize(_idMap, _jsonOpts);

                // 原子写入：先写入临时文件，再替换目标文件
                var tempPath = IdMapFilePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, IdMapFilePath, overwrite: true);
            }
            catch
            {
                // 持久化失败不影响运行时功能，ID 仍在内存中有效
            }
        }
    }

    /// <summary>
    /// 使用加密随机数生成器生成 Base62 短随机码。
    /// 8 位 Base62 码有约 218 万亿种组合（62^8），冲突概率极低。
    /// </summary>
    private static string GenerateUniqueId()
    {
        lock (_lock)
        {
            // 生成直到获得不冲突的 ID
            string id;
            do
            {
                id = GenerateBase62Code();
            }
            while (_idMap.ContainsValue(id));
            return id;
        }
    }

    private static string GenerateBase62Code()
    {
        Span<byte> buffer = stackalloc byte[IdLength];
        RandomNumberGenerator.Fill(buffer);

        var chars = new char[IdLength];
        for (int i = 0; i < IdLength; i++)
        {
            // 使用模运算映射到 Base62 字符集
            chars[i] = Base62Chars[buffer[i] % 62];
        }
        return new string(chars);
    }
}
