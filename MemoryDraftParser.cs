using System;
using System.Globalization;
using System.Text;

namespace Suran.Memory;

/// <summary>压缩出来的一条记忆草稿</summary>
/// <summary>Supersede = 这条新记忆推翻了哪条旧存档（索引），没有就是 null</summary>
/// <summary>
/// FactKey = 「对象.方面」式的事实键（如 `他.身高`）。同一个键 = 同一件会变的事，
/// 新的进来时旧的自动作废——不靠模型记得去写「替代：某索引」。
/// 允许为空：拿不准就留空，给错键比留空糟得多。
/// </summary>
public record MemoryDraft(string Title, string Summary, double Importance,
    string? Supersede = null, string? FactKey = null);

/// <summary>
/// 解析模型输出。单独放一个文件是因为它不依赖宿主——可以脱离 Alife 直接单测。
/// 容错原则：模型不按格式写就退回「首行当标题、其余当正文」，绝不让这一批白压。
/// </summary>
public static class MemoryDraftParser
{
    public static MemoryDraft? Parse(string raw)
    {
        string text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstBreak = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak > 0 && lastFence > firstBreak)
            {
                text = text[(firstBreak + 1)..lastFence].Trim();
            }
        }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string title = "";
        double importance = 0.5;
        string? supersede = null;
        string? factKey = null;
        int bodyStart = 0;

        for (int i = 0; i < Math.Min(lines.Length, 6); i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                bodyStart = i + 1;
                continue;
            }

            if (title.Length == 0 && line.StartsWith("标题", StringComparison.Ordinal))
            {
                title = line.Split('：', ':').Length > 1 ? line[(line.IndexOfAny(new[] { '：', ':' }) + 1)..].Trim() : "";
                bodyStart = i + 1;
                continue;
            }

            if (line.StartsWith("重要度", StringComparison.Ordinal))
            {
                int cut = line.IndexOfAny(new[] { '：', ':' });
                string num = cut >= 0 ? line[(cut + 1)..] : line;
                StringBuilder digits = new();
                foreach (char ch in num)
                {
                    if (char.IsDigit(ch) || ch == '.')
                        digits.Append(ch);
                }
                if (double.TryParse(digits.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    importance = Math.Clamp(value, 0, 1);
                bodyStart = i + 1;
                continue;
            }

            if (supersede == null && (line.StartsWith("替代", StringComparison.Ordinal) || line.StartsWith("取代", StringComparison.Ordinal)))
            {
                int cut = line.IndexOfAny(new[] { '：', ':' });
                string value = cut >= 0 ? line[(cut + 1)..] : "";
                value = value.Trim().Trim('`', '"', '「', '」');
                if (value.Length > 0 && !value.Contains('无'))
                    supersede = value;
                bodyStart = i + 1;
                continue;
            }

            if (factKey == null && line.StartsWith("事实键", StringComparison.Ordinal))
            {
                int cut = line.IndexOfAny(new[] { '：', ':' });
                string value = cut >= 0 ? line[(cut + 1)..] : "";
                value = value.Trim().Trim('`', '"', '「', '」');
                if (value.Length > 0 && !value.Contains('无'))
                    factKey = value;
                bodyStart = i + 1;
                continue;
            }

            break;
        }

        string body = string.Join("\n", lines[bodyStart..]).Trim();
        if (body.Length == 0)
        {
            body = text;
            if (title.Length > 0 && body.StartsWith(title, StringComparison.Ordinal))
            {
                body = body[title.Length..].Trim();
            }
        }

        body = body.Replace("正文：", "", StringComparison.Ordinal).Replace("正文:", "", StringComparison.Ordinal).Trim();
        if (body.Length == 0)
            return null;

        if (title.Length == 0)
        {
            title = MemoryStore.Trim(MemoryStore.FirstLine(body).Replace("#", "").Trim(), 24);
        }

        return new MemoryDraft(MemoryStore.Trim(title, 40), body, importance, supersede, factKey);
    }
}
