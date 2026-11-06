using System;
using System.Text.Json;

namespace Suran.Memory;

/// <summary>
/// 从角色文件夹的 `index.json` 取人设文本，压缩记忆时用来对齐口吻。
/// 纯函数：只认 JSON 的 `Prompt` 字段，找不到语气小节就退回全文。
/// </summary>
public static class MemoryPersona
{
    public sealed record Result(string Text, string Mode);

    static readonly string[] StyleHeadings =
    [
        "## 性格与语言", "## 性格", "## 语言风格", "## 说话方式", "## 口癖", "## 风格",
        "## Personality", "## Speaking Style", "## Style",
    ];

    public static Result Extract(string json, bool styleOnly, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Result("", "没有 index.json");

        string prompt;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Result("", "index.json 不是对象");
            if (!document.RootElement.TryGetProperty("Prompt", out JsonElement element)
                || element.ValueKind != JsonValueKind.String)
                return new Result("", "index.json 没有 Prompt 字段");
            prompt = (element.GetString() ?? "").Trim();
        }
        catch (JsonException)
        {
            return new Result("", "index.json 解析失败");
        }

        if (prompt.Length == 0)
            return new Result("", "Prompt 是空的");

        string mode = "全文";
        if (styleOnly)
        {
            int start = -1;
            foreach (string heading in StyleHeadings)
            {
                int at = prompt.IndexOf(heading, StringComparison.Ordinal);
                if (at >= 0 && (start < 0 || at < start))
                    start = at;
            }

            if (start >= 0)
            {
                int next = prompt.IndexOf("\n## ", start + 3, StringComparison.Ordinal);
                prompt = (next > start ? prompt[start..next] : prompt[start..]).Trim();
                mode = "仅语气段";
            }
            else
            {
                mode = "全文（没找到语气小节）";
            }
        }

        if (maxChars > 0 && prompt.Length > maxChars)
        {
            prompt = prompt[..maxChars] + "…";
            mode += "·已截断";
        }
        return new Result(prompt, mode);
    }

    /// <summary>
    /// 拼进压缩提示词的那一段。人设为空时返回空串，调用方不要加。
    /// </summary>
    public static string Block(string persona)
    {
        string text = (persona ?? "").Trim();
        if (text.Length == 0)
            return "";
        return "\n\n你平时是这样说话的（只对齐口吻，不要复述这段设定；写的是你回想自己的经历，不是对话）：\n"
            + text + "\n";
    }
}
