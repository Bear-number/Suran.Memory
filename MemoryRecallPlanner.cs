using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Suran.Memory;

/// <summary>
/// 回忆：不定期想起一件很久没被想起的旧事。
///
/// 它和「检索」是两件事。检索是有人问、你去翻；回忆是没人问、它自己浮上来。
/// 所以选谁不能按相关度，得按**多久没被想起**——这正是 ACT-R 的可得性 A 回答的问题：
/// A 低 = 快忘了 = 尘封得最久。随机抽签抽不出"忽然想起"，只会抽出嘈杂。
///
/// 但纯按 A 升序也不行：那样每次都会挑同几条最老的，第三次就变成复读。
/// 所以从最低的一段里随机取，兼顾"尘封优先"和"每次不一样"。
/// </summary>
public static class MemoryRecallPlanner
{
    public sealed record Pick(MemoryItem Item, double Availability, string Detail);

    /// <summary>相对时间说法。绝对日期反而没有回忆的味道，但两者都给——模型需要能说清是哪天。</summary>
    public static string When(MemoryItem item, DateTime now)
    {
        DateTime at = item.StartLocal;
        double days = Math.Max(0, (now - at).TotalDays);
        string ago = days switch {
            < 1 => "今天早些时候",
            < 3 => "前两天",
            < 8 => "上周",
            < 32 => "上个月",
            < 100 => "两三个月前",
            < 200 => "半年前",
            < 400 => "快一年前",
            _ => Math.Max(1, (int)(days / 365)) + " 年前"
        };
        return ago + "（" + at.ToString("MM-dd", CultureInfo.InvariantCulture) + "）";
    }

    /// <summary>
    /// 挑几条。冷却内的不挑（同一条一天内想起来两次就不是"想起"了），
    /// 过时的（被新结论取代的）也不挑——回忆一条已经作废的结论没有意义。
    ///
    /// skipInContext：跳过**当前就在上下文里**的条目。
    /// 那些摘要此刻正摆在模型眼前，"想起"它等于把同一段话再说一遍——
    /// 既占掉一次回忆名额，又让模型以为这是一条新情报。
    /// </summary>
    public static List<Pick> Choose(
        IEnumerable<MemoryItem> items,
        int count,
        int maxLevel,
        long nowMs,
        TimeSpan cooldown,
        Random? random = null,
        Func<MemoryItem, string?>? detailReader = null,
        int detailChars = 180,
        bool skipInContext = false)
    {
        count = Math.Clamp(count, 1, 8);
        random ??= Random.Shared;

        List<(MemoryItem Item, double A)> pool = new();
        foreach (MemoryItem item in items)
        {
            if (item.SupersededBy.Length > 0)
                continue;
            // L100 是永久层，数字大不等于"更抽象"，不能和 MaxLevel 一起比
            if (item.Level < 100 && item.Level > maxLevel)
                continue;
            if (item.LastRecalledTs > 0 && nowMs - item.LastRecalledTs < (long)cooldown.TotalMilliseconds)
                continue;
            if (skipInContext && item.InContext)
                continue;
            pool.Add((item, MemoryActivation.AvailabilityOf(item, nowMs)));
        }

        if (pool.Count == 0)
            return new List<Pick>();

        List<(MemoryItem Item, double A)> ordered = pool
            .OrderBy(pair => pair.A)
            .ThenBy(pair => pair.Item.StartTs)
            .ToList();

        // 按排名加权、无放回抽。**不能用洗牌**：洗牌之后排名信息就没了，
        // count=1 时"最尘封的"和"第二尘封的"各 50% —— 那就退化成抽签了。
        // 权重 0.55^rank：第一名权重 1，第二名 0.55，第三名 0.30……
        // 结果是"尘封越久越容易被想起"，但同一条不会每次都中，不会变成复读。
        List<(MemoryItem Item, double A, double Weight)> remain = ordered
            .Select((pair, rank) => (pair.Item, pair.A, Weight: Math.Pow(0.55, rank)))
            .ToList();

        List<Pick> result = new();
        int take = Math.Min(count, remain.Count);
        for (int n = 0; n < take; n++)
        {
            double total = 0;
            foreach ((_, _, double w) in remain)
                total += w;

            double roll = random.NextDouble() * total;
            int index = 0;
            for (; index < remain.Count - 1; index++)
            {
                roll -= remain[index].Weight;
                if (roll <= 0)
                    break;
            }

            (MemoryItem item, double a, _) = remain[index];
            remain.RemoveAt(index);

            string detail = "";
            if (detailReader != null && detailChars > 0)
            {
                string? raw = detailReader(item);
                if (!string.IsNullOrEmpty(raw))
                {
                    string flat = raw.Replace("\r", "").Replace('\n', ' ').Trim();
                    detail = flat.Length <= detailChars ? flat : MemoryText.DensestWindow(flat, detailChars);
                }
            }
            result.Add(new Pick(item, a, detail));
        }
        return result;
    }

    /// <summary>
    /// 拼成给模型看的一段话。
    /// 语气刻意收着：不是下命令「你要回忆」，而是描述发生了什么事——「它们自己浮了上来」。
    /// 回忆是它自己的事，插件只负责把东西递到它面前，说不说由它。
    /// </summary>
    public static string Compose(IReadOnlyList<Pick> picks, DateTime now, int summaryChars = 500)
    {
        StringBuilder sb = new();
        sb.AppendLine("(回忆时间到)");

        if (picks.Count == 0)
        {
            sb.AppendLine("你想翻翻旧事，可是记忆里还没有沉下来什么。");
            sb.AppendLine("那些还没变成存档的日子，其实也都在。");
            return sb.ToString();
        }

        sb.AppendLine("有些事没人提起，却自己浮了上来：");
        foreach (Pick pick in picks)
        {
            MemoryItem item = pick.Item;
            sb.AppendLine();
            sb.Append("【").Append(When(item, now)).Append(" · ").AppendLine(item.Title + "】");
            sb.AppendLine(MemoryStore.Trim(MemoryStore.Flatten(item.Summary), summaryChars));
            if (item.Facts.Length > 0)
                sb.AppendLine("（那会儿提到的：" + item.Facts + "）");
            if (pick.Detail.Length > 0)
                sb.AppendLine("（当时的原话：" + pick.Detail + "）");
        }

        sb.AppendLine();
        sb.AppendLine("久远的事记得未必准。想接着提就自然地说一句，不想说，就只是想一想也好。");
        return sb.ToString();
    }
}
