using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Suran.Memory;

/// <summary>
/// 一条归纳：从**多条存档**里提炼出来的规律或信念。
///
/// 为什么要有这一层：存档回答"发生过什么"，检索也只能找"某件事"。
/// 但它答不了"她一般怎么看这件事"——而后者才是人格。
/// 差别在于证据数：一条存档是一次经历，一条规律是至少三次同一类经历。
///
/// 两条硬规矩：
///   1. 必须有证据。没有 Evidence 的归纳就是模型编的鸡汤，直接丢掉。
///   2. 可以被推翻。RetiredBy 非空即失效——人是会变的，信念更该会。
/// </summary>
public sealed class MemoryPattern
{
    public string Id { get; set; } = "";
    /// <summary>
    /// 归在哪一章。八章是**固定的骨架**（我是谁 / 我是怎么说话的 / 对方是谁 /
    /// 我们怎么走到今天的 / 我们的约定 / 难忘的时刻 / 怎么跟对方相处 / 一起做的东西），
    /// 借自 dsh-xingxi 的记忆卡结构。
    ///
    /// 为什么要骨架而不是自由生长：主题自由生长三个月会碎成几十条同义条目，
    /// 每条只有一两条证据，等于没有归纳。而且散着的主题答不了"我是谁"——
    /// 那才是人格的底座。空 = 还没归章（老数据）。
    /// </summary>
    public string Chapter { get; set; } = "";

    /// <summary>主题，4~10 字。合并去重靠它，所以它是这条归纳的"身份"</summary>
    public string Theme { get; set; } = "";
    /// <summary>一句话：她因此信了什么，或这类事一般是怎么回事</summary>
    public string Statement { get; set; } = "";
    /// <summary>0=规律（事情一般怎么发生）1=信念（她因此信了什么）</summary>
    public int Kind { get; set; }
    /// <summary>模型自评把握 0~1。低于 0.4 的不收——宁可少一条，不要一条含糊的</summary>
    public double Confidence { get; set; } = 0.5;
    /// <summary>支持它的存档 Id。这是它和"随口感叹"的唯一区别</summary>
    public List<string> Evidence { get; set; } = new();
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    /// <summary>被哪一条取代（主题相同但结论变了）。非空即失效，但仍可查</summary>
    public string RetiredBy { get; set; } = "";

    public bool Live => RetiredBy.Length == 0;

    public string KindWord => Kind == 1 ? "信念" : "规律";
}

/// <summary>
/// 归纳的落盘层。和 items.json 平级，单独一个文件——
/// 归纳和存档的生命周期不一样：存档只增不减，归纳会被推翻、被合并。
/// 混在一个文件里，改归纳就得重写整个存档索引。
/// </summary>
public sealed class MemoryPatternStore(string root)
{
    static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FilePath { get; } = Path.Combine(root, "patterns.json");

    public List<MemoryPattern> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new List<MemoryPattern>();
            return JsonSerializer.Deserialize<List<MemoryPattern>>(File.ReadAllText(FilePath), Json)
                ?? new List<MemoryPattern>();
        }
        catch
        {
            // 坏文件当空库：归纳丢了只是"她暂时说不出所以然"，不该让记忆整体起不来
            return new List<MemoryPattern>();
        }
    }

    public void Save(List<MemoryPattern> patterns)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(patterns, Json));
            File.Move(temp, FilePath, true);
        }
        catch
        {
            // 写不进去就只用内存里的这份
        }
    }
}

/// <summary>
/// 解析归纳输出。和 MemoryDraftParser 一样不依赖宿主，可以脱离 Alife 单测。
///
/// 容错原则与压缩相反：压缩宁可救回一条半成品，归纳宁可**少收**——
/// 一条没有证据的"她相信……"比没有归纳更糟，它会污染人格。
/// </summary>
public static class MemoryPatternParser
{
    public static List<MemoryPattern> Parse(string raw, long now)
    {
        List<MemoryPattern> result = new();
        string text = (raw ?? "").Trim();
        if (text.Length == 0)
            return result;

        // 模型常套 ```json 围栏；也可能前后写两句废话
        int start = text.IndexOf('[');
        int end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            return result;
        text = text[start..(end + 1)];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch
        {
            return result;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return result;

            HashSet<string> themes = new(StringComparer.Ordinal);
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                string theme = Text(entry, "theme");
                string statement = Text(entry, "statement");
                if (theme.Length is < 2 or > 16 || statement.Length < 4)
                    continue;
                if (!themes.Add(theme))
                    continue;   // 同一次输出里主题重复：留第一条

                List<string> evidence = new();
                if (entry.TryGetProperty("evidence", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement id in list.EnumerateArray())
                    {
                        if (id.ValueKind == JsonValueKind.String)
                        {
                            string value = (id.GetString() ?? "").Trim();
                            if (value.Length > 0)
                                evidence.Add(value);
                        }
                    }
                }

                // 没有证据就不是归纳，是感想。直接丢
                if (evidence.Count < 2)
                    continue;

                double confidence = 0.5;
                if (entry.TryGetProperty("confidence", out JsonElement score) && score.ValueKind == JsonValueKind.Number)
                    confidence = Math.Clamp(score.GetDouble(), 0, 1);
                if (confidence < 0.4)
                    continue;

                string chapter = Text(entry, "chapter");

                int kind = 0;
                if (entry.TryGetProperty("kind", out JsonElement kindElement))
                {
                    if (kindElement.ValueKind == JsonValueKind.Number)
                        kind = kindElement.GetInt32() == 1 ? 1 : 0;
                    else if (kindElement.ValueKind == JsonValueKind.String
                             && (kindElement.GetString() ?? "").Contains("信", StringComparison.Ordinal))
                        kind = 1;
                }

                result.Add(new MemoryPattern {
                    Id = "p-" + now.ToString("yyyyMMddHHmmss") + "-" + result.Count.ToString("D2"),
                    Chapter = chapter,
                    Theme = theme,
                    Statement = statement.Length > 80 ? statement[..80] : statement,
                    Kind = kind,
                    Confidence = confidence,
                    Evidence = evidence.Distinct(StringComparer.Ordinal).ToList(),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }

        return result;
    }

    static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";
}
