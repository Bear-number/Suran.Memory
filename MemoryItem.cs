using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Suran.Memory;

/// <summary>
/// 一条记忆。摘要进上下文，原文落盘（archive 相对路径）。
/// id 带时间戳与随机尾：两个进程同毫秒也不会撞（旧插件用文件名当 id，同名即冲突）。
/// </summary>
public class MemoryItem
{
    public string Id { get; set; } = "";

    /// <summary>
    /// 给模型用的 6 位短码。完整索引有 31 字符（`3-20261031141657-20261101054025`），
    /// 模型抄错一位就查不到；短码由 Id 哈希得来，稳定、可复制、不会变。
    /// </summary>
    public string ShortId { get; set; } = "";

    public static string MakeShortId(string id, int length = 6)
    {
        if (string.IsNullOrEmpty(id))
            return "";
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(id));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hex[..Math.Clamp(length, 4, hex.Length)];
    }
    /// <summary>1..MaxLevel；导入的旧存档保留原等级</summary>
    public int Level { get; set; } = 1;
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public long StartTs { get; set; }
    public long EndTs { get; set; }
    public double Importance { get; set; } = 0.5;
    public bool Pinned { get; set; }
    /// <summary>当前是否在上下文（记忆区）里。淘汰后置 false，但库中仍可检索</summary>
    public bool InContext { get; set; } = true;
    /// <summary>原文相对路径，如 L1/m-20261104....txt</summary>
    public string ArchiveRel { get; set; } = "";
    public long CreatedAt { get; set; }
    /// <summary>auto=自动压缩 / manual=模型主动记 / legacy=旧存档导入</summary>
    public string Source { get; set; } = "auto";
    /// <summary>这条摘要由多少条原始消息压成（导入的为 0）</summary>
    public int MsgCount { get; set; }
    /// <summary>旧存档的索引名（导入用，兼作幂等键：没有原文文件的条目只能靠它去重）</summary>
    public string LegacyKey { get; set; } = "";
    /// <summary>
    /// 永久层：L100 及以上，或者手动钉住。两者都不参与淘汰和升级合并。
    /// 官方把「永久记忆」交给模型自己填 level=100 来表达，这里就沿用这个约定，
    /// 免得只靠一个开关——万一忘了开，规矩就被淘汰掉了。
    /// </summary>
    public bool IsPermanent => Pinned || Level >= 100;

    /// <summary>这条被哪条新存档取代了（矛盾作废）。非空表示"结论已过时"，不移入上下文但仍在库里可查</summary>
    public string SupersededBy { get; set; } = "";

    /// <summary>
    /// 事实键：「对象.方面」（如 `他.身高`）。同一个键 = 同一件会变的事，
    /// 新的一条进来时旧的自动标作废。
    ///
    /// 为什么需要：原来只靠模型在压缩时自己写「替代：某索引」——它得在几十条存档里
    /// 认出"这条推翻了那条"，还得把索引抄对。这件事它经常不做，或者做错。
    /// 有键之后，取代判断从"模型回忆"变成"字符串相等"。
    /// 空 = 没给键（不是"没有事实"，只是享受不到这个便利）。
    /// </summary>
    public string FactKey { get; set; } = "";

    /// <summary>
    /// 摘要里没法自然带、但丢了就再也猜不回来的东西：路径、版本号、命令行开关、错误码。
    /// 压缩时从原文机械抽取，和摘要比对后只留"摘要没覆盖"的那些。
    /// 这部分不进 Summary，是为了让界面上编辑摘要时不会误删它。
    /// </summary>
    public string Facts { get; set; } = "";

    /// <summary>
    /// 压成这条存档时她的心情/体力/亲近（0~1）。null = 那一版还没有状态插件，或读不到。
    ///
    /// 为什么要记：摘要里只有"发生了什么"，没有"她当时什么感觉"。
    /// 有了这一组，才能做两件事——检索时按情绪一致性重排，
    /// 以及以后从多条存档里提炼"她一般怎么看这件事"（规律/信念）。
    /// </summary>
    public double? StateMood { get; set; }
    public double? StateEnergy { get; set; }
    public double? StateCloseness { get; set; }

    /// <summary>被检索命中（MemorySearch / MemoryRecall）的次数，用来算激活度</summary>
    public int Hits { get; set; }

    /// <summary>最近一次被命中的时刻，同上</summary>
    public long LastHitTs { get; set; }

    /// <summary>最近一次被「回忆」挑中的时刻。同一条冷却期内不再挑，否则就不是想起而是复读</summary>
    public long LastRecalledTs { get; set; }

    public DateTime StartLocal => DateTimeOffset.FromUnixTimeMilliseconds(StartTs).LocalDateTime;
    public DateTime EndLocal => DateTimeOffset.FromUnixTimeMilliseconds(EndTs).LocalDateTime;

    /// <summary>上下文里那一行的头：带稳定 id，便于模型用 MemoryRecall 精确回取</summary>
    /// <summary>
    /// 存档消息的头。**必须以 `[记忆存档(索引)]` 开头**——宿主的识别正则就认这个前缀，
    /// 换成别的字样，这条消息会被当成普通原文再压一遍（早期写成 `[记忆档案 …]`，踩过）。
    /// 标签后面再补等级和时间，方便人和模型一眼看出这是哪一段。
    /// </summary>
    public string Header()
    {
        string span = StartTs == EndTs
            ? StartLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : StartLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "~"
              + EndLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        return "[记忆存档(" + Id + ")] L" + Level + " " + span + " " + Title;
    }

    /// <summary>进上下文的消息正文 = 头 + 摘要（+ 原文锚点）</summary>
    public string Render()
    {
        string body = Header() + "\n" + Summary.Trim();
        return Facts.Length == 0 ? body : body + "\n[原文锚点] " + Facts;
    }

    public static string NewId(DateTime at)
    {
        string tail = Guid.NewGuid().ToString("N")[..4];
        return "m-" + at.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" + tail;
    }

    /// <summary>
    /// 存档索引的"同一性"：把新旧两种写法归一化到同一把钥匙。
    ///
    /// 早期版本写进历史的是 `{等级}-{yyyyMMdd}-{HHmm}-{尾号}`（`4-20261104-1314-8966`），
    /// 后来 `NewId` 改成 `m-{yyyyMMdd}-{HHmm}-{尾号}`。同一条记忆因此有两个身份证：
    /// 删除时按新 id 找不到旧消息，那条记忆就永远留在上下文里——用户看到的就是"删了还在"。
    /// 压缩产出的是 `{等级}-{14位起}-{14位止}`，没有尾号，原样返回，不会被误认成同一条。
    /// </summary>
    public static string KeyOf(string id)
    {
        string[] parts = id.Split('-');
        if (parts.Length == 4 && parts[1].Length == 8 && parts[2].Length == 4)
            return parts[1] + "-" + parts[2] + "-" + parts[3];
        return id;
    }
}

/// <summary>旧插件的存档索引：`等级-名称`</summary>
public readonly record struct LegacyIndex(int Level, string Name)
{
    public static LegacyIndex? Parse(string raw)
    {
        int split = raw.IndexOf('-');
        if (split <= 0)
            return null;
        if (!int.TryParse(raw[..split], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level))
            return null;
        string name = raw[(split + 1)..];
        return name.Length == 0 ? null : new LegacyIndex(level, name);
    }
}

/// <summary>中文二元组 + 英文/数字词。纯本地，不依赖分词库。</summary>
public static class MemoryTokenizer
{
    static readonly HashSet<string> Stop = new(StringComparer.Ordinal) {
        "的", "了", "是", "在", "我", "你", "他", "她", "它", "和", "就", "都", "也", "很", "有", "不", "这", "那", "一个", "什么"
    };

    public static IEnumerable<string> Tokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        // 标识符整体成词，排在前面。理由：`D:\Alife\Storage\Plugins` 按普通规则会碎成
        // "alife" "storage" "plugins" 三个高频词，唯一性归零——而它恰恰是摘要最先丢、
        // 丢了又最猜不回来的东西。整体保留一份，碎片照旧保留，这样"苏染"和完整路径都能搜到。
        foreach (string identifier in MemoryText.Identifiers(text))
            yield return identifier.Replace('\\', '/').ToLowerInvariant();

        StringBuilder ascii = new();
        char prev = '\0';
        foreach (char ch in text)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                ascii.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                if (ascii.Length >= 2)
                    yield return ascii.ToString();
                ascii.Clear();
            }

            if (IsCjk(ch))
            {
                if (IsCjk(prev))
                {
                    string bigram = string.Concat(prev, ch);
                    if (!Stop.Contains(bigram))
                        yield return bigram;
                }
                prev = ch;
            }
            else
            {
                prev = '\0';
            }
        }

        if (ascii.Length >= 2)
            yield return ascii.ToString();
    }

    public static bool IsCjk(char ch)
    {
        return (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF);
    }

    public static bool IsStopWord(string token)
    {
        return Stop.Contains(token);
    }

    public static List<string> Distinct(string text)
    {
        return Tokens(text).Distinct(StringComparer.Ordinal).Take(400).ToList();
    }
}
