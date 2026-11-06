using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Suran.Memory;

/// <summary>
/// 纯文本处理：脱敏、标识符抽取、中文时间词解析、稠密窗口。
///
/// 这些都是压缩与检索都要用、但谁都不该私有的东西，所以单独一处。
/// 全部是纯函数，可单测——这是刻意的：时间解析错一天、脱敏漏一个 token，
/// 都是那种"跑起来不报错、用起来才发现"的错。
/// </summary>
public static class MemoryText
{
    // ── 脱敏 ────────────────────────────────────────────────────

    static readonly Regex SecretBlock = new(@"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----", RegexOptions.Compiled);
    static readonly Regex SecretToken = new(@"\b(?:sk-|gh[pousr]_|xox[baprs]-|AKIA|glpat-)[A-Za-z0-9_\-]{12,}", RegexOptions.Compiled);
    static readonly Regex SecretAssign = new(@"((?:api[_-]?key|access[_-]?token|refresh[_-]?token|secret|password|passwd|sessdata|authorization|cookie|token)\s*[=:]\s*)(""[^""\r\n]*""|[^\s,;""'\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SecretBearer = new(@"(\bBearer\s+)[A-Za-z0-9._~+\-/]+=*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex SecretUrl = new(@"(https?://)[^\s/@:]+:[^\s/@]+@", RegexOptions.Compiled);
    static readonly Regex SecretCn = new(@"((?:会话令牌|访问令牌|会话密钥|密钥|密码|口令)\s*[:：=]\s*)[^\s，。；\r\n]+", RegexOptions.Compiled);

    /// <summary>
    /// 把凭证形状的串换成占位符。是黑名单不是保证：调用方仍不该把密钥写进对话，
    /// 但用户在聊天里贴 token 是常态（贴完还会说"帮我推上去"），落盘前过一道能少一次事故。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";
        string s = SecretBlock.Replace(text, "[密钥已隐去]");
        s = SecretToken.Replace(s, "[令牌已隐去]");
        s = SecretBearer.Replace(s, "$1[令牌已隐去]");
        s = SecretAssign.Replace(s, match => match.Groups[1].Value + "[已隐去]");
        s = SecretUrl.Replace(s, "$1[已隐去]@");
        s = SecretCn.Replace(s, "$1[已隐去]");
        return s;
    }

    // ── 标识符 ──────────────────────────────────────────────────

    /// <summary>
    /// 路径、命令行开关、版本号、全大写错误码。
    /// 这些东西的共同点是**猜不出来**：摘要里丢了 D:/Alife/Storage/Plugins，模型再怎么想也想不回来，
    /// 而"海边的约定"这种词丢了还能靠语义重新逼近。所以它们要单独成词条、单独统计覆盖。
    /// </summary>
    static readonly Regex IdentifierPattern = new(
        @"[A-Za-z]:\\[^\s""'`,;)\]]+"
        + @"|(?<![\w/])/[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+"
        + @"|(?<![\w/])[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)+\.[A-Za-z0-9]{1,8}\b"
        + @"|--[a-z0-9][a-z0-9\-]*"
        + @"|\bv?\d+\.\d+\.\d+(?:\.\d+)*\b"
        + @"|\b[A-Z][A-Z0-9_]{2,}(?:[-_]\d{2,})?\b",
        RegexOptions.Compiled);

    public static List<string> Identifiers(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<string>();
        List<string> list = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Match match in IdentifierPattern.Matches(text))
        {
            string value = match.Value.Trim();
            if (value.Length < 2)
                continue;
            string key = value.Replace('\\', '/').ToLowerInvariant();
            if (seen.Add(key))
                list.Add(value);
        }
        return list;
    }

    // ── 时间词 ──────────────────────────────────────────────────

    /// <summary>闭开区间 [From, To)。Label 回给模型看，让它确认"我理解的时间"和你说的是一回事。</summary>
    public readonly record struct TimeQuery(long From, long To, string Label)
    {
        public bool Overlaps(long start, long end)
        {
            return end >= From && start < To;
        }
    }

    static readonly Regex NumDayAgo = new(@"(\d{1,3})\s*(?:天|日)\s*(?:前|以前)", RegexOptions.Compiled);
    static readonly Regex CnDayAgo = new(@"([零一二两三四五六七八九十百]{1,4})(?:天|日)(?:前|以前)", RegexOptions.Compiled);
    static readonly Regex NumWeekAgo = new(@"(\d{1,2})\s*(?:周|星期|礼拜)\s*(?:前|以前)", RegexOptions.Compiled);
    static readonly Regex CnWeekAgo = new(@"([零一二两三四五六七八九十]{1,3})(?:周|星期|礼拜)(?:前|以前)", RegexOptions.Compiled);
    static readonly Regex NumMonthAgo = new(@"(\d{1,2})\s*个?\s*月\s*(?:前|以前)", RegexOptions.Compiled);
    static readonly Regex CnMonthAgo = new(@"([零一二两三四五六七八九十]{1,3})个?月(?:前|以前)", RegexOptions.Compiled);
    static readonly Regex NumRecent = new(@"(?:最近|近|过去|前)\s*(\d{1,3})\s*(?:天|日)", RegexOptions.Compiled);
    static readonly Regex CnRecent = new(@"(?:最近|近|过去)([零一二两三四五六七八九十]{1,3})(?:天|日)", RegexOptions.Compiled);
    static readonly Regex ExplicitDate = new(@"(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]?", RegexOptions.Compiled);
    static readonly Regex Weekday = new(@"(?:(上上个?|上个?|这|本)?\s*(?:周|星期|礼拜))\s*([一二三四五六日天1-7])", RegexOptions.Compiled);

    /// <summary>
    /// 把一句中文时间说法解析成绝对区间。认不出来就返回 null——**不要猜**。
    /// 猜错的时间比不查更糟：模型会拿"上周"的结果去回答"上个月"的问题，而且它自己不知道错了。
    /// </summary>
    public static TimeQuery? ParseWhen(string? raw, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string s = Normalize(raw);
        if (s.Length == 0)
            return null;

        // 时段：先记下来，最后跟日期求交
        (int From, int To)? slot = Slot(s);

        DateTime today = now.Date;

        // 1) N 天/周/月前（阿拉伯数字与中文数字两种写法都认："3 天前" / "三天前"）
        Match m = NumDayAgo.Match(s);
        int? daysAgo = m.Success && int.TryParse(m.Groups[1].Value, out int parsedDays) ? parsedDays : null;
        if (daysAgo == null)
        {
            m = CnDayAgo.Match(s);
            if (m.Success)
                daysAgo = CnNumber(m.Groups[1].Value);
        }
        if (daysAgo is > 0 and <= 3650)
            return Build(today.AddDays(-daysAgo.Value), today.AddDays(-daysAgo.Value + 1), slot, now, daysAgo.Value + " 天前");

        m = NumWeekAgo.Match(s);
        int? weeksAgo = m.Success && int.TryParse(m.Groups[1].Value, out int parsedWeeks) ? parsedWeeks : null;
        if (weeksAgo == null)
        {
            m = CnWeekAgo.Match(s);
            if (m.Success)
                weeksAgo = CnNumber(m.Groups[1].Value);
        }
        if (weeksAgo is > 0 and <= 520)
        {
            DateTime weekAgo = today.AddDays(-7 * weeksAgo.Value);
            return Build(weekAgo, weekAgo.AddDays(1), slot, now, weeksAgo.Value + " 周前");
        }

        m = NumMonthAgo.Match(s);
        int? monthsAgo = m.Success && int.TryParse(m.Groups[1].Value, out int parsedMonths) ? parsedMonths : null;
        if (monthsAgo == null)
        {
            m = CnMonthAgo.Match(s);
            if (m.Success)
                monthsAgo = CnNumber(m.Groups[1].Value);
        }
        if (monthsAgo is > 0 and <= 120)
        {
            DateTime monthBack = new DateTime(today.Year, today.Month, 1).AddMonths(-monthsAgo.Value);
            return Build(monthBack, monthBack.AddMonths(1), slot, now, monthsAgo.Value + " 个月前");
        }

        // 2) 最近 N 天（区间，不是某一天）
        m = NumRecent.Match(s);
        int? recent = m.Success && int.TryParse(m.Groups[1].Value, out int parsedRecent) ? parsedRecent : null;
        if (recent == null)
        {
            m = CnRecent.Match(s);
            if (m.Success)
                recent = CnNumber(m.Groups[1].Value);
        }
        if (recent is > 0 and <= 3650)
            return Range(today.AddDays(-recent.Value), now, "最近 " + recent.Value + " 天");

        // 3) X 月 X 日
        m = ExplicitDate.Match(s);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out int monthNum) && monthNum is >= 1 and <= 12
            && int.TryParse(m.Groups[2].Value, out int dayNum) && dayNum >= 1 && dayNum <= DateTime.DaysInMonth(now.Year, monthNum))
        {
            DateTime at = new(now.Year, monthNum, dayNum);
            if (at > today.AddDays(1))
                at = at.AddYears(-1);   // "11 月 1 日"说过头了，指去年的
            return Build(at, at.AddDays(1), slot, now, monthNum + " 月 " + dayNum + " 日");
        }

        // 4) 周几（可带 上/上上/这/本 前缀）
        m = Weekday.Match(s);
        if (m.Success)
        {
            int weekday = WeekdayIndex(m.Groups[2].Value);
            if (weekday >= 0)
            {
                DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                string prefix = m.Groups[1].Value;
                DateTime targetDay;
                string label;
                if (prefix is "上上" or "上上个")
                {
                    targetDay = monday.AddDays(-14 + (weekday == 0 ? 6 : weekday - 1));
                    label = "上上周" + m.Groups[2].Value;
                }
                else if (prefix is "上" or "上个")
                {
                    targetDay = monday.AddDays(-7 + (weekday == 0 ? 6 : weekday - 1));
                    label = "上周" + m.Groups[2].Value;
                }
                else if (prefix is "这" or "本")
                {
                    targetDay = monday.AddDays(weekday == 0 ? 6 : weekday - 1);
                    label = "本周" + m.Groups[2].Value;
                }
                else
                {
                    // 光说"周三"：取最近的那个（含今天），不说清就按最近理解
                    int offset = weekday == 0 ? 6 : weekday - 1;
                    targetDay = monday.AddDays(offset);
                    if (targetDay > today)
                        targetDay = targetDay.AddDays(-7);
                    label = "周" + m.Groups[2].Value;
                }
                return Build(targetDay, targetDay.AddDays(1), slot, now, label);
            }
        }

        // 5) 整周
        if (Has(s, "上上周", "上上个星期", "上上个礼拜"))
            return Week(today, -2, now);
        if (Has(s, "上周", "上星期", "上个星期", "上个礼拜", "上礼拜"))
            return Week(today, -1, now);
        if (Has(s, "这周", "本周", "这星期", "这个星期", "本星期", "这个礼拜"))
            return Week(today, 0, now);

        // 6) 整月 / 整年
        if (Has(s, "上个月", "上月", "上一个月"))
        {
            DateTime lastMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
            return Build(lastMonth, lastMonth.AddMonths(1), slot, now, "上个月");
        }
        if (Has(s, "这个月", "本月", "这月"))
        {
            DateTime thisMonth = new DateTime(today.Year, today.Month, 1);
            return Build(thisMonth, thisMonth.AddMonths(1), slot, now, "本月");
        }
        if (Has(s, "去年", "上年"))
        {
            DateTime year = new(today.Year - 1, 1, 1);
            return Range(year, year.AddYears(1), "去年");
        }
        if (Has(s, "今年", "本年"))
        {
            DateTime year = new(today.Year, 1, 1);
            return Range(year, year.AddYears(1), "今年");
        }

        // 7) 相对天
        if (Has(s, "前天", "前日"))
            return Build(today.AddDays(-2), today.AddDays(-1), slot, now, "前天");
        if (Has(s, "昨天", "昨日", "昨儿", "昨晚", "昨夜", "昨天晚上"))
        {
            if (slot == null && (Has(s, "昨晚", "昨夜", "昨天晚上")))
                slot = (18, 24);
            return Build(today.AddDays(-1), today, slot, now, "昨天");
        }
        if (Has(s, "今天", "今日", "今儿", "今晚", "今晚儿", "今早", "今天早上", "今天上午", "今天下午", "今天中午", "今天晚上"))
        {
            if (slot == null && (Has(s, "今晚", "今天晚上")))
                slot = (18, 24);
            return Build(today, today.AddDays(1), slot, now, "今天");
        }
        if (Has(s, "明天", "明日", "明儿"))
            return Build(today.AddDays(1), today.AddDays(2), slot, now, "明天");

        // 8) 模糊区间
        if (Has(s, "前几天", "这两天", "这几日", "前些天", "前些日子"))
            return Range(today.AddDays(-7), today.AddDays(-1), "前几天");
        if (Has(s, "前一阵", "前几天", "前段时间", "那阵子", "那段时间", "之前那段"))
            return Range(today.AddDays(-14), today.AddDays(-1), "前一阵");
        if (Has(s, "以前", "很久以前", "很久之前", "早些时候"))
            return Range(DateTime.UnixEpoch.ToLocalTime(), today.AddDays(-30), "很久以前");
        if (Has(s, "刚才", "刚刚", "方才"))
            return Range(now.AddHours(-2), now, "刚才");

        // 9) 只有时段，没有日期 → 今天
        if (slot != null)
            return Build(today, today.AddDays(1), slot, now, "今天");

        return null;
    }

    /// <summary>
    /// "三天" "二十天" "一百天" 这类中文数量。
    /// 逐位累加是不行的——"十五" 的 "十" 不是数字，是量级位，得按 十/百 分段算。
    /// </summary>
    public static int? CnNumber(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        int section = 0;
        int digit = 0;
        bool any = false;
        foreach (char ch in raw)
        {
            if (ch is >= '0' and <= '9')
            {
                digit = digit * 10 + (ch - '0');
                any = true;
                continue;
            }

            int value = ch switch {
                '零' => 0, '一' => 1, '二' => 2, '两' => 2, '三' => 3, '四' => 4,
                '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, _ => -1
            };
            if (value >= 0)
            {
                digit = value;
                any = true;
                continue;
            }

            if (ch == '十')
            {
                section += (digit == 0 ? 1 : digit) * 10;
                digit = 0;
                any = true;
            }
            else if (ch == '百')
            {
                section += (digit == 0 ? 1 : digit) * 100;
                digit = 0;
                any = true;
            }
            else
            {
                return null;
            }
        }

        int total = section + digit;
        return any && total > 0 ? total : null;
    }

    static int WeekdayIndex(string raw)
    {
        return raw switch {
            "一" or "1" => 1,
            "二" or "2" => 2,
            "三" or "3" => 3,
            "四" or "4" => 4,
            "五" or "5" => 5,
            "六" or "6" => 6,
            "日" or "天" or "7" => 0,
            _ => -1
        };
    }

    static TimeQuery Week(DateTime today, int offsetWeeks, DateTime now)
    {
        DateTime monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) + offsetWeeks * 7);
        string label = offsetWeeks switch { -2 => "上上周", -1 => "上周", 0 => "本周", _ => "那一周" };
        return Range(monday, monday.AddDays(7), label);
    }

    static TimeQuery Range(DateTime from, DateTime toExclusive, string label)
    {
        if (toExclusive <= from)
            toExclusive = from.AddDays(1);
        return new TimeQuery(
            new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Local)).ToUnixTimeMilliseconds(),
            new DateTimeOffset(DateTime.SpecifyKind(toExclusive, DateTimeKind.Local)).ToUnixTimeMilliseconds(),
            label);
    }

    /// <summary>某一天（或一段）叠加时段。时段落在日期之外就退回整天，不返回空区间。</summary>
    static TimeQuery Build(DateTime from, DateTime toExclusive, (int From, int To)? slot, DateTime now, string label)
    {
        if (slot == null || toExclusive.Subtract(from) > TimeSpan.FromDays(1.5))
            return Range(from, toExclusive, label);

        DateTime slotFrom = from.AddHours(slot.Value.From);
        DateTime slotTo = slot.Value.To >= 24
            ? from.AddDays(1)
            : from.AddHours(slot.Value.To);
        if (slotFrom < from)
            slotFrom = from;
        if (slotTo > toExclusive)
            slotTo = toExclusive;
        if (slotTo <= slotFrom)
            return Range(from, toExclusive, label);

        return Range(slotFrom, slotTo, label + " " + slot.Value.From.ToString(CultureInfo.InvariantCulture)
            + ":00-" + slot.Value.To.ToString(CultureInfo.InvariantCulture) + ":00");
    }

    static (int, int)? Slot(string s)
    {
        if (Has(s, "凌晨")) return (0, 6);
        if (Has(s, "清晨", "早上", "早晨", "一早", "今早", "上午")) return (5, 12);
        if (Has(s, "中午", "正午")) return (11, 14);
        if (Has(s, "下午")) return (13, 18);
        if (Has(s, "傍晚", "黄昏")) return (17, 19);
        if (Has(s, "深夜", "半夜")) return (22, 24);
        if (Has(s, "晚上", "晚间", "夜里", "今晚", "昨晚")) return (18, 24);
        return null;
    }

    static bool Has(string s, params string[] needles)
    {
        foreach (string needle in needles)
            if (s.Contains(needle, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>全角转半角、去空白与"的那这"这类不影响语义的衬字，统一小写英文</summary>
    static string Normalize(string raw)
    {
        StringBuilder sb = new(raw.Length);
        foreach (char ch in raw.Trim())
        {
            if (ch == ' ' || ch == '\t' || ch == '\u3000')
                continue;
            sb.Append(ch >= '\uFF01' && ch <= '\uFF5E' ? (char)(ch - 0xFEE0) : ch);
        }
        return sb.ToString().ToLowerInvariant();
    }

    // ── 稠密窗口 ────────────────────────────────────────────────

    /// <summary>
    /// 从长文本里截出信息密度最高的一段，而不是开头。
    /// 一条 20000 字的原文，前 400 字通常是寒暄；路径、错误码、结论都在后面。
    /// 命中字面时以命中处为中心，否则按标识符密度滑动打分。
    /// </summary>
    public static string DensestWindow(string? text, int limit, IReadOnlyList<string>? needles = null)
    {
        string s = text ?? "";
        if (limit <= 0 || s.Length <= limit)
            return s;

        if (needles != null)
        {
            foreach (string needle in needles.OrderByDescending(value => value.Length))
            {
                if (needle.Length < 2)
                    continue;
                int at = s.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    continue;
                int start = Math.Max(0, Math.Min(s.Length - limit, at - limit / 3));
                return Slice(s, start, limit);
            }
        }

        int stride = Math.Max(32, limit / 4);
        int bestStart = 0;
        int bestScore = -1;
        for (int start = 0; start + limit <= s.Length; start += stride)
        {
            int score = IdentifierPattern.Matches(s.Substring(start, limit)).Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = start;
            }
        }
        if (s.Length > limit)
        {
            int tail = s.Length - limit;
            int score = IdentifierPattern.Matches(s.Substring(tail, limit)).Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = tail;
            }
        }
        return bestScore <= 0 ? Slice(s, 0, limit) : Slice(s, bestStart, limit);
    }

    static string Slice(string s, int start, int limit)
    {
        if (start > 0 && start < s.Length && char.IsLowSurrogate(s[start]))
            start--;                                   // 别从半个代理对开始
        int take = Math.Min(limit, s.Length - start);
        string piece = s.Substring(start, take);
        if (piece.Length > 0 && char.IsHighSurrogate(piece[^1]))
            piece = piece[..^1];                       // 也别切在半个代理对上结束
        return (start > 0 ? "…" : "") + piece + (start + take < s.Length ? "…" : "");
    }
}
