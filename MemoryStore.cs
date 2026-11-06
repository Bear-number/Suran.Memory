using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Suran.Memory;

/// <summary>
/// 记忆库的落盘层。所有写操作走「临时文件 + 改名」，崩在写入中间不会留半截 JSON。
/// 目录布局（都在角色 Storage\&lt;key&gt;\Memory 下）：
///   tiers/items.json              条目索引
///   tiers/archive/L{n}/&lt;id&gt;.txt   原文
///   tiers/ledger.jsonl            遗忘/淘汰台账（先记台账再动数据）
///   L{n}/*.txt、History.json      旧插件的存档，只读不写
/// </summary>
public class MemoryStore
{
    public string Root { get; }
    public string TierDir { get; }
    public string ItemsPath { get; }
    public string ArchiveDir { get; }
    public string LedgerPath { get; }
    public string HistoryPath { get; }

    /// <summary>
    /// 不转义中文。System.Text.Json 默认把非 ASCII 写成 \uXXXX —— 机器读得懂，
    /// 但人打开文件看到的是一屏转义码，没法直接改、也没法跟官方 History.json 对读。
    /// </summary>
    static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>台账是 jsonl，一行一条，不能缩进</summary>
    static readonly JsonSerializerOptions JsonLine = new() {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public MemoryStore(string root)
    {
        Root = root;
        TierDir = Path.Combine(root, "tiers");
        ItemsPath = Path.Combine(TierDir, "items.json");
        ArchiveDir = Path.Combine(TierDir, "archive");
        LedgerPath = Path.Combine(TierDir, "ledger.jsonl");
        HistoryPath = Path.Combine(root, "History.json");
        Directory.CreateDirectory(TierDir);
        Directory.CreateDirectory(ArchiveDir);
    }

    public List<MemoryItem> Load()
    {
        if (!File.Exists(ItemsPath))
            return new List<MemoryItem>();
        try
        {
            List<MemoryItem>? items = JsonSerializer.Deserialize<List<MemoryItem>>(File.ReadAllText(ItemsPath), Json);
            return items ?? new List<MemoryItem>();
        }
        catch (Exception)
        {
            // 索引坏了不能让记忆整个瘫痪：备份坏文件后从空库继续，原文都还在 archive 里
            string broken = ItemsPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try { File.Move(ItemsPath, broken); } catch { /* 备份不了就留着，下面按空库走 */ }
            return new List<MemoryItem>();
        }
    }

    public void Save(IEnumerable<MemoryItem> items)
    {
        List<MemoryItem> ordered = items.OrderBy(item => item.StartTs).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        AtomicWrite(ItemsPath, JsonSerializer.Serialize(ordered, Json));
    }

    /// <summary>
    /// 原文就放在官方那套目录里：`Memory/L{等级}/{等级}-{起}-{止}.txt`。
    /// 以前新存档写在 tiers/archive 下、旧存档在 L{n} 下，同一个库两套位置，人和插件都会找错地方。
    /// ArchiveRel 为空表示「这条没有原文」（摘要还在，原文被删过），返回空串由调用方当缺失处理。
    /// </summary>
    public string ArchivePath(MemoryItem item)
    {
        return item.ArchiveRel.Length == 0 ? "" : Path.Combine(Root, item.ArchiveRel);
    }

    /// <summary>
    /// 把旧布局（tiers/archive/…、legacy/ 前缀）的原文文件挪到官方位置。
    /// 幂等：目标已存在就跳过；只搬不删库，失败也不影响条目可用。
    /// </summary>
    public int MigrateArchiveLayout(List<MemoryItem> items)
    {
        int changed = 0;
        foreach (MemoryItem item in items)
        {
            string before = item.ArchiveRel;
            string rel = item.ArchiveRel;
            if (rel.StartsWith("legacy/", StringComparison.OrdinalIgnoreCase))
                rel = rel["legacy/".Length..];

            if (rel.Length == 0)
            {
                // 只有真有文件时才补路径——旧存档的索引是 LegacyKey，用 Id 推出来的路径是假的
                string derived = "L" + item.Level.ToString(CultureInfo.InvariantCulture) + "/" + item.Id + ".txt";
                rel = File.Exists(Path.Combine(Root, derived)) ? derived : "";
            }

            if (!string.Equals(before, rel, StringComparison.Ordinal))
            {
                item.ArchiveRel = rel;
                changed++;
            }
            if (rel.Length == 0)
                continue;

            string target = Path.Combine(Root, rel);
            if (File.Exists(target))
                continue;

            string old = Path.Combine(ArchiveDir, rel);
            if (!File.Exists(old))
                continue;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(old, target);
                changed++;
            }
            catch (Exception)
            {
                // 搬不动就留着旧路径，条目照样能读
            }
        }
        return changed;
    }

    public void WriteArchive(MemoryItem item, string content)
    {
        if (item.ArchiveRel.Length == 0)
            item.ArchiveRel = "L" + item.Level.ToString(CultureInfo.InvariantCulture) + "/" + item.Id + ".txt";
        string path = Path.Combine(Root, item.ArchiveRel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, content);
    }

    /// <summary>
    /// 删除条目时把原文挪进 tiers/deleted/，不直接删文件——设置页点错一下不至于把原文弄没了。
    /// 挪走之后 L{n}/ 里没有它，下次启动的旧存档导入也不会把它捞回来。
    /// 返回新相对路径，没有文件就返回空串。
    /// </summary>
    public string ParkArchive(MemoryItem item)
    {
        string path = ArchivePath(item);
        if (path.Length == 0 || !File.Exists(path))
            return "";

        string dir = Path.Combine(TierDir, "deleted");
        string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Path.GetFileName(path);
        try
        {
            Directory.CreateDirectory(dir);
            File.Move(path, Path.Combine(dir, name));
            item.ArchiveRel = "";
            return "tiers/deleted/" + name;
        }
        catch (Exception)
        {
            return "";
        }
    }

    public string? ReadArchive(MemoryItem item, int maxChars)
    {
        string path = ArchivePath(item);
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        string text = File.ReadAllText(path);
        return maxChars > 0 && text.Length > maxChars ? text[..maxChars] + "\n…（已截断，原文更长）" : text;
    }

    /// <summary>历史里的一条消息。格式和官方 History.json 一致，互相能读</summary>
    public sealed record HistoryEntry(string Role, string Content, int Level, long StartTs, long EndTs);

    /// <summary>
    /// 把当前历史整段落盘。Alife 自己不做会话持久化——官方就是用这个文件在重启后恢复对话的，
    /// 不写它，重启后还没压的原文（L0）会全部消失。
    /// </summary>
    public void SaveHistory(IEnumerable<HistoryEntry> entries)
    {
        JsonArray array = new();
        foreach (HistoryEntry entry in entries)
        {
            array.Add(new JsonObject {
                ["Role"] = new JsonObject { ["Label"] = entry.Role },
                ["Content"] = entry.Content,
                ["MemoryMeta"] = new JsonObject {
                    ["Level"] = entry.Level,
                    ["StartTime"] = Iso(entry.StartTs),
                    ["EndTime"] = Iso(entry.EndTs)
                }
            });
        }
        AtomicWrite(HistoryPath, array.ToJsonString(Json));
    }

    public List<HistoryEntry> LoadHistory()
    {
        List<HistoryEntry> list = new();
        if (!File.Exists(HistoryPath))
            return list;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(HistoryPath)) is not JsonArray array)
                return list;

            foreach (JsonNode? record in array)
            {
                string content = record?["Content"]?.GetValue<string>() ?? "";
                if (content.Length == 0)
                    continue;
                string role = record?["Role"]?["Label"]?.GetValue<string>() ?? "assistant";
                int level = 0;
                long start = 0;
                long end = 0;
                try
                {
                    level = record?["MemoryMeta"]?["Level"]?.GetValue<int>() ?? 0;
                    start = ParseIso(record?["MemoryMeta"]?["StartTime"]);
                    end = ParseIso(record?["MemoryMeta"]?["EndTime"]);
                }
                catch (Exception) { /* 元数据坏了不影响正文 */ }

                list.Add(new HistoryEntry(role, content, level, start, end > 0 ? end : start));
            }
        }
        catch (Exception)
        {
            // 文件可能带尾逗号（官方某次写坏了），当空处理；存档本来就都在 L{n}/ 里
            return new List<HistoryEntry>();
        }
        return list;
    }

    static string Iso(long ms)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
    }

    static long ParseIso(JsonNode? node)
    {
        string text = node?.GetValue<string>() ?? "";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)
            ? at.ToUnixTimeMilliseconds()
            : 0;
    }

    public void Ledger(string action, MemoryItem item, string reason)
    {
        try
        {
            string line = JsonSerializer.Serialize(new {
                at = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                action,
                id = item.Id,
                level = item.Level,
                title = item.Title,
                reason
            }, JsonLine);
            File.AppendAllText(LedgerPath, line + "\n", Encoding.UTF8);
        }
        catch (Exception)
        {
            // 台账写不进去不该挡主流程，但这一条会丢——保持其余行为不变
        }
    }

    /// <summary>
    /// 把旧持久记忆插件的存档导进来。三件事：
    ///   1. History.json 里的摘要优先（那是模型写的，比原文截断好）；
    ///   2. L2 以上的文件本身就是摘要串（`【assistant】：[记忆存档(...)]正文`），剥掉标记直接用；
    ///   3. 高层级的文件包含低层级摘要的原文，所以按「时间范围被包含」去重，避免同一段记忆存两遍。
    /// 幂等：ArchiveRel 已经存在的跳过。
    /// </summary>
    public int ImportLegacy(List<MemoryItem> items, int maxLevel)
    {
        Dictionary<string, (string Summary, long Start, long End, int Level)> summaries = ReadLegacySummaries();
        HashSet<string> known = items
            .Select(item => item.ArchiveRel)
            .Where(rel => rel.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownKeys = items
            .Select(item => item.LegacyKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<Candidate> candidates = new();
        foreach (string dir in SafeDirs(Root))
        {
            string name = Path.GetFileName(dir);
            if (name.Length < 2 || name[0] != 'L' || !int.TryParse(name[1..], out int dirLevel))
                continue;

            foreach (string file in SafeFiles(dir, "*.txt"))
            {
                string idxName = Path.GetFileNameWithoutExtension(file);
                LegacyIndex? index = LegacyIndex.Parse(idxName);
                if (index == null)
                    continue;

                string rel = name + "/" + Path.GetFileName(file);
                if (known.Contains(rel))
                    continue;

                string raw;
                try { raw = File.ReadAllText(file); }
                catch { continue; }

                (long spanStart, long spanEnd) = ParseSpan(index.Value.Name, file);
                candidates.Add(new Candidate {
                    Index = idxName,
                    Rel = rel,
                    Level = Math.Max(1, index.Value.Level),   // 等级是旧的优先级标记（100 = 人工钉死），不裁剪
                    Summary = "",
                    Raw = raw,
                    Start = spanStart,
                    End = spanEnd
                });
            }
        }

        // History.json 的摘要按索引名合并进来（它的时间与正文更可信），没有对应文件的单独成条
        foreach (KeyValuePair<string, (string Summary, long Start, long End, int Level)> pair in summaries)
        {
            Candidate? hit = candidates.FirstOrDefault(c => c.Index.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                hit.Summary = pair.Value.Summary;
                hit.Start = pair.Value.Start > 0 ? pair.Value.Start : hit.Start;
                hit.End = pair.Value.End > 0 ? pair.Value.End : hit.End;
                hit.Level = Math.Max(1, pair.Value.Level);
                continue;
            }
            if (known.Contains(pair.Key) || knownKeys.Contains(pair.Key))
                continue;
            candidates.Add(new Candidate {
                Index = pair.Key,
                Rel = "",
                Level = Math.Max(1, pair.Value.Level),
                Summary = pair.Value.Summary,
                Raw = "",
                Start = pair.Value.Start,
                End = pair.Value.End > 0 ? pair.Value.End : pair.Value.Start
            });
        }

        // 高层级包含低层级：先按等级从高到低，再按时间跨度从长到短，被包含的跳过
        // 各层都留：高层摘要确实包含低层摘要的原文，但把它们删掉就等于"压过两次的内容看不见了"，
        // 而用户要的正是"摘要全留"。重复的只靠索引名合并（History 与文件是同一条）。
        int added = 0;
        List<Candidate> kept = candidates;

        foreach (Candidate candidate in kept.OrderBy(c => c.Start))
        {
            string summary = candidate.Summary;
            if (summary.Length == 0)
            {
                summary = candidate.Level >= 2
                    ? ExtractSummaries(candidate.Raw)
                    : Trim(Flatten(candidate.Raw), 200);
            }
            if (summary.Length == 0)
                continue;

            long start = candidate.Start;
            long end = candidate.End;
            items.Add(new MemoryItem {
                Id = MemoryItem.NewId(DateTime.Now),
                Level = candidate.Level,
                Title = CleanTitle(FirstLine(summary)),
                Summary = summary,
                StartTs = start,
                EndTs = end > 0 ? end : start,
                Importance = 0.5,
                InContext = false,   // 旧存档默认不进上下文，靠 MemorySearch/Recall 找，避免一上来就把记忆区撑满
                ArchiveRel = candidate.Rel,
                LegacyKey = candidate.Index,
                CreatedAt = start,
                Source = "legacy"
            });
            added++;
        }

        return added;
    }

    /// <summary>
    /// 旧文件名是 `{等级}-{yyyyMMddHHmmss}-{yyyyMMddHHmmss}`，区间就是真实时间。
    /// 用 mtime 会让所有条目挤到同一天，检索的时间排序全错——这是实测出来的。
    /// </summary>
    static (long Start, long End) ParseSpan(string name, string file)
    {
        string[] parts = name.Split('-');
        if (parts.Length >= 2
            && DateTime.TryParseExact(parts[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start)
            && DateTime.TryParseExact(parts[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime end))
        {
            long s1 = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            long s2 = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Local)).ToUnixTimeMilliseconds();
            return (s1, Math.Max(s1, s2));
        }

        long fallback = File.GetLastWriteTimeUtc(file).Subtract(DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMillisecond;
        return (fallback, fallback);
    }

    /// <summary>L2 以上的旧文件是「【assistant】：[记忆存档(...)]正文」串，剥掉两层壳只留正文</summary>
    static string ExtractSummaries(string raw)
    {
        StringBuilder text = new();
        foreach (string line in raw.Replace("\r\n", "\n").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("【") || trimmed.StartsWith("[记忆存档(", StringComparison.Ordinal))
                continue;
            text.AppendLine(trimmed);
        }
        return Trim(text.ToString().Trim(), 2000);
    }

    sealed class Candidate
    {
        public string Index { get; set; } = "";
        public string Rel { get; set; } = "";
        public int Level { get; set; }
        public string Summary { get; set; } = "";
        public string Raw { get; set; } = "";
        public long Start { get; set; }
        public long End { get; set; }
    }

    Dictionary<string, (string, long, long, int)> ReadLegacySummaries()
    {
        Dictionary<string, (string, long, long, int)> map = new(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(Root, "History.json");
        if (!File.Exists(path))
            return map;

        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            if (root is not JsonArray records)
                return map;

            foreach (JsonNode? record in records)
            {
                string content = record?["Content"]?.GetValue<string>() ?? "";
                if (content.Length == 0 || !content.StartsWith("[记忆存档(", StringComparison.Ordinal))
                    continue;

                int close = content.IndexOf(')');
                if (close < 0)
                    continue;
                string idxName = content["[记忆存档(".Length..close];
                LegacyIndex? index = LegacyIndex.Parse(idxName);
                if (index == null)
                    continue;

                string body = content[(close + 1)..].TrimStart(']').Trim();
                JsonNode? meta = record?["MemoryMeta"];
                long start = ParseTime(meta?["StartTime"]?.GetValue<string>() ?? "");
                long end = ParseTime(meta?["EndTime"]?.GetValue<string>() ?? "");
                map[idxName] = (body, start, end, index.Value.Level);
            }
        }
        catch (Exception)
        {
            // History.json 格式变了就当没有摘要，退回用原文开头
        }

        return map;
    }

    static long ParseTime(string raw)
    {
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Local)).ToUnixTimeMilliseconds()
            : 0;
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    public static string FirstLine(string text)
    {
        int idx = text.IndexOfAny(new[] { '\r', '\n' });
        return idx < 0 ? text : text[..idx];
    }

    public static string Flatten(string text)
    {
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    public static string Trim(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>
    /// 标题过一遍清洗：方括号、冒号、换行一律去掉。
    ///
    /// 存档头是 `[记忆存档(索引)] L2 10-31 14:16 标题`，标题就在同一行。标题里带个换行，
    /// 那一行会断成两截，模型读到的结构就乱了；带方括号则容易和别的标记混在一起。
    /// 危害不如 QQ 那边大（索引是我们自己拼的、改不了），但这是免费的卫生。
    /// </summary>
    public static string CleanTitle(string? text, int max = 40)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            if (c is '\r' or '\n' or '[' or ']' or ':' or '：')
                continue;
            sb.Append(c);
        }

        string s = sb.ToString().Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    static void AtomicWrite(string path, string body)
    {
        string tmp = path + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        File.WriteAllText(tmp, body, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
