using System;
using System.Collections.Generic;
using System.Linq;

namespace Suran.Memory;

/// <summary>
/// 记忆动力学：强度不是存下来的，是读时算出来的。
///
/// 原来的淘汰规则是「重要度最低的先走，同重要度里最老的先走」——纯静态。
/// 它有一个说不出口的后果：**一条三个月前压出来、但这三个月被反复检索的存档，
/// 和一条两个月前压出来、从来没人提过的存档，在规则眼里一样老。**
///
/// ACT-R 的 base-level activation 一句话解决这件事：
///     B = ln Σ (t − tⱼ)^−d
/// 创建算一次编码，每次被检索到再算一次。于是
///   · 越近越强（时间衰减）
///   · 越常被用到越强（频率效应）
///   · 间隔着用到比连着用到更强（间隔效应，幂律自带的）
/// 一个参数 d=0.5，整条遗忘曲线就出来了，不需要训练、不需要表结构改动。
///
/// 用法克制：它只参与**淘汰顺序**，不决定"要不要显示"。
/// 显示顺序仍然按历史位置（就地压缩），因为那是用户看得懂的结构；
/// 淘汰是发生在背后的，那里用一条更合理的曲线没有副作用。
/// </summary>
public static class MemoryActivation
{
    const double Day = 86_400_000.0;

    /// <summary>ACT-R 的规范值。不去拟合——没有几千条真实复习日志，拟合出来的只是噪声。</summary>
    public const double Decay = 0.5;

    /// <summary>下限一分钟：同一毫秒内的两次编码不该把 ln 推到 −∞。</summary>
    const double FloorDays = 1.0 / 1440.0;

    public static double BaseLevel(IEnumerable<long> timesMs, long nowMs)
    {
        double sum = 0;
        bool any = false;
        foreach (long at in timesMs)
        {
            any = true;
            double days = Math.Max(FloorDays, (nowMs - at) / Day);
            sum += Math.Pow(days, -Decay);
        }
        return any && sum > 0 ? Math.Log(sum) : double.NegativeInfinity;
    }

    /// <summary>逻辑斯蒂映射到 [0,1]。**序数，不是概率**：读作"比另一条更可得"，不是"62% 能想起来"。</summary>
    public static double Availability(double baseLevel, double threshold = 0, double noise = 0.4)
    {
        if (!double.IsFinite(baseLevel))
            return 0;
        return 1.0 / (1.0 + Math.Exp(-(baseLevel - threshold) / noise));
    }

    /// <summary>
    /// 展示排序用的"该复习了吗"（本项目没有每轮投影，暂时不用，留着是为了别处别再造一遍）。
    /// 可得性越低系数越大——快忘的最该摆出来。
    /// </summary>
    public static double Due(double availability, double high = 0.95)
    {
        return availability <= high
            ? 1.0 + (1.0 - availability)
            : 1.0 / (1.0 + 4.0 * (availability - high));
    }

    /// <summary>
    /// 一条记忆的完整编码痕迹：出生一次，之后每被检索一次算一次。
    ///
    /// 真实实现会存一张 activation 表（每次检索一行）。这里只有 Hits 计数和"最后一次"，
    /// 所以把 Hits 均匀摊在 [出生, 最后一次] 之间重建一份近似痕迹。
    /// 摊开而不是堆在最后一刻，是因为幂律衰减关心的是**间隔**：八次检索挤在同一分钟，
    /// 和八次分布在两个月里，算出来的强度不该一样。
    /// </summary>
    static IEnumerable<long> Trace(MemoryItem item)
    {
        long birth = item.CreatedAt > 0 ? item.CreatedAt : item.StartTs;
        yield return birth;

        int hits = Math.Min(item.Hits, 40);
        if (hits <= 0 || item.LastHitTs <= birth)
            yield break;

        int points = Math.Min(hits, 12);
        long span = item.LastHitTs - birth;
        for (int i = 1; i <= points; i++)
            yield return birth + span * i / points;
    }

    /// <summary>一条记忆现在的可得性 [0,1]。0 = 快忘干净了。</summary>
    public static double AvailabilityOf(MemoryItem item, long nowMs)
    {
        return Availability(BaseLevel(Trace(item), nowMs));
    }

    /// <summary>
    /// 淘汰顺序里的"留存价值"。
    ///
    /// **这里必须用可得性 A，不能用 due(A)。** 两个东西回答的是相反的问题：
    ///   due(A) 问"这条现在值不值得摆出来"——快忘的最该复习，所以 A 低时 due 高；
    ///   A      问"这条还剩多少"——快忘的就是快没了，所以 A 低时该淘汰。
    /// 拿 due 去排淘汰，会把最该复习的先删掉，正好反了。（第一版就是这么写的，被测试打回来的。）
    /// </summary>
    public static double Value(MemoryItem item, long nowMs)
    {
        double availability = AvailabilityOf(item, nowMs);
        // 被检索过这件事本身有微弱加成：A 已经把时间算进去了，这里只补一点点"用过的更可靠"
        double used = 1.0 + 0.05 * Math.Min(6, Math.Max(0, item.Hits));
        return Math.Max(0.05, item.Importance) * availability * used;
    }

    /// <summary>
    /// 淘汰顺序：价值最低的先走。钉住的、永久层的不参与（调用方过滤）。
    /// 与旧规则相比多两件事：常被检索的能顶住，刚压出来的不会因为"重要度相同"被先删。
    /// </summary>
    public static List<MemoryItem> EvictionOrder(IEnumerable<MemoryItem> live)
    {
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        return live
            .Select(item => (Item: item, Value: Value(item, now)))
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Item.StartTs)
            .ThenBy(pair => pair.Item.Id, StringComparer.Ordinal)
            .Select(pair => pair.Item)
            .ToList();
    }
}
