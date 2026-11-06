using System;
using System.IO;
using System.Text.Json;
using Alife.Foundation;

namespace Suran.Memory;

/// <summary>
/// 某一刻的她。只读——记忆插件不改状态，只是"记下当时什么心情"和"检索时知道现在什么心情"。
/// </summary>
public sealed class MoodSnapshot
{
    public double Mood { get; set; } = 0.55;
    public double Energy { get; set; } = 0.72;
    public double Closeness { get; set; } = 0.55;
    public string Doing { get; set; } = "";
    /// <summary>这份状态是什么时候的（状态插件自己的 UpdatedAt）</summary>
    public long At { get; set; }
}

/// <summary>
/// 状态插件（Suran.Mood）的只读镜像。
///
/// 为什么读文件而不是取模块实例：
///   状态插件可能没装、可能被禁用、可能正在重载——记忆不该因为"邻居不在"就压不了。
///   一个不存在的文件就是"不知道"，全部退回中性值，压缩照跑、检索照排。
///
/// 路径契约写在两边的 README 里：Storage/&lt;角色&gt;/Mood/state.json。
/// 改这个路径等于拆掉这条连接，所以它要显式写下来，而不是靠"反正就在那儿"。
/// </summary>
public static class MoodMirror
{
    static MoodSnapshot? cached;
    static DateTime cachedAt = DateTime.MinValue;
    static string cachedPath = "";

    /// <summary>缓存 30 秒：检索可能一次调好几十遍，没必要每次都读盘</summary>
    static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static MoodSnapshot? Read(string characterKey)
    {
        string path = Path.Combine(AlifePath.StorageFolderPath, characterKey, "Mood", "state.json");

        if (cached != null && cachedPath == path && DateTime.Now - cachedAt < Ttl)
            return cached;

        cachedPath = path;
        cachedAt = DateTime.Now;
        try
        {
            if (!File.Exists(path))
            {
                cached = null;
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                cached = null;
                return null;
            }

            cached = new MoodSnapshot {
                Mood = Read(root, "Mood", 0.55),
                Energy = Read(root, "Energy", 0.72),
                Closeness = Read(root, "Closeness", 0.55),
                Doing = root.TryGetProperty("Doing", out JsonElement doing) && doing.ValueKind == JsonValueKind.String
                    ? doing.GetString() ?? ""
                    : "",
                At = root.TryGetProperty("UpdatedAt", out JsonElement at) && at.ValueKind == JsonValueKind.Number
                    ? at.GetInt64()
                    : 0,
            };
            return cached;
        }
        catch
        {
            // 状态文件坏了不该影响记忆。当它不存在
            cached = null;
            return null;
        }

        static double Read(JsonElement root, string name, double fallback)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
                return fallback;
            double number = value.GetDouble();
            return double.IsFinite(number) ? Math.Clamp(number, 0, 1) : fallback;
        }
    }

    /// <summary>检索时按此刻心情给存档加权。纯函数实现在 MemoryBias，这里只是"从快照取一个数"</summary>
    public static Func<MemoryItem, double> BiasFor(MoodSnapshot? now, double strength)
        => MemoryBias.Of(now?.Mood, strength);
}
