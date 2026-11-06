using System;
using System.Collections.Generic;
using System.Linq;

namespace Suran.Memory;

/// <summary>一次检索命中。区分"命中摘要"和"命中原文"，因为两者的可信度不一样。</summary>
public sealed class MemoryHit
{
    public required MemoryItem Item { get; init; }
    public double Score { get; init; }
    public bool InSummary { get; init; }
    public bool InArchive { get; init; }
    /// <summary>命中原文时带出的证据片段；命中摘要时为空（摘要本来就会整条给出）</summary>
    public string Excerpt { get; init; } = "";

    public string Where => InSummary && InArchive ? "摘要+原文" : InArchive ? "原文" : "摘要";
}

/// <summary>
/// 内存倒排索引。中文用二元组、英文用词，全在内存里，几万条也是毫秒级。
/// 不接 embedding：宿主没有嵌入能力，拉外部 API 会让"写入"变成一次网络调用，
/// 记忆这种每轮都可能写的东西不该有这种依赖。
///
/// 原文（L{n}/*.txt）不进倒排，改成检索时按需扫描——理由很实际：
/// 倒排一条 6000 字的原文要留几千个引用，400 条就是几十 MB 常驻；
/// 而按需扫描是 400 个文件、一次几十毫秒，还带字符串缓存。
/// 工具调用不是每轮都发生，这笔交易划算。
/// </summary>
public class MemoryIndexer
{
    readonly Dictionary<string, HashSet<string>> inverted = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> archiveCache = new(StringComparer.Ordinal);

    /// <summary>扫原文时最多扫多少条（按结束时间取最新的），防止库变大后一次检索卡住</summary>
    public int ArchiveScanMax { get; set; } = 400;

    public int TokenCount => inverted.Count;
    public int CachedArchives => archiveCache.Count;

    public void Build(IEnumerable<MemoryItem> items)
    {
        inverted.Clear();
        foreach (MemoryItem item in items)
        {
            Add(item);
        }
    }

    public void Add(MemoryItem item)
    {
        // 标题权重靠重复计入实现：同一个 id 在标题里出现两次就算两票
        foreach (string token in MemoryTokenizer.Distinct(item.Title))
        {
            Bucket(token).Add(item.Id);
            Bucket(token).Add(item.Id);
        }
        foreach (string token in MemoryTokenizer.Distinct(item.Summary + " " + item.Facts))
        {
            Bucket(token).Add(item.Id);
        }
    }

    /// <summary>条目变了（改摘要/补锚点）时先摘掉旧词再重新加，否则旧词会一直命中它</summary>
    public void Reindex(MemoryItem item)
    {
        foreach (HashSet<string> bucket in inverted.Values)
            bucket.Remove(item.Id);
        Add(item);
    }

    public void Forget(string id)
    {
        foreach (HashSet<string> bucket in inverted.Values)
            bucket.Remove(id);
        archiveCache.Remove(id);
    }

    HashSet<string> Bucket(string token)
    {
        if (!inverted.TryGetValue(token, out HashSet<string>? set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            inverted[token] = set;
        }
        return set;
    }

    /// <summary>读原文并缓存。读不到返回 null（原文被删过的条目只有摘要）</summary>
    string? ArchiveOf(MemoryItem item, Func<MemoryItem, string?>? reader)
    {
        if (reader == null || item.ArchiveRel.Length == 0)
            return null;
        if (archiveCache.TryGetValue(item.Id, out string? cached))
            return cached;

        string? text = reader(item);
        if (text == null)
            return null;

        // 简单容量控制：超过 200 条就清空重来。这些文件都在系统文件缓存里，重建不贵，
        // 而做 LRU 链表换来的那点命中率不值得多一份要维护的状态。
        if (archiveCache.Count >= 200)
            archiveCache.Clear();
        archiveCache[item.Id] = text;
        return text;
    }

    /// <summary>
    /// 打分：IDF 加权的摘要命中 + 原文命中，再按时间新旧轻微加成。
    /// IDF 是必需的——"看海"这种词在五条记忆里都有，它不该和"冻雨"（只有一条有）等价。
    /// </summary>
    public List<MemoryHit> Search(
        IReadOnlyList<MemoryItem> items,
        string? query,
        int take,
        Func<MemoryItem, bool>? filter = null,
        MemoryText.TimeQuery? when = null,
        Func<MemoryItem, string?>? archiveReader = null,
        bool includeArchive = true,
        Func<MemoryItem, double>? bias = null)
    {
        take = Math.Clamp(take, 1, 50);
        List<string> tokens = MemoryTokenizer.Distinct(query ?? "");

        bool Pass(MemoryItem item)
        {
            if (filter != null && !filter(item))
                return false;
            return when == null || when.Value.Overlaps(item.StartTs, item.EndTs);
        }

        if (tokens.Count == 0)
        {
            // 有关键词但分不出词：几乎都是**单个汉字**（「猫」「钱」「书」这种）。
            // 分词只产二元组，单字一个词都产不出来；以前这里直接按时间倒序返回最近几条，
            // 看起来像搜到了，其实一条都没匹配——最坑的是它还很像"我确实查过"。
            // 单字没法走倒排，就老老实实扫一遍标题和摘要。
            if (!string.IsNullOrWhiteSpace(query))
                return LiteralScan(items, query.Trim(), take, Pass, archiveReader, includeArchive);

            return items.Where(Pass)
                .OrderByDescending(item => item.EndTs)
                .Take(take)
                .Select(item => new MemoryHit { Item = item, Score = 0, InSummary = false })
                .ToList();
        }

        Dictionary<string, MemoryItem> byId = new(StringComparer.Ordinal);
        foreach (MemoryItem item in items)
            byId[item.Id] = item;

        Dictionary<string, double> score = new(StringComparer.Ordinal);
        HashSet<string> inSummary = new(StringComparer.Ordinal);
        HashSet<string> inArchive = new(StringComparer.Ordinal);

        // 文档频率取自倒排桶大小；桶是"摘要+标题"的并集，足够近似
        double Idf(string token)
        {
            int df = inverted.TryGetValue(token, out HashSet<string>? bucket) ? bucket.Count : 1;
            return Math.Log2(1.0 + byId.Count / (1.0 + df));
        }

        foreach (string token in tokens)
        {
            if (!inverted.TryGetValue(token, out HashSet<string>? ids))
                continue;
            double weight = Idf(token);
            foreach (string id in ids)
            {
                score[id] = score.GetValueOrDefault(id) + weight;
                inSummary.Add(id);
            }
        }

        // 原文扫描：只解决一件事——关键词只在原文里出现、摘要没提到。
        // 这条路径的权重比摘要低（0.6）：摘要命中的是"这事本身"，原文命中的可能只是一句闲聊。
        if (includeArchive && archiveReader != null)
        {
            List<MemoryItem> scannable = items
                .Where(item => Pass(item) && item.ArchiveRel.Length > 0)
                .OrderByDescending(item => item.EndTs)
                .Take(Math.Max(1, ArchiveScanMax))
                .ToList();

            foreach (MemoryItem item in scannable)
            {
                string? text = ArchiveOf(item, archiveReader);
                if (string.IsNullOrEmpty(text))
                    continue;

                double gained = 0;
                foreach (string token in tokens)
                {
                    if (text!.Contains(token, StringComparison.OrdinalIgnoreCase))
                        gained += 0.6 * Idf(token);
                }
                if (gained <= 0)
                    continue;

                score[item.Id] = score.GetValueOrDefault(item.Id) + gained;
                inArchive.Add(item.Id);
            }
        }

        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        List<MemoryHit> hits = new();
        foreach (KeyValuePair<string, double> pair in score)
        {
            if (!byId.TryGetValue(pair.Key, out MemoryItem? item) || !Pass(item))
                continue;

            double ageDays = Math.Max(0, (now - item.EndTs) / 86400000.0);
            double recency = 1.0 / (1.0 + ageDays / 30.0);       // 一个月内基本不衰减
            double boost = item.IsPermanent ? 1.5 : 1.0;
            if (item.SupersededBy.Length > 0)
                boost *= 0.85;                                    // 过时的仍可查，但不该压过现行的

            string excerpt = "";
            if (!inSummary.Contains(item.Id) && inArchive.Contains(item.Id))
            {
                string? text = ArchiveOf(item, archiveReader);
                if (!string.IsNullOrEmpty(text))
                    excerpt = MemoryText.DensestWindow(text, 180, tokens);
            }

            // bias 只改顺序：字面分决定"能不能进候选"，情绪只决定"先想起哪条"
            double emotional = bias?.Invoke(item) ?? 1.0;
            if (!double.IsFinite(emotional) || emotional <= 0)
                emotional = 1.0;

            hits.Add(new MemoryHit {
                Item = item,
                Score = pair.Value * (1 + 0.3 * recency) * boost * emotional,
                InSummary = inSummary.Contains(item.Id),
                InArchive = inArchive.Contains(item.Id),
                Excerpt = excerpt
            });
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Item.EndTs)
            .Take(take)
            .ToList();
    }

    /// <summary>
    /// 字面扫描：给"分不出词"的短查询用（单字、符号）。
    ///
    /// 命中次数就是分数，不做 IDF——单个字本来就没有稀有度可言。
    /// 原文命中记 0.5 分（比摘要低，和二元组检索的 0.6 是一个意思：摘要命中的是"这事本身"）。
    /// </summary>
    List<MemoryHit> LiteralScan(IReadOnlyList<MemoryItem> items, string needle, int take, Func<MemoryItem, bool> pass,
        Func<MemoryItem, string?>? archiveReader, bool includeArchive)
    {
        List<MemoryHit> hits = new();
        foreach (MemoryItem item in items)
        {
            if (!pass(item))
                continue;

            string text = item.Title + "\n" + item.Summary;
            int count = CountOf(text, needle);
            bool inSummary = count > 0;
            double score = count;

            string excerpt = "";
            if (score <= 0 && includeArchive && archiveReader != null && item.ArchiveRel.Length > 0)
            {
                string? body = ArchiveOf(item, archiveReader);
                if (!string.IsNullOrEmpty(body))
                {
                    int hit = CountOf(body!, needle);
                    if (hit > 0)
                    {
                        score = hit * 0.5;
                        excerpt = MemoryText.DensestWindow(body!, 180, new List<string> { needle });
                    }
                }
            }

            if (score <= 0)
                continue;

            hits.Add(new MemoryHit {
                Item = item,
                Score = score,
                InSummary = inSummary,
                InArchive = !inSummary,
                Excerpt = excerpt,
            });
        }

        return hits
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Item.EndTs)
            .Take(take)
            .ToList();
    }

    static int CountOf(string text, string needle)
    {
        if (needle.Length == 0)
            return 0;
        int count = 0;
        int at = 0;
        while (true)
        {
            int found = text.IndexOf(needle, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return count;
            count++;
            at = found + needle.Length;
            if (count > 999)   // 防呆：真有上万次命中也没意义
                return count;
        }
    }
}
