using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.TokenStatistics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Suran.Memory;

/// <summary>
/// 分层记忆（官方「持久记忆」的增强版）。
///
/// 与原版保持一致的部分：
///   · 摘要在**原位置**替换被压缩的那一段，历史仍是一条连续的时间线；
///   · 标签格式 `[记忆存档(等级-起-止)]`，和原版一模一样；
///   · 概率触发、思考通道、token 告警、关键词提示、可指定等级；
///   · 原文落盘、按索引取回。
///
/// 增强的三件事：
///   1. 容量上限：存档总字数超上限就把最旧的移出上下文，原文仍在，可检索；
///   2. 关键词检索：中文二元组倒排，能搜已移出上下文的旧存档；
///   3. 钉住：MemoryRemember(pinned=true) 的条目永远不会被移出。
/// </summary>
[Module("记忆（分层归档）",
    "把旧对话压成摘要留在原位置，超出容量自动归档到外存；可按关键词检索全部历史、取回原文，也能主动记录重要的事。",
    defaultCategory: "苏染的工具",
    EditorUI = typeof(MemoryServiceUI),
    GlobalUI = typeof(MemoryNebulaUI),
    LaunchOrder = -10000)]   // 抢在其他功能之前就位，避免第一轮对话漏抓
public class MemoryService(
    ILogger<MemoryService> logger,
    XmlFunctionCaller functionService,
    Interactor<MemoryService> interactor,
    ITokenUser tokenUser,
    ILanguageModel languageModel,
    ModuleSystem moduleSystem) :
    ChatBehaviour,
    IConfigurable<MemoryConfig>
{
    public MemoryConfig Configuration { get; set; } = null!;

    /// <summary>给设置页显示用：库里共几条</summary>
    public int ItemCount => items.Count;

    /// <summary>给设置页显示用：还在上下文里有几条</summary>
    public int LiveCount => items.Count(item => item.InContext);

    /// <summary>给设置页显示用：有原文文件可取的几条</summary>
    public int ArchiveCount => items.Count(item => item.ArchiveRel.Length > 0);

    /// <summary>给设置页显示用：带原文锚点的几条（摘要没写、但靠它才能查回来的路径/版本/错误码）</summary>
    public int FactsCount => items.Count(item => item.Facts.Length > 0);

    /// <summary>给设置页显示用：L0（还没压的原文消息）条数。每次扫描时更新，不是实时</summary>
    public int RawCount => lastRawCount;

    const string TagStart = "[记忆存档(";
    static readonly Regex ArchiveTag = new(@"^\[记忆存档\(([^)]+)\)\]", RegexOptions.Compiled);
    // 早期版本写的头是 [记忆档案 L2 11-01 14:51 2-2026…] 标题，得认，否则那些消息会被当成原文重压
    static readonly Regex LegacyArchiveTag = new(@"^\[记忆档案 L\d+ [^\]]*?([0-9]+-[0-9]{14}-[0-9]{14})\]", RegexOptions.Compiled);
    // 时间戳有两种写法，都得认：
    //   [时间(2026-11-04 17:00:35)]      ← 宿主 MessageFilter 现在用的是这个
    //   当前时间: [2026-11-04 17:00:35]  ← 旧版与部分插件用的
    // 只认后者的话，前者一条都匹配不上，所有存档的起止都会退化成"压缩那一刻"。
    static readonly Regex TimeTag = new(
        @"(?:\[时间\(|当前时间\s*[:：]\s*\[)(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled);

    MemoryStore store = null!;
    MemoryIndexer indexer = new();
    List<MemoryItem> items = new();
    ILanguageModel compressModel = null!;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<ChatMessageContent, DateTime> stamps = new(ReferenceEqualityComparer.Instance);
    int lastRawCount;   // 最近一次扫描时 L0（还没压的原文）条数，给设置页显示
    readonly HashSet<string> givenToModel = new(StringComparer.OrdinalIgnoreCase);   // 本会话已给过的短码
    bool warnedNoTimestamp;
    HashSet<string>? cardTokens;   // 上下文里所有存档的词集合，用于判断"这句话跟记忆对得上吗"
    string cardStamp = "";
    long lastHintAt;
    DateTime nextRecallAt;         // 下一次「回忆」的时间点
    bool recallRunning;

    // 归纳层（规律/信念）。和存档分开放：存档只增不减，归纳会被推翻、被合并
    static WeakReference<MemoryService>? latestRef;

    MemoryPatternStore patternStore = null!;
    List<MemoryPattern> patterns = new();
    bool reflectRunning;
    int lastReflectItemCount;

    // ── 生命周期 ─────────────────────────────────────────────────

    protected override Task OnAwake()
    {
        string root = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Memory");
        Directory.CreateDirectory(root);
        store = new MemoryStore(root);
        items = store.Load();
        EnsureShortIds();

        patternStore = new MemoryPatternStore(root);
        patterns = patternStore.Load();
        latestRef = new WeakReference<MemoryService>(this);
        lastReflectItemCount = items.Count;
        if (patterns.Count > 0)
            logger.LogInformation("[Memory] 归纳库：{Count} 条规律/信念（现行 {Live} 条）",
                patterns.Count, patterns.Count(pattern => pattern.Live));

        // 旧布局的原文（tiers/archive/…）归位到官方目录 L{n}/，只搬一次
        int migrated = store.MigrateArchiveLayout(items);
        if (migrated > 0)
        {
            store.Save(items);
            logger.LogInformation("[Memory] 原文归位到官方目录 L{{等级}}/ 共 {Count} 个文件", migrated);
        }

        if (Configuration.ImportLegacy)
        {
            int added = store.ImportLegacy(items, Configuration.MaxLevel);
            if (added > 0)
                logger.LogInformation("[Memory] 导入旧存档 {Count} 条", added);
        }

        int mustPin = items.Count(item => item.Level >= 100 && !item.Pinned);
        if (mustPin > 0)
        {
            foreach (MemoryItem item in items.Where(item => item.Level >= 100 && !item.Pinned))
                item.Pinned = true;
            store.Save(items);
            logger.LogInformation("[Memory] {Count} 条 L100 记忆补成钉住（永久层）", mustPin);
        }

        // 上下文为空时补一次阶梯（只在一开局 / 全被清空时发生，平时不会重复放回）
        int seeded = SeedContextFromLegacy();
        if (seeded > 0)
        {
            store.Save(items);
            logger.LogInformation("[Memory] 把 {Count} 条高层存档放回上下文，阶梯起点：{Levels}", seeded,
                string.Join(" / ", Live().GroupBy(item => item.Level).OrderBy(group => group.Key)
                    .Select(group => "L" + group.Key + "×" + group.Count())));
        }

        indexer.Build(items);

        XmlHandler handler = new(this) {
            Description = "分层长期记忆：旧对话会在原位置被压成摘要存档，可按关键词或时间检索全部历史并取回原文。",
            Explanation = """
                          记忆的工作原理：对话里以 `[记忆存档(索引)]` 开头的消息就是压缩过的旧对话，索引形如 `3-20261031141657-20261101054025`。
                          对话变长后，最旧的一批会被压成一条存档，**留在原来的位置**（历史仍按时间连续），原文存到磁盘。
                          记忆总量有上限，超了就把最旧的存档移出上下文——它不会消失，用 MemorySearch 还能搜到、用 MemoryRecall 取原文。

                          等级怎么读：L1 是刚压过的近期对话；L2 以上是把更早的摘要再压一遍，**等级越高覆盖的时间段越长、越抽象，但也越可信**（反复提炼后剩下的骨架）。
                          L100 不是"压了一百轮"，是永久层：你和主人钉住、永不被淘汰的约定与原则，比任何摘要都更该遵守。

                          什么时候用：
                          · 想不起某件事、或用户提到很久以前的细节 → MemorySearch 搜关键词，再 MemoryRecall 取原文；
                          · 用户问"上周三""前几天""上个月"那会儿的事 → MemorySearch 不用自己想日期，直接传 when="上周三"；
                          · 没人问、但想翻翻旧事 → MemoryReminisce（它挑的是最久没被想起的，不是最相关的）；
                          · 有一件必须长期记住的事（约定、原则、人物设定）→ MemoryRemember 记下来，重要就 pinned=true，钉住的不会被移出；
                          · 想确认自己现在记着多少东西 → MemoryStatus。

                          存档末尾如果有一行 `[原文锚点]`，那是摘要里写不下、但猜不回来的路径/版本号/开关，需要复现时照它去查。
                          检索结果末尾会写清覆盖范围（多少条摘要 + 多少篇原文）；范围外的内容确实没有，不要编成有。
                          不要每轮都检索：上下文里已有的内容不要重复查，只有确实缺信息时才查。
                          标了〔摘要已在上下文〕的结果，那段摘要此刻就摆在你眼前——它是"你手上已有的"，不是"刚想起来的"，别当成新情报复述一遍。

                          存档和当前对话冲突时，一律以**此刻听到的**为准，并且直说「我记得的是 X，你现在说的是 Y」——
                          不要为了保持前后一致悄悄改口，也不要拿旧存档去纠正主人。记忆是参考，不是事实。
                          """
        };
        functionService.RegisterHandler(handler, DocumentMode.Explicit, DestroyCancellationToken);
        return Task.CompletedTask;
    }

    protected override async Task OnStart()
    {
        compressModel = await ResolveCompressModel();
        givenToModel.Clear();   // 每次启动当成新会话，别让上次的"已给过"挡住检索
        ChatBot.ChatHistoryAdd += OnChatHistoryAdd;
        ChatBot.ChatSend += OnChatSend;
        tokenUser.TokenUsed += OnTokenUsed;

        // 先恢复整段对话（含还没压的 L0 原文），再按库补回缺席的存档消息
        if (Configuration.PersistHistory)
        {
            List<MemoryStore.HistoryEntry> saved = store.LoadHistory();
            if (saved.Count > 0)
            {
                await EditHistoryAsync(thread => {
                    for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
                    {
                        if (thread.ChatHistory[i].Role != AuthorRole.System)
                            thread.ChatHistory.RemoveAt(i);
                    }
                    foreach (MemoryStore.HistoryEntry entry in saved)
                    {
                        ChatMessageContent restored = new(RoleOf(entry.Role), entry.Content);
                        thread.ChatHistory.Add(restored);
                        // 时间戳一起恢复，否则重启后所有原文都盖成"启动时刻"，区间检索全错
                        if (entry.StartTs > 0)
                            stamps[restored] = DateTimeOffset.FromUnixTimeMilliseconds(entry.StartTs).LocalDateTime;
                    }
                }, "恢复对话历史");
                logger.LogInformation("[Memory] 从 History.json 恢复对话 {Count} 条", saved.Count);
            }
        }

        // 宿主重启后上下文里可能没有存档消息（或与库不一致）：按库补回原位、顺手清掉孤儿
        int touched = 0;
        await EditHistoryAsync(thread => touched = RestoreArchives(thread), "恢复记忆存档");
        if (touched > 0)
            await SaveHistoryAsync("对齐记忆存档");   // 对齐结果落盘，别下次重启又回到不一致
        await EnforcePermanentBudgetAsync();
        nextRecallAt = ComputeNextRecall();
        logger.LogInformation("[Memory] 就绪：条目 {Count} 条，在上下文 {Live} 条", items.Count, CountLive());
    }

    /// <summary>
    /// 每秒检查一次「该回忆了吗」。
    ///
    /// 两个必须有的克制：
    ///   1. 正在生成回复时顺延 30 秒——回忆是插话，不能打断它自己正在说的话；
    ///   2. 间隔是 [最短, 最长] 之间随机——固定周期会让"忽然想起"变成准点报时。
    /// </summary>
    protected override Task OnUpdate()
    {
        if (!Configuration.RecallEnabled)
            return Task.CompletedTask;

        // 帧率归一化：不论 30 帧还是 144 帧，下面这段每秒只跑一次
        double delta = UpdateContext.ExpectedDeltaTime;
        int frames = delta > 0.0001 ? Math.Max(1, (int)Math.Round(1.0 / delta)) : 60;
        if (UpdateContext.FrameCount % frames != 0)
            return Task.CompletedTask;

        if (recallRunning || DateTime.Now < nextRecallAt)
            return Task.CompletedTask;

        if (!functionService.IsIdle)
        {
            nextRecallAt = DateTime.Now.AddSeconds(30);   // 正在说话，往后让一让
            return Task.CompletedTask;
        }

        _ = DoRecallAsync();
        return Task.CompletedTask;
    }

    DateTime ComputeNextRecall()
    {
        int min = Math.Max(1, Configuration.RecallMinMinutes);
        int max = Math.Max(min, Configuration.RecallMaxMinutes);
        return DateTime.Now.AddMinutes(Random.Shared.Next(min, max + 1));
    }

    /// <summary>
    /// 真正做一次回忆：挑几条尘封最久的，推给模型。
    ///
    /// 挑谁不看相关度——没人提问，也就没有"相关"这回事。看的是可得性 A 最低的那段，
    /// 也就是最久没被想起的。随机窗口保证每次不一样（见 MemoryRecallPlanner）。
    /// </summary>
    async Task<int> DoRecallAsync()
    {
        if (recallRunning)
            return 0;
        recallRunning = true;
        try
        {
            long nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            // skipInContext：跳过此刻还摆在上下文里的存档。那些摘要模型正看着，
            // 再"想起"一遍既占名额又像新情报，越推越像复读。
            List<MemoryRecallPlanner.Pick> picks = MemoryRecallPlanner.Choose(
                items,
                Configuration.RecallCount,
                Configuration.RecallMaxLevel,
                nowMs,
                TimeSpan.FromHours(Math.Max(1, Configuration.RecallCooldownHours)),
                random: null,
                detailReader: Configuration.RecallWithDetail ? item => store.ReadArchive(item, 0) : null,
                skipInContext: true);

            // 挑不出东西就别说话。以前这里照样 Poke 一句"回忆时间到"，
            // 可什么都没想起来，那一句就是纯噪音，还让模型以为自己刚想起了什么。
            if (picks.Count == 0)
                return 0;

            foreach (MemoryRecallPlanner.Pick pick in picks)
                pick.Item.LastRecalledTs = nowMs;
            store.Save(items);

            interactor.Poke(MemoryRecallPlanner.Compose(picks, DateTime.Now));
            logger.LogInformation("[Memory] 回忆：{Titles}", string.Join(" / ", picks.Select(pick => pick.Item.Title)));
            return picks.Count;
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Memory] 回忆失败（数据未改动）");
            return 0;
        }
        finally
        {
            recallRunning = false;
            nextRecallAt = ComputeNextRecall();
        }
    }

    /// <summary>设置页的「现在就回忆一次」。返回一句人话，点完知道发生了什么</summary>
    public async Task<string> RecallNow()
    {
        int count = await DoRecallAsync();
        return count == 0
            ? "没有可回忆的存档（都刚回忆过，或者库里还空着）"
            : "回忆起 " + count + " 条，已经推到对话里";
    }

    protected override Task OnDestroy()
    {
        ChatBot.ChatHistoryAdd -= OnChatHistoryAdd;
        ChatBot.ChatSend -= OnChatSend;
        tokenUser.TokenUsed -= OnTokenUsed;
        return Task.CompletedTask;
    }

    string personaCache = "";
    DateTime personaCacheAt = DateTime.MinValue;

    /// <summary>
    /// 压缩用的人设语气：读角色文件夹的 index.json。读不到就返回空串，压缩照常走内置提示词。
    /// 缓存 5 分钟，改完人设不用重载。
    /// </summary>
    string LoadPersona()
    {
        if (Configuration.CompressPersona == false)
            return "";
        if (personaCacheAt != DateTime.MinValue && DateTime.Now - personaCacheAt < TimeSpan.FromMinutes(5))
            return personaCache;

        personaCacheAt = DateTime.Now;
        personaCache = "";
        try
        {
            string[] candidates =
            [
                Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "index.json"),
                Path.Combine(AlifePath.StorageFolderPath, "index.json"),
            ];
            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                    continue;
                MemoryPersona.Result result = MemoryPersona.Extract(
                    File.ReadAllText(path), Configuration.CompressPersonaStyleOnly, Configuration.CompressPersonaMaxChars);
                personaCache = result.Text;
                logger.LogInformation("[Memory] 压缩人设：{Mode}，{Chars} 字", result.Mode, result.Text.Length);
                return personaCache;
            }
            logger.LogInformation("[Memory] 压缩人设：没找到 index.json，只用内置提示词");
        }
        catch (Exception ex)
        {
            personaCache = "";
            logger.LogWarning(ex, "[Memory] 读压缩人设失败");
        }
        return personaCache;
    }

    Task<ILanguageModel> ResolveCompressModel()
        => ResolveModelAsync(Configuration.CompressModelClassName, "压缩");

    /// <summary>按类名取模型；留空或取不到就用角色默认。取不到只记一行日志，不抛</summary>
    async Task<ILanguageModel> ResolveModelAsync(string? className, string purpose)
    {
        className = (className ?? "").Trim();
        if (className.Length == 0)
            return languageModel;

        Type? type = moduleSystem.GetAllModules()
            .FirstOrDefault(candidate => candidate.Name == className || candidate.FullName == className);
        if (type == null)
        {
            logger.LogWarning("[Memory] 找不到{Purpose}模型 {Name}，改用默认模型", purpose, className);
            return languageModel;
        }

        try
        {
            object instance = await ChatActivity.Container.RequireInstance(type, true);
            if (instance is ILanguageModel model)
                return model;
            logger.LogWarning("[Memory] {Name} 没实现 ILanguageModel，{Purpose}改用默认模型", className, purpose);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[Memory] 取{Purpose}模型 {Name} 失败，改用默认模型", purpose, className);
        }
        return languageModel;
    }

    /// <summary>
    /// 把关键词扩成几个近义说法。
    ///
    /// 二元组分词对同义词是瞎的：搜「记性」找不到写着「记忆」的存档，
    /// 搜「翻脸」找不到写着「吵起来」的。与其上向量，不如让便宜模型换三种说法——
    /// 代价小、可解释（日志里能看到扩了什么）、失败了也只是召回没变好。
    ///
    /// 三条防线：模型不可用返回空、解析不出来返回空、单个说法超过 12 字丢掉。
    /// 扩展是锦上添花，不该让一次检索因为它挂掉。
    /// </summary>
    async Task<List<string>> ExpandQueryAsync(string query)
    {
        try
        {
            ILanguageModel model = await ResolveModelAsync(Configuration.QueryExpandModelClassName, "扩展");

            ChatHistory history = new();
            history.AddSystemMessage("你在帮人做关键词检索的查询扩展。只输出结果，不要解释。");
            history.AddUserMessage(
                "把下面这个词扩成 3 个意思接近、但用词不同的说法，用于在记忆库里做关键词检索。\n"
                + "不是翻译，不是解释，不要同义词词典那种书面词。每行 2~8 个字，只输出 3 行。\n\n"
                + "词：" + query);

            ChatResult result = await model.ChatStreamingAsync(
                new ChatHistoryAgentThread(history), cancellationToken: DestroyCancellationToken);
            if (result.Exception != null || string.IsNullOrWhiteSpace(result.AIMessage))
                return [];

            List<string> variants = [];
            foreach (string raw in result.AIMessage.Split('\n'))
            {
                string line = raw.Trim().Trim('-', '*', '1', '2', '3', '.', '、', ' ', '：', ':');
                if (line.Length is < 2 or > 12)
                    continue;
                if (line.Contains("：") || line.Contains("说法") || line.Contains("近义"))
                    continue;
                if (variants.Contains(line) || string.Equals(line, query.Trim(), StringComparison.Ordinal))
                    continue;
                variants.Add(line);
                if (variants.Count >= 3)
                    break;
            }

            if (variants.Count > 0)
                logger.LogInformation("[Memory] 检索「{Query}」字面命中不足，扩展为：{Variants}",
                    query, string.Join(" / ", variants));
            return variants;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[Memory] 查询扩展失败，按原词检索（不影响结果可用性）");
            return [];
        }
    }

    // ── 抓取 → 压缩 → 就地替换 ──────────────────────────────────

    async void OnChatHistoryAdd(ChatMessageContent content)
    {
        try
        {
            if (!Configuration.CaptureEnabled || content.Role != AuthorRole.Assistant)
                return;

            await SaveHistoryAsync("保存对话历史");   // 先落盘：即便这轮不压，重启也不丢

            if (!gate.Wait(0))
                return;   // 上一轮还没压完，这轮跳过，下轮再来

            try
            {
                await OrganizeAsync();
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Memory] 整理失败（数据未改动）");
        }
    }

    /// <summary>
    /// 一轮整理最多连着压几批。
    ///
    /// 必须连着压：触发线和每批条数是配套的（120 触发 / 100 每批 → 压完留 20 条）。
    /// 但每批还受字数上限约束——一条消息几百字，上限小的时候只装得下十几条，
    /// 于是"压 100 条"变成"压 14 条"，压完还剩 106 条，下次又触发，
    /// 上下文永远卡在触发线以上。这里改成压到目标水位为止，把被字数上限拆碎的批次补齐。
    /// （默认上限已调到 50000，一百来条能一次装下，正常情况下这里只会跑一轮；
    ///   调小的用户才需要靠多批补齐。）
    /// 上限 10 批是防呆：真出意外也不会一口气把整段会话都送去模型。
    /// </summary>
    const int MaxBatchesPerPass = 10;

    /// <summary>待压缩原文的一次快照。压一批要用到正文、时间、有没有时间戳，一次扫齐</summary>
    sealed record PendingScan(List<ChatMessageContent> Raw, List<DateTime> Times, List<bool> Tagged);

    /// <summary>扫一遍上下文里还没被压成存档的原文。阶段一：只在副本上判断，不碰真实上下文，也不持历史锁</summary>
    async Task<PendingScan> ScanPendingAsync()
    {
        List<ChatMessageContent> raw = new();
        List<DateTime> times = new();
        List<bool> tagged = new();
        await EditHistoryAsync(thread => {
            foreach (ChatMessageContent message in thread.ChatHistory)
            {
                if (message.Role == AuthorRole.System || IsArchive(message))
                    continue;
                if (message.Content == null || message.Content.Trim().Length == 0)
                    continue;
                raw.Add(message);
                times.Add(StampOf(message));
                tagged.Add(TimeTag.IsMatch(message.Content));
            }
        }, "扫描待压缩对话");
        return new PendingScan(raw, times, tagged);
    }

    /// <summary>
    /// 取最旧的一批。条数上限和字数上限同时生效，哪个先到算哪个。
    ///
    /// 以前这里不告诉调用方为什么只取了这么点，日志里就只看到"压缩 14 条"，
    /// 和配置上的"每批 100 条"对不上，看着像漏压。cutByChars 就是给日志用的。
    /// </summary>
    static List<int> TakeBatch(PendingScan scan, int from, int batchSize, int maxChars, out int chars, out bool cutByChars)
    {
        List<int> batch = new();
        chars = 0;
        cutByChars = false;
        for (int i = from; i < scan.Raw.Count; i++)
        {
            if (batch.Count >= batchSize)
                break;
            int len = scan.Raw[i].Content?.Length ?? 0;
            // 至少留两条：一条的摘要等于没压，白花一次模型调用
            if (batch.Count >= 2 && chars + len > maxChars)
            {
                cutByChars = true;
                break;
            }
            batch.Add(i);
            chars += len;
        }
        return batch;
    }

    /// <summary>掐头留尾。见过上万字的一整段设定，整条塞进 transcript 会把一批的字数预算吃光</summary>
    static string Truncate(string body, int limit)
    {
        if (body.Length <= limit || limit < 100)
            return body;
        int head = limit * 3 / 5;
        int tail = limit - head;
        return body[..head] + "\n…（中间省略 " + (body.Length - limit) + " 字）…\n" + body[^tail..];
    }

    async Task OrganizeAsync()
    {
        PendingScan scan = await ScanPendingAsync();
        lastRawCount = scan.Raw.Count;   // 先记下来，界面要显示"还差多少条触发"

        int trigger = Math.Max(4, Configuration.TriggerMessages);
        if (scan.Raw.Count < trigger)
            return;

        // 原版的「概率触发」：到了阈值也不一定压，避免每次都被一次模型调用打断
        float probability = Math.Clamp(Configuration.Probability, 0f, 1f);
        if (probability < 1f && Random.Shared.NextSingle() > probability)
            return;

        // 目标水位 = 触发线 - 每批条数。120 / 100 → 压到 20 条原文。
        // 单批装不下 100 条（字数上限调小、或碰上超长消息）时，靠多轮补齐到这个水位。
        // 两个夹取：水位不能低于 4 条（留点最近的原文，别把上下文压成纯摘要）；
        // 也不能高到压不动（每批比触发还大时，至少得比触发线低 2 条才可能推进）。
        int batchSize = Math.Max(2, Configuration.BatchMessages);
        int maxChars = Math.Max(1000, Configuration.BatchMaxChars);
        int target = Math.Min(Math.Max(4, trigger - batchSize), Math.Max(2, trigger - 2));

        // 补压下限：只剩这么几条时不再单独跑一次模型，留到下次触发一起压。
        //
        // 实测过：120 条触发、压掉 100 条后剩 23 条，离水位 20 只差 3 条，
        // 却又要完整走一次「融合 + 合成摘要」——为 3 条消息花一整次调用，不值。
        // 取每批条数的四分之一：批量大时（100）门槛是 25，批量小时（5）也不会卡死成永远只压一批。
        int minTopUp = Math.Max(4, batchSize / 4);
        int before = scan.Raw.Count;
        int cursor = 0;   // 已经"算作压掉"的位置；历史此刻还没动，所以用游标而不是重扫

        // 每批的替换先攒着，最后一次性写回历史。
        //
        // 为什么要攒：宿主的历史界面渲染时会直接遍历 ChatHistory，插件每改一次，
        // 就有一个「渲染到一半集合被改」的窗口（InvalidOperationException: Collection was modified）。
        // 一轮只改一次时窗口还小，现在一轮可能连压十批，窗口就是十倍。攒成一次，窗口回到一次。
        List<(HashSet<ChatMessageContent> Consumed, MemoryItem Item, List<string> Superseded)> pending = new();
        List<string> batchLogs = new();

        while (scan.Raw.Count - cursor > target)
        {
            if (pending.Count >= MaxBatchesPerPass)
            {
                logger.LogWarning("[Memory] 本轮已连压 {Rounds} 批，待压原文还有 {Count} 条（目标 {Target} 条），先停，下次再继续",
                    pending.Count, scan.Raw.Count - cursor, target);
                break;
            }

            // 这一轮只允许压到水位为止：入口 125 条、每批 100 时，第二轮不能再把剩下的 25 条全吃了，
            // 否则上下文里一条原文都不剩，全是摘要，接话时会失真。
            int room = scan.Raw.Count - cursor - target;
            if (room < 2)
                break;

            // 已经压过至少一批，而剩下的差额还不够一次补压的门槛 → 收手。
            // 第一批不受这条限制，否则「触发后就该压一次」这个基本行为会被门槛挡掉。
            if (pending.Count > 0 && room < minTopUp)
                break;

            List<int> batch = TakeBatch(scan, cursor, Math.Min(batchSize, room), maxChars, out int chars, out bool cutByChars);
            if (batch.Count < 2)
                break;

            // 官方遇到这个直接抛异常（「请先打开时间戳」）；这边不抛——一抛整个模块就死了。
            //
            // 判定的粒度是**整段待压历史**，不是当前这一批。以前看一批：一批十几条里恰好都没带
            // （密集对话很常见，消息过滤按 TimestampInterval 节流，40 秒才注入一次），
            // 就喊「去打开时间戳开关」——可开关本来就是开的，只会让人白找一遍。
            if (!warnedNoTimestamp)
            {
                warnedNoTimestamp = true;
                int tagged = scan.Tagged.Count(value => value);
                int total = scan.Tagged.Count;

                if (tagged == 0)
                {
                    logger.LogWarning("[Memory] 上下文里 {Total} 条原文一条时间戳都没有，存档起止只能记成消息到达时刻，"
                        + "按时间检索会不准。这是真的没开，不是节流——去消息过滤插件打开 EnableTimestamp", total);
                }
                else if (tagged * 5 < total)
                {
                    logger.LogInformation("[Memory] 时间戳覆盖 {Tagged}/{Total} 条（{Percent}%），没带的用消息到达时刻补。"
                        + "消息过滤按 TimestampInterval 节流注入（默认 40 秒一次），密集对话本来就只有少数几条带——正常，不用管",
                        tagged, total, tagged * 100 / Math.Max(1, total));
                }
            }

            StringBuilder transcript = new();
            foreach (int i in batch)
            {
                string role = scan.Raw[i].Role == AuthorRole.User ? "用户" : scan.Raw[i].Role == AuthorRole.Assistant ? "我" : scan.Raw[i].Role.Label;
                // 脱敏在这里做：原文要落盘，摘要也要落盘，两处都从这一份 transcript 出发
                string body = Protect(scan.Raw[i].Content!.Trim());
                if (body.Length > maxChars)
                    body = Truncate(body, maxChars);
                transcript.Append('【').Append(role).Append("】").AppendLine(body).AppendLine();
            }

            DateTime start = scan.Times[batch[0]];
            // 结束时间必须是**这一批**的最后一条。以前取 times[^1]（整个历史的最新一条），
            // 存档区间会一直拉到"压缩那一刻"，与后面几条存档重叠，按时间检索时同一段时间会被两条命中。
            DateTime end = scan.Times[batch[^1]] > start ? scan.Times[batch[^1]] : start;
            string range = start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 到 "
                + scan.Times[batch[^1]].ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

            MemoryDraft? draft;
            using (RentThinking())
            {
                draft = await MemoryCompressor.CompressAsync(
                    compressModel, transcript.ToString(), range, Configuration.CompressPrompt, ArchiveDigest(), LoadPersona(), false, DestroyCancellationToken);
            }
            if (draft == null)
            {
                // 模型失败多半不是偶发（超长、限流），接着压下一批只会连着失败，本轮就此收手；
                // 已经成功的那几批仍然要写回去，不能因为最后一批失败就丢掉前面的成果
                logger.LogWarning("[Memory] 这一批压缩失败，数据未改动（{Count} 条留在上下文）", batch.Count);
                break;
            }

            MemoryItem item = NewItem(1, draft, start, end, batch.Count, transcript.ToString());
            items.Add(item);
            indexer.Add(item);

            HashSet<ChatMessageContent> consumed = batch.Select(i => scan.Raw[i])
                .ToHashSet<ChatMessageContent>(ReferenceEqualityComparer.Instance);
            List<string> superseded = new();
            MarkSuperseded(draft, item, superseded);
            pending.Add((consumed, item, superseded));

            batchLogs.Add(cutByChars
                ? "[Memory] 压缩 " + batch.Count + " 条（" + chars + " 字，被字数上限 " + maxChars + " 截断）→ " + item.Id + "「" + item.Title + "」"
                : "[Memory] 压缩 " + batch.Count + " 条（" + chars + " 字）→ " + item.Id + "「" + item.Title + "」");

            cursor = batch[^1] + 1;
        }

        if (pending.Count == 0)
            return;

        // 阶段二：在原位置替换——这就是原版最有价值的手感，历史不会断裂。
        // 多批一次写：宿主历史界面渲染时直接遍历 ChatHistory，改的次数越多越容易撞上。
        await EditHistoryAsync(thread =>
        {
            foreach ((HashSet<ChatMessageContent> consumed, MemoryItem item, List<string> superseded) in pending)
                ReplaceInPlace(thread, consumed, item, superseded);
        }, "写入记忆存档");

        store.Save(items);
        lastRawCount = scan.Raw.Count - cursor;

        foreach (string line in batchLogs)
            logger.LogInformation("{Line}", line);

        int after = Math.Max(0, scan.Raw.Count - cursor);
        if (pending.Count > 1)
        {
            logger.LogInformation("[Memory] 本轮压了 {Rounds} 批，待压原文 {Before} → {After} 条（目标 {Target} 条）",
                pending.Count, before, after, target);
        }
        else if (after > target)
        {
            // 只压了一批、停在离水位不远的地方——这是有意留的，不是没压到位
            logger.LogInformation("[Memory] 待压原文 {Before} → {After} 条；离水位 {Target} 条只差 {Gap} 条，"
                + "不值得为它多跑一次模型，留到下次触发一起压", before, after, target, after - target);
        }

        await CollapseAsync();
        await EnforceCapacityAsync();
        await EnforcePermanentBudgetAsync();

        // 攒够一批就归纳一次。放后台：归纳要调模型，不该拖着这轮压缩
        if (Configuration.ReflectEnabled
            && items.Count - lastReflectItemCount >= Math.Max(2, Configuration.ReflectAfterArchives))
        {
            _ = Task.Run(async () => {
                try { await ReflectAsync(false); }
                catch (Exception e) { logger.LogWarning(e, "[Memory] 后台归纳失败"); }
            });
        }
    }

    /// <summary>
    /// 按事实键取代：同一个键只留最新那条，旧的移出上下文并记账（不删，仍可检索、可撤销）。
    ///
    /// 只认**完全相同**的键。宁可不取代，也不做模糊匹配——
    /// 把 `他.身高` 和 `他.体重` 认成一件事，等于凭空抹掉一条记忆。
    /// </summary>
    void SupersedeByFactKey(MemoryItem created, List<string> removeIds)
    {
        string key = (created.FactKey ?? "").Trim();
        if (key.Length == 0)
            return;

        foreach (MemoryItem old in items.Where(candidate =>
            candidate.InContext
            && !candidate.Id.Equals(created.Id, StringComparison.Ordinal)
            && string.Equals((candidate.FactKey ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            old.InContext = false;
            old.SupersededBy = created.Id;
            removeIds.Add(old.Id);
            store.Ledger("supersede", old, "被 " + created.Id + " 取代（同一件事：" + key + "）");
            logger.LogInformation("[Memory] {Old} 被 {New} 取代：同一件事（{Key}）", old.Id, created.Id, key);
        }
    }

    // ── 星云（给 MemoryNebulaUI 读）─────────────────────────────

    /// <summary>
    /// 星云数据。没有实例就返回空串，组件会跳过这一轮。
    /// 组件是 UI 线程上的定时器，而压缩是后台线程——中间可能有并发修改，
    /// 所以整段包在 try 里：画不出来最多是这一帧不动，不该让记忆出问题。
    /// </summary>
    internal static string NebulaJson()
    {
        if (latestRef == null || !latestRef.TryGetTarget(out MemoryService? service))
            return "";
        try
        {
            return service.BuildNebulaJson();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>星云按钮离底部多远。没有实例就给默认值，别让按钮因为角色没激活就消失</summary>
    internal static int NebulaButtonBottomPx()
    {
        if (latestRef != null && latestRef.TryGetTarget(out MemoryService? service))
        {
            try { return Math.Clamp(service.Configuration.NebulaButtonBottomPx, 0, 1200); }
            catch { }
        }
        return 300;
    }

    /// <summary>星云按钮离右边多远。和状态挂件各调各的</summary>
    internal static int NebulaButtonRightPx()
    {
        if (latestRef != null && latestRef.TryGetTarget(out MemoryService? service))
        {
            try { return Math.Clamp(service.Configuration.NebulaButtonRightPx, 0, 1200); }
            catch { }
        }
        return 14;
    }

    static readonly JsonSerializerOptions NebulaJsonOptions = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    string BuildNebulaJson()
    {
        const int MaxStars = 300;
        const int MaxClusters = 12;

        List<MemoryPattern> live = patterns.Where(pattern => pattern.Live)
            .OrderByDescending(pattern => pattern.Evidence.Count)
            .Take(MaxClusters)
            .ToList();

        Dictionary<string, int> clusterOf = new(StringComparer.Ordinal);
        for (int i = 0; i < live.Count; i++)
        {
            foreach (string id in live[i].Evidence)
                clusterOf[id] = i;
        }

        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        List<MemoryItem> pool = items
            .Where(item => item.Summary.Length > 0)
            .OrderByDescending(item => item.InContext)
            .ThenByDescending(item => item.EndTs)
            .Take(MaxStars)
            .ToList();

        var clusters = live.Select(pattern => new {
            t = pattern.Theme,
            n = pattern.Evidence.Count,
        }).ToList();

        var stars = pool.Select(item => new {
            t = item.Title,
            s = MemoryStore.Trim(MemoryStore.Flatten(item.Summary), 260),
            d = DateTimeOffset.FromUnixTimeMilliseconds(item.EndTs)
                .LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            // 心情：没有快照就是 0.55（中性）——不是"难过"，只是不知道
            m = Math.Round(item.StateMood ?? 0.55, 3),
            // 亮度：重要度 + 新鲜度各占一半。太旧的星应该暗下去，但不该消失
            w = Math.Round(0.5 * Math.Clamp(item.Importance, 0, 1)
                + 0.5 * (1.0 / (1.0 + Math.Max(0, (now - item.EndTs) / 86400000.0) / 30.0)), 3),
            p = clusterOf.TryGetValue(item.Id, out int index) ? index : -1,
        }).ToList();

        return JsonSerializer.Serialize(new { c = clusters, s = stars }, NebulaJsonOptions);
    }

    // ── 归纳：存档 → 规律 / 信念 ────────────────────────────────

    /// <summary>
    /// 从最近的存档里找反复出现的模式，炼成规律/信念。
    ///
    /// 这是「记忆 → 规律 → 信念」缺的那一层。压缩只会把 100 条变成 1 条摘要，
    /// 从来没做过"把这 8 条摘要变成 1 条规律"——而后者才是人格，前者只是台账。
    ///
    /// 三条克制：
    ///   1. 证据少于 2 条的不要（模型很爱把单次经历说成"她总是"）
    ///   2. 证据 id 必须真存在（模型会编索引，编出来的就是伪造引用）
    ///   3. 只增补、不自动作废。推翻一条信念要人来做——模型今天心情不好就改人格，比不改更糟
    /// </summary>
    async Task<int> ReflectAsync(bool manual)
    {
        if (reflectRunning)
            return 0;
        if (!manual && !Configuration.ReflectEnabled)
            return 0;

        reflectRunning = true;
        try
        {
            List<MemoryItem> pool = items
                .Where(item => item.Summary.Length > 0 && item.SupersededBy.Length == 0)
                .OrderByDescending(item => item.EndTs)
                .Take(Math.Clamp(Configuration.ReflectMaxArchives, 6, 120))
                .ToList();

            if (pool.Count < 4)
            {
                if (manual)
                    interactor.Poke("存档还太少（" + pool.Count + " 条），先攒到 4 条以上才谈得上「规律」");
                return 0;
            }

            StringBuilder material = new();
            foreach (MemoryItem item in pool)
            {
                material.Append('[').Append(item.Id).Append("] ")
                    .Append(item.Title)
                    .Append("（").Append(DateTimeOffset.FromUnixTimeMilliseconds(item.EndTs)
                        .LocalDateTime.ToString("MM-dd", CultureInfo.InvariantCulture));
                if (item.StateMood is { } mood)
                    material.Append("，当时心情 ").Append((int)Math.Round(mood * 100));
                material.AppendLine("）");
                material.AppendLine(MemoryStore.Trim(MemoryStore.Flatten(item.Summary), 260));
                material.AppendLine();
            }

            string known = patterns.Where(pattern => pattern.Live).ToList() is { Count: > 0 } live
                ? "\n已经归纳过的主题（同类就合并，别重复造）：" + string.Join("、", live.Select(pattern => pattern.Theme))
                : "";

            string prompt = """
                下面是一个角色过去一段时间的记忆存档。请找出**反复出现**的模式，不是总结单次事件。

                输出一个 JSON 数组，最多 3 条；没有够格的就输出 []。
                每条长这样：
                {"theme":"主题，4~10 字，同类事用它去重","chapter":"章名","statement":"一句话说清这类事一般怎么回事，或她因此信了什么","kind":0,"evidence":["存档id","存档id"],"confidence":0.0}

                规则：
                - evidence 至少 2 条，必须抄上面真实出现过的 id，不要自己编
                - kind=0 是规律（事情一般怎么发生），kind=1 是信念（她因此信了什么）。有明确立场才写 1
                - 只写材料支持的，不要补充常识，不要写成鸡汤
                - 宁可少给一条，也不要为了凑数把单次经历写成"她总是"
                - theme 必须能装进上面「已经归纳过的主题」里就用那个**原词**，不要换个说法另开一条。
                  只有现有主题确实装不下、而且材料里同一类事出现了两次以上，才新开
                - chapter **必须**是下面这几章之一，照抄原词，不许自己起名：
                  {chapters}
                  装不进任何一章就不要输出这一条 —— 骨架是固定的，它回答的是"我是谁"，不是流水账
                """;
            prompt = prompt.Replace("{chapters}", string.Join(" / ", Configuration.ReflectChapters));

            ChatHistory history = new();
            history.AddSystemMessage("你在从记忆存档里归纳反复出现的模式。只输出 JSON 数组。");
            history.AddUserMessage(material + known + "\n" + prompt);

            ILanguageModel model = await ResolveModelAsync(Configuration.ReflectModelClassName, "归纳");
            ChatResult result = await model.ChatStreamingAsync(
                new ChatHistoryAgentThread(history), cancellationToken: DestroyCancellationToken);
            if (result.Exception != null || string.IsNullOrWhiteSpace(result.AIMessage))
                return 0;

            long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            List<MemoryPattern> parsed = MemoryPatternParser.Parse(result.AIMessage, now);
            if (parsed.Count == 0)
            {
                logger.LogInformation("[Memory] 这轮归纳没有产出（证据不足或格式不符）");
                return 0;
            }

            HashSet<string> knownIds = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            int added = 0;
            int merged = 0;

            foreach (MemoryPattern fresh in parsed)
            {
                // 编出来的 id 不许进证据——伪造引用比没有归纳更糟
                fresh.Evidence = fresh.Evidence.Where(knownIds.Contains).Distinct(StringComparer.Ordinal).ToList();
                if (fresh.Evidence.Count < 2)
                    continue;

                // 章名不在名单里就当没归章：宁可散着，也不要长出第九个游离的章
                if (fresh.Chapter.Length > 0 && !Configuration.ReflectChapters.Contains(fresh.Chapter))
                    fresh.Chapter = "";

                MemoryPattern? same = FindSameTheme(fresh.Theme);

                if (same == null)
                {
                    patterns.Add(fresh);
                    added++;
                    continue;
                }

                foreach (string id in fresh.Evidence)
                {
                    if (!same.Evidence.Contains(id))
                        same.Evidence.Add(id);
                }
                same.Statement = fresh.Statement;
                same.Kind = fresh.Kind;
                if (fresh.Chapter.Length > 0)
                    same.Chapter = fresh.Chapter;   // 归章以最新一次为准（她可能在重新理解自己）
                same.Confidence = Math.Max(same.Confidence, fresh.Confidence);
                same.UpdatedAt = now;
                merged++;
            }

            if (added + merged == 0)
                return 0;

            patternStore.Save(patterns);
            lastReflectItemCount = items.Count;

            logger.LogInformation("[Memory] 归纳完成：新增 {Added} 条、合并 {Merged} 条，现有 {Live} 条现行", added, merged,
                patterns.Count(pattern => pattern.Live));
            foreach (MemoryPattern pattern in patterns.Where(pattern => pattern.Live && pattern.UpdatedAt == now))
                logger.LogInformation("[Memory] 　{Kind}｜{Theme}：{Statement}（{Count} 条证据）",
                    pattern.KindWord, pattern.Theme, pattern.Statement, pattern.Evidence.Count);

            return added + merged;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[Memory] 归纳失败（存档未受影响）");
            return 0;
        }
        finally
        {
            reflectRunning = false;
        }
    }

    /// <summary>
    /// 找同一个主题。除了完全相同，还认**互相包含**——
    /// 模型会给出「被说任性」「嫌我任性」「说我作」三种说法，其实是同一件事。
    /// 不合并的话，跑三个月主题会越长越碎，每条都只有一两条证据，等于没有归纳。
    ///
    /// 只在长度 ≥2 时做包含判断：单字主题太容易误吞。
    /// </summary>
    MemoryPattern? FindSameTheme(string theme)
    {
        string key = NormalizeTheme(theme);
        if (key.Length == 0)
            return null;

        MemoryPattern? exact = patterns.FirstOrDefault(pattern =>
            pattern.Live && string.Equals(NormalizeTheme(pattern.Theme), key, StringComparison.Ordinal));
        if (exact != null)
            return exact;

        if (key.Length < 2)
            return null;

        return patterns.FirstOrDefault(pattern => {
            if (!pattern.Live)
                return false;
            string other = NormalizeTheme(pattern.Theme);
            return other.Length >= 2 && (other.Contains(key, StringComparison.Ordinal) || key.Contains(other, StringComparison.Ordinal));
        });
    }

    static string NormalizeTheme(string theme)
        => new string(theme.Where(ch => !char.IsPunctuation(ch) && !char.IsWhiteSpace(ch)).ToArray());

    /// <summary>把被压掉的那一段换成一条存档消息，位置就在原来第一条消息处</summary>
    void ReplaceInPlace(ChatHistoryAgentThread thread, HashSet<ChatMessageContent> consumed, MemoryItem item,
        List<string> alsoRemove)
    {
        int insertAt = -1;
        for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
        {
            ChatMessageContent message = thread.ChatHistory[i];
            bool hit = consumed.Contains(message)
                || (alsoRemove.Count > 0 && IsArchive(message) && alsoRemove.Contains(IdOf(message) ?? ""));
            if (!hit)
                continue;
            if (consumed.Contains(message))
                insertAt = i;
            thread.ChatHistory.RemoveAt(i);
        }

        if (insertAt < 0)
            insertAt = thread.ChatHistory.Count;

        thread.ChatHistory.Insert(insertAt, new ChatMessageContent(AuthorRole.Assistant, item.Render()));
    }

    /// <summary>
    /// dsh 的「融合四判」里最有价值的一条：矛盾作废。
    /// 模型在压缩时如果写明「替代：某索引」，就把那条标记作废——不再进上下文，但原文和摘要都留在库里。
    /// </summary>
    void MarkSuperseded(MemoryDraft draft, MemoryItem created, List<string> removeIds)
    {
        // 先按事实键收：同一件会变的事，新的一条进来，旧的自动作废。
        // 这条不依赖模型认出"哪条被推翻了"，只要求它说清"这是什么方面"。
        SupersedeByFactKey(created, removeIds);

        string key = (draft.Supersede ?? "").Trim();
        if (key.Length == 0)
            return;

        MemoryItem? old = items.FirstOrDefault(candidate =>
            candidate.InContext
            && !candidate.Id.Equals(created.Id, StringComparison.Ordinal)
            && candidate.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        if (old == null)
            return;

        old.InContext = false;
        old.SupersededBy = created.Id;
        removeIds.Add(old.Id);
        store.Ledger("supersede", old, "被 " + created.Id + " 取代（结论更新）");
        logger.LogInformation("[Memory] {Old} 被 {New} 取代：结论已更新", old.Id, created.Id);
    }

    /// <summary>给压缩模型看的已有存档清单（只给索引和标题，控制 token）</summary>
    string ArchiveDigest()
    {
        List<MemoryItem> live = Live().ToList();
        if (live.Count == 0)
            return "";
        StringBuilder digest = new();
        foreach (MemoryItem item in live.Take(40))
        {
            digest.Append("- ").Append(item.Id).Append("  ").AppendLine(item.Title);
        }
        return digest.ToString().TrimEnd();
    }

    /// <summary>
    /// 同等级的存档攒够 GroupSize 条，压掉最旧的几条升一级。
    /// **留最新的一条在原层**——这是原版的「压 3 留 1」，也是等级阶梯不断层的关键：
    /// 全吃掉的话 4 条 L1 会直接变成 1 条 L2，上下文里那段近期记忆就整个消失了。
    /// </summary>
    async Task CollapseAsync()
    {
        int groupSize = Math.Max(2, Configuration.GroupSize);
        foreach (int level in items.Where(item => item.InContext).Select(item => item.Level).Distinct().OrderBy(value => value).ToList())
        {
            List<MemoryItem> all = items
                .Where(item => item.InContext && item.Level == level && !item.IsPermanent)
                .OrderBy(item => item.StartTs)
                .ToList();
            if (all.Count < groupSize)
                continue;
            if (level >= Math.Max(1, Configuration.MaxLevel))
                continue;   // 到顶了不再升，和原版一致

            // GroupSize=2 时没有可留的（压 1 条等于没压），退化成全压
            int take = groupSize > 2 ? groupSize - 1 : groupSize;
            List<MemoryItem> group = all.Take(take).ToList();

            StringBuilder transcript = new();
            foreach (MemoryItem member in group)
            {
                transcript.Append("【L").Append(member.Level).Append(' ')
                    .Append(member.StartLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("】")
                    .AppendLine(member.Summary).AppendLine();
            }

            string range = group[0].StartLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 到 "
                + group[^1].EndLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            MemoryDraft? draft;
            using (RentThinking())
            {
                draft = await MemoryCompressor.CompressAsync(
                    compressModel, transcript.ToString(), range, Configuration.CompressPrompt, ArchiveDigest(), LoadPersona(), true, DestroyCancellationToken);
            }
            if (draft == null)
                continue;   // 升不了级不影响可用性，下次再来

            MemoryItem merged = NewItem(level + 1, draft, group[0].StartLocal, group[^1].EndLocal,
                group.Sum(member => Math.Max(1, member.MsgCount)),
                string.Join("\n\n", group.Select(member => member.Render())));
            merged.Importance = Math.Max(draft.Importance, group.Max(item => item.Importance));
            items.Add(merged);
            indexer.Add(merged);

            HashSet<string> ids = group.Select(member => member.Id).ToHashSet(StringComparer.Ordinal);
            foreach (MemoryItem member in group)
            {
                member.InContext = false;
                store.Ledger("collapse", member, "并入 " + merged.Id);
            }
            List<string> superseded = new();
            MarkSuperseded(draft, merged, superseded);

            await EditHistoryAsync(thread => {
                HashSet<ChatMessageContent> consumed = thread.ChatHistory
                    .Where(message => IsArchive(message) && idsof(message).Any(ids.Contains))
                    .ToHashSet<ChatMessageContent>(ReferenceEqualityComparer.Instance);
                ReplaceInPlace(thread, consumed, merged, superseded);
            }, "记忆升级");

            store.Save(items);
            logger.LogInformation("[Memory] L{From} ×{Count} → L{To}「{Title}」", level, group.Count, level + 1, merged.Title);
            return;   // 一次只升一级，避免同轮连锁改动

            static IEnumerable<string> idsof(ChatMessageContent message)
            {
                string? id = IdOf(message);
                return id == null ? Array.Empty<string>() : new[] { id };
            }
        }
    }

    /// <summary>存档总量超上限就把最旧的移出上下文；钉住的、以及 MinKeepItems 以内的不动</summary>
    async Task EnforceCapacityAsync()
    {
        List<MemoryItem> live = Live().ToList();
        int budget = Math.Max(500, Configuration.MaxBlockChars);
        int keepAtLeast = Math.Max(1, Configuration.MinKeepItems);
        HashSet<string> drop = new(StringComparer.Ordinal);

        // 顺序先算好，再按序淘汰。两种规则回答的是不同的问题：
        //   旧规则（重要度 → 时间）：谁重要。它看不见"常被检索"这件事。
        //   新规则（重要度 × 可得性）：谁还活着。一条三个月前压出来但反复被翻出来的记忆，
        //   比一条两个月前压出来、再没人提过的更该留——这是 ACT-R 的幂律衰减，纯本地算。
        List<MemoryItem> candidates = live.Where(item => !item.IsPermanent).ToList();
        List<MemoryItem> order = Configuration.ActivationEvict
            ? MemoryActivation.EvictionOrder(candidates)
            : candidates.OrderBy(item => item.Importance)
                .ThenBy(item => item.StartTs)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();

        int cursor = 0;
        while (CharsOf(live) > budget && live.Count > keepAtLeast && cursor < order.Count)
        {
            MemoryItem pick = order[cursor++];
            if (!pick.InContext)
                continue;
            pick.InContext = false;
            live.Remove(pick);
            drop.Add(pick.Id);
            store.Ledger("evict", pick, Configuration.ActivationEvict
                ? "超出记忆总量上限（激活度最低）"
                : "超出记忆总量上限");
        }

        if (drop.Count == 0)
            return;

        store.Save(items);
        await EditHistoryAsync(thread => {
            for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
            {
                ChatMessageContent message = thread.ChatHistory[i];
                if (!IsArchive(message))
                    continue;
                string? id = IdOf(message);
                if (id != null && drop.Contains(id))
                    thread.ChatHistory.RemoveAt(i);
            }
        }, "归档旧记忆");
        logger.LogInformation("[Memory] 记忆超限，移出 {Count} 条（仍可检索）", drop.Count);
    }

    // ── 上下文里的存档 ──────────────────────────────────────────

    static bool IsArchive(ChatMessageContent message)
    {
        if (message.Role != AuthorRole.Assistant || message.Content == null)
            return false;
        return ArchiveTag.IsMatch(message.Content) || LegacyArchiveTag.IsMatch(message.Content);
    }

    static string? IdOf(ChatMessageContent message)
    {
        if (message.Content == null)
            return null;
        Match match = ArchiveTag.Match(message.Content);
        if (match.Success)
            return match.Groups[1].Value;
        match = LegacyArchiveTag.Match(message.Content);
        return match.Success ? match.Groups[1].Value : null;
    }

    IEnumerable<MemoryItem> Live()
    {
        return items.Where(item => item.InContext).OrderBy(item => item.StartTs).ThenBy(item => item.Id, StringComparer.Ordinal);
    }

    /// <summary>
    /// 首次导入旧存档后，把最高等级的几条放回上下文，让「阶梯」不是空的。
    /// 全放会把它撑爆（47 条 × 平均 1900 字 ≈ 9 万字），所以只取等级最高的，
    /// 装到预算的七成为止——L4 → L3 → L100 这个顺序，本来就是原版压缩出来的骨架。
    /// </summary>
    int SeedContextFromLegacy()
    {
        // 只补一次：上下文里已经有旧存档了就不再补（新压出来的条目 Source 是 auto，不算）
        if (items.Any(item => item.InContext && item.Source == "legacy"))
            return 0;

        int budget = (int)(Math.Max(500, Configuration.MaxBlockChars) * 0.7);
        int used = CharsOf(Live());
        int seeded = 0;
        foreach (MemoryItem item in items
                     .Where(item => !item.InContext && item.SupersededBy.Length == 0)
                     .OrderByDescending(item => item.Level)
                     .ThenByDescending(item => item.StartTs))
        {
            int size = item.Render().Length + 1;
            if (seeded > 0 && used + size > budget)
                continue;   // 这条放不下，试下一条更短的
            if (seeded >= 16)
                break;
            item.InContext = true;
            used += size;
            seeded++;
        }
        return seeded;
    }

    int CountLive() => items.Count(item => item.InContext);

    /// <summary>
    /// 数一下上下文里还有多少条没压的原文（L0）。顺带数有多少条带时间戳——
    /// 这个数是设置页「L0 原文 N 条」的来源，不数就一直是 0，看起来像"没有待压的"。
    /// </summary>
    async Task<(int Raw, int Tagged)> CountRawAsync()
    {
        int raw = 0;
        int tagged = 0;
        await EditHistoryAsync(thread => {
            foreach (ChatMessageContent message in thread.ChatHistory)
            {
                if (message.Role == AuthorRole.System || IsArchive(message))
                    continue;
                if (message.Content == null || message.Content.Trim().Length == 0)
                    continue;
                raw++;
                if (TimeTag.IsMatch(message.Content))
                    tagged++;
            }
        }, "统计原文条数");
        lastRawCount = raw;
        return (raw, tagged);
    }

    /// <summary>
    /// 从磁盘重读一遍，重建索引与统计。
    ///
    /// 什么时候需要它：设置页刚打开（内存里的列表可能停在上次启动）、手动改了
    /// `tiers/items.json`、或者点了界面上的「刷新」。磁盘是权威——所有改动都是
    /// 先 `store.Save` 再改历史，所以重读不会丢任何东西。
    ///
    /// 走 `Guarded` 和压缩互斥：正在压缩时直接拒绝，而不是两边同时改 `items`。
    /// </summary>
    public async Task<string> RescanAsync()
    {
        string result = await Guarded(async () =>
        {
            items = store.Load();
            EnsureShortIds();
            indexer.Build(items);
            await CountRawAsync();
            return "已刷新：库里 " + items.Count + " 条，上下文 " + CountLive() + " 条，待压原文 " + lastRawCount + " 条";
        });

        // 条目换了，卡片词集合的缓存必须作废，否则「该不该提醒去查」会用旧数据判断
        cardTokens = null;
        cardStamp = "";
        return result;
    }

    /// <summary>
    /// 设置页「导出完整上下文」：把模型眼下能看到的历史铺成一份能翻的文本。
    /// 走 Guarded 是为了不和压缩同时读历史；整个过程只读，不改任何数据。
    /// </summary>
    public async Task<string> DumpContextAsync()
    {
        return await Guarded(DumpContextCoreAsync);
    }

    async Task<string> DumpContextCoreAsync()
    {
        var lines = new List<ContextDump.Line>();
        await EditHistoryAsync(thread => {
            foreach (ChatMessageContent message in thread.ChatHistory)
            {
                string content = message.Content ?? "";
                Match tag = ArchiveTag.Match(content);
                lines.Add(new ContextDump.Line(
                    message.Role.Label,
                    content,
                    StampOf(message).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    tag.Success ? LevelOf(message) : 0,
                    tag.Success ? tag.Groups[1].Value : ""));
            }
        }, "导出完整上下文");

        string dump = ContextDump.Build(lines, Character.Name, DateTime.Now);
        string root = Path.Combine(AlifePath.StorageFolderPath, Character.StorageKey, "Memory");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "context-dump.txt");
        await File.WriteAllTextAsync(path, dump, new UTF8Encoding(false));

        logger.LogInformation("[Memory] 完整上下文已导出：{Count} 条 / {Chars} 字 → {Path}",
            lines.Count, dump.Length, path);
        return "已导出 " + lines.Count + " 条 / "
            + dump.Length.ToString("N0", CultureInfo.InvariantCulture) + " 字 → " + path;
    }

    static int CharsOf(IEnumerable<MemoryItem> live) => live.Sum(item => item.Render().Length + 1);

    MemoryItem NewItem(int level, MemoryDraft draft, DateTime start, DateTime end, int msgCount, string archiveBody)
    {
        string id = level.ToString(CultureInfo.InvariantCulture) + "-"
            + start.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-"
            + end.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        string summary = Protect(draft.Summary);
        string title = MemoryStore.CleanTitle(Protect(draft.Title));
        if (title.Length == 0)
            title = MemoryStore.CleanTitle(MemoryStore.FirstLine(summary));

        MemoryItem item = new() {
            Id = id,
            ShortId = MemoryItem.MakeShortId(id),
            Level = level,
            Title = title,
            Summary = summary,
            Facts = BuildFacts(summary, archiveBody),
            StartTs = new DateTimeOffset(start).ToUnixTimeMilliseconds(),
            EndTs = new DateTimeOffset(end).ToUnixTimeMilliseconds(),
            Importance = draft.Importance,
            InContext = true,
            CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            FactKey = (draft.FactKey ?? "").Trim(),
            Source = "auto",
            MsgCount = msgCount,
            ArchiveRel = "L" + level.ToString(CultureInfo.InvariantCulture) + "/" + id + ".txt"
        };

        // 压缩那一刻她的状态。状态插件没装/读不到就留 null——
        // 这不是"她当时无所谓"，是"不知道"，两者不能混。
        if (Configuration.MoodAwareSearch)
        {
            MoodSnapshot? snapshot = MoodMirror.Read(Character.StorageKey);
            if (snapshot != null)
            {
                item.StateMood = snapshot.Mood;
                item.StateEnergy = snapshot.Energy;
                item.StateCloseness = snapshot.Closeness;
            }
        }
        if (archiveBody.Length > 0)
            store.WriteArchive(item, Protect(archiveBody));
        return item;
    }

    /// <summary>落盘前统一过一道脱敏。关掉开关就原样返回，行为与本版之前一致</summary>
    string Protect(string text)
    {
        return Configuration.RedactSecrets ? MemoryText.Redact(text) : text;
    }

    /// <summary>
    /// 摘要没覆盖、但猜不回来的东西：路径、版本号、开关、错误码。
    ///
    /// 这是压缩最隐性的损失。摘要写"改了插件的编译引用"是对的，可下次要复现就得知道
    /// 是 `D:\Alife.Client\*.dll` 还是 `Runtime\PluginContext\CompiledPlugins`——
    /// 这两个字符串模型编不出来，检索也检不到（原文字面有，摘要里没写）。
    /// 所以从原文机械抽一份差集，附在存档末尾。几十个字，换掉一次"翻遍全库也找不到"。
    /// </summary>
    string BuildFacts(string summary, string original)
    {
        if (!Configuration.FactsEnabled || original.Length == 0)
            return "";

        HashSet<string> covered = MemoryText.Identifiers(summary)
            .Select(value => value.Replace('\\', '/').ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        List<string> fresh = new();
        foreach (string identifier in MemoryText.Identifiers(original))
        {
            string key = identifier.Replace('\\', '/').ToLowerInvariant();
            if (covered.Contains(key) || fresh.Contains(identifier))
                continue;
            fresh.Add(MemoryStore.Trim(identifier, 80));
            if (fresh.Count >= 12)
                break;
        }
        if (fresh.Count == 0)
            return "";

        string text = string.Join(" · ", fresh);
        int max = Configuration.FactsMaxChars;
        return max > 0 && text.Length > max ? MemoryStore.Trim(text, max) : text;
    }

    /// <summary>
    /// 启动时按库补回存档：宿主没保留上下文、或存档消息丢了，都能恢复。
    /// 位置按时间戳插——排在第一条比它晚的对话之前，保持"就地压缩"的形态。
    /// </summary>
    int RestoreArchives(ChatHistoryAgentThread thread)
    {
        int changes = ReconcileArchives(thread);

        HashSet<string> present = thread.ChatHistory
            .Where(message => IsArchive(message))
            .Select(IdOf)
            .Where(id => id != null)
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (MemoryItem item in Live().ToList())
        {
            if (present.Contains(item.Id))
                continue;
            thread.ChatHistory.Insert(PositionFor(thread, item), new ChatMessageContent(AuthorRole.Assistant, item.Render()));
            changes++;
        }
        return changes;
    }

    /// <summary>
    /// 把上下文里的存档消息跟库对齐。三种情况：
    ///   1. 索引精确命中 → 不动（内容旧不旧是 ReplaceArchiveMessage 的事）
    ///   2. 只对上"归一化钥匙"（新旧 id 写法不同）→ 按库里的当前内容重写，身份证顺带改正
    ///   3. 都对不上 → 库里已经删了，消息是孤儿，摘掉
    ///
    /// 不这么做的话，删掉的记忆会以孤儿消息的形式一直躺在上下文里——这是"删了还在"的真正原因，
    /// 跟背景/人设文本一点关系都没有。
    /// 库读不出来（首次启动、文件损坏）时一律不动，宁可留着也不清空。
    /// </summary>
    int ReconcileArchives(ChatHistoryAgentThread thread)
    {
        if (items.Count == 0)
            return 0;

        HashSet<string> known = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, MemoryItem> byKey = new(StringComparer.Ordinal);
        foreach (MemoryItem item in items)
            byKey[KeyOf(item.Id)] = item;

        int fixedUp = 0, dropped = 0;
        for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
        {
            ChatMessageContent message = thread.ChatHistory[i];
            if (!IsArchive(message))
                continue;

            string? id = IdOf(message);
            if (id == null || known.Contains(id))
                continue;

            if (byKey.TryGetValue(KeyOf(id), out MemoryItem? match))
            {
                thread.ChatHistory[i] = new ChatMessageContent(AuthorRole.Assistant, match.Render());
                fixedUp++;
                continue;
            }

            thread.ChatHistory.RemoveAt(i);
            dropped++;
        }

        if (fixedUp > 0 || dropped > 0)
            logger.LogInformation("[Memory] 上下文对齐：改正 {Fixed} 条旧索引，清掉 {Dropped} 条库里已无的存档",
                fixedUp, dropped);
        return fixedUp + dropped;
    }

    int PositionFor(ChatHistoryAgentThread thread, MemoryItem item)
    {
        for (int i = 0; i < thread.ChatHistory.Count; i++)
        {
            ChatMessageContent message = thread.ChatHistory[i];
            if (message.Role == AuthorRole.System || IsArchive(message))
                continue;
            if (StampOf(message) > item.EndLocal)
                return i;
        }
        return thread.ChatHistory.Count;
    }

    /// <summary>
    /// 把当前历史整段写进 Memory/History.json（官方同一格式）。
    /// Alife 不持久化会话——不写它，重启后未压缩的原文就没了。
    /// 存档消息带上它在库里的等级与起止时间，重启后才认得出哪条是存档。
    /// </summary>
    async Task SaveHistoryAsync(string reason)
    {
        if (!Configuration.PersistHistory || store == null)
            return;

        List<MemoryStore.HistoryEntry> entries = new();
        await EditHistoryAsync(thread => {
            foreach (ChatMessageContent message in thread.ChatHistory)
            {
                if (message.Role == AuthorRole.System || message.Content == null || message.Content.Trim().Length == 0)
                    continue;

                string? id = IsArchive(message) ? IdOf(message) : null;
                MemoryItem? item = id == null ? null : FindItem(id);
                long start = item?.StartTs ?? new DateTimeOffset(StampOf(message)).ToUnixTimeMilliseconds();
                long end = item?.EndTs ?? start;
                entries.Add(new MemoryStore.HistoryEntry(message.Role.Label, message.Content, item?.Level ?? 0, start, end));
            }
        }, reason);

        store.SaveHistory(entries);
    }

    static AuthorRole RoleOf(string label)
    {
        return label switch {
            "user" => AuthorRole.User,
            "system" => AuthorRole.System,
            _ => AuthorRole.Assistant
        };
    }

    async Task EditHistoryAsync(Action<ChatHistoryAgentThread> action, string reason)
    {
        await ChatBot.EditChatHistoryAsync(thread => {
            action(thread);
            return Task.CompletedTask;
        }, reason);
    }

    DateTime StampOf(ChatMessageContent message)
    {
        if (stamps.TryGetValue(message, out DateTime known))
            return known;

        // 消息过滤会把「当前时间:[...]」写进正文，优先用它；没有就用第一次看到的时刻
        Match match = TimeTag.Match(message.Content ?? "");
        DateTime stamp = match.Success
            && DateTime.TryParse(match.Groups[1].Value.Replace('T', ' '), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : DateTime.Now;
        stamps[message] = stamp;
        return stamp;
    }

    /// <summary>原版行为：压缩时租思考通道（开着质量更好、更慢），失败也不能挡住压缩</summary>
    IDisposable RentThinking()
    {
        try
        {
            string name = Configuration.ThinkingCompress ? "记忆压缩" : "记忆压缩(轻量)";
            return compressModel.GetThinkingRequester().Rent(name);
        }
        catch (Exception)
        {
            return EmptyDisposable.Instance;
        }
    }

    sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    /// <summary>原版行为：用户消息命中关键词就追加一句提示，让它想起来可以检索</summary>
    string OnChatSend(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;
        foreach (string keyword in Configuration.RecallHintKeywords)
        {
            if (keyword.Length > 0 && message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return message + "\n(提示：如果这件事你记不清了，可以 MemorySearch 检索过去的记忆)";
        }

        if (Configuration.AutoRecallHint && NeedsLookup(message))
            return message + "\n(提示：这句话跟你现在手上的记忆几乎对不上，先 MemorySearch 查一下再回答"
                + "（想按时间翻就带 when=\"上周三\" 这种说法），不要凭印象)";

        return message;
    }

    /// <summary>
    /// 用户这句话跟"卡片上写着的"重合多少。
    ///
    /// 解决的不是检索能力，是**检索意愿**：模型手上有一张卡片，看起来像全量，
    /// 于是它凭卡片回答，而答案其实躺在某条没人提过的旧存档里。
    /// 重合率低说明卡片答不了这个问题——这时候唯一该做的是去查，而不是多想一会儿。
    /// 宁可多提示一次，也不要它编一段。
    /// </summary>
    bool NeedsLookup(string message)
    {
        string text = message.Trim();
        if (text.Length < 8)
            return false;   // "好" "嗯" "继续" 这种不需要查
        if (text.Contains("先检索", StringComparison.Ordinal) || text.Contains("不要凭印象", StringComparison.Ordinal))
            return false;   // 已经提示过了（上一条被回填进历史时不该再叠一层）

        int cooldown = Math.Max(0, Configuration.AutoRecallHintCooldownSeconds);
        if (cooldown > 0)
        {
            long now = DateTimeOffset.Now.ToUnixTimeSeconds();
            if (lastHintAt > 0 && now - lastHintAt < cooldown)
                return false;
        }

        HashSet<string> wanted = MemoryTokenizer.Distinct(text)
            .Where(token => !MemoryTokenizer.IsStopWord(token))
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> have = CardTokens();
        if (wanted.Count == 0 || have.Count == 0)
            return false;

        int shared = wanted.Count(have.Contains);
        if (shared / (double)wanted.Count >= 0.2)
            return false;

        lastHintAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        return true;
    }

    /// <summary>卡片词集合按"存档 id + 长度"缓存：改一条摘要才重算，平时零成本</summary>
    HashSet<string> CardTokens()
    {
        string stamp = string.Join("|", Live().Select(item =>
            item.Id + ":" + item.Summary.Length.ToString(CultureInfo.InvariantCulture)
            + ":" + item.Facts.Length.ToString(CultureInfo.InvariantCulture)));
        if (cardTokens != null && stamp == cardStamp)
            return cardTokens;

        cardTokens = Live()
            .SelectMany(item => MemoryTokenizer.Tokens(item.Title + " " + item.Summary))
            .ToHashSet(StringComparer.Ordinal);
        cardStamp = stamp;
        return cardTokens;
    }

    /// <summary>原版行为：上下文超线只在日志里警告——不自动删，删不删由容量规则决定</summary>
    void OnTokenUsed(TokenUsageContext context)
    {
        if (context.TokenUsage.Total < Math.Max(1000, Configuration.TokenWarningThreshold))
            return;

        logger.LogWarning("[Memory] 上下文已到 {Total} tokens（警告线 {Line}）：记忆区占用 {Chars} 字。"
            + "如果继续增长，先看 MaxLevel 是否到顶导致同级摘要堆积，或把 TriggerMessages 调小让它更早压",
            context.TokenUsage.Total, Configuration.TokenWarningThreshold, CharsOf(Live()));
    }

    // ── 设置页用的读写口 ────────────────────────────────────────
    // 这些不是给模型调的，是给 MemoryServiceUI 用的。都走同一套落盘与台账，
    // 并且只要条目在上下文里，就同步改历史中的那条存档消息——否则界面上改了、模型看到的还是旧文本。

    /// <summary>
    /// 给设置页列表用：**等级从高到低**（L100 → L1），同级再按时间倒序。
    /// 顺序和官方一致——它的存档是就地插入的，历史里天然是 [L4][L3][L2][L1][L0 原文…]，
    /// 等级越高的越靠前。按时间排会把这个结构打乱，看的时候分不清哪条是被压过几轮的。
    /// </summary>
    public IReadOnlyList<MemoryItem> ListItems(int take = 300)
    {
        return items.OrderByDescending(item => item.Level)
            .ThenByDescending(item => item.StartTs)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, take))
            .ToList();
    }

    /// <summary>
    /// 找条目：完整索引、6 位短码、索引片段、标题片段都认。
    /// 模型最容易给的是短码和标题，所以这两种必须能落到唯一一条上。
    /// </summary>
    public MemoryItem? FindItem(string key)
    {
        string text = (key ?? "").Trim();
        if (text.Length == 0)
            return null;

        MemoryItem? hit = items.FirstOrDefault(item => item.Id.Equals(text, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(item => item.ShortId.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (hit != null)
            return hit;

        if (text.Length >= 6)
        {
            List<MemoryItem> byId = items.Where(item => item.Id.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count == 1)
                return byId[0];
        }

        List<MemoryItem> byTitle = items.Where(item => item.Title.Contains(text, StringComparison.Ordinal)).ToList();
        return byTitle.Count == 1 ? byTitle[0] : null;
    }

    /// <summary>给每条补短码；撞了就加长一位，保证唯一</summary>
    void EnsureShortIds()
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        int changed = 0;
        foreach (MemoryItem item in items)
        {
            string code = item.ShortId;
            if (code.Length == 0 || used.Contains(code) || !IsUniqueCode(item, code))
            {
                int length = 6;
                code = MemoryItem.MakeShortId(item.Id, length);
                while (used.Contains(code) && length < 12)
                {
                    length++;
                    code = MemoryItem.MakeShortId(item.Id, length);
                }
                changed++;
            }
            item.ShortId = code;
            used.Add(code);
        }
        if (changed > 0)
            store.Save(items);
    }

    bool IsUniqueCode(MemoryItem self, string code)
    {
        if (code.Length == 0)
            return false;
        foreach (MemoryItem other in items)
        {
            if (ReferenceEquals(other, self))
                continue;
            if (other.ShortId.Equals(code, StringComparison.OrdinalIgnoreCase))
                return false;
            // 短码还必须能从自己 Id 的哈希推出来，否则是旧数据留下的脏值
            if (!string.Equals(MemoryItem.MakeShortId(other.Id, code.Length), code, StringComparison.OrdinalIgnoreCase))
                continue;
        }
        return string.Equals(MemoryItem.MakeShortId(self.Id, code.Length), code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>库所在的目录（给「打开库存位置」用）</summary>
    public string StorePath => store?.Root ?? "";

    /// <summary>在资源管理器里打开记忆库目录</summary>
    public string OpenStoreFolder()
    {
        try
        {
            string path = StorePath;
            if (path.Length == 0)
                return "库还没初始化完，稍后再试";
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return "已在资源管理器打开：" + path;
        }
        catch (Exception ex)
        {
            return "打开失败：" + ex.Message + "（路径：" + StorePath + "）";
        }
    }

    /// <summary>在资源管理器里选中这条的原文文件</summary>
    public string RevealArchive(string id)
    {
        MemoryItem? item = FindItem(id);
        if (item == null)
            return "找不到这条记忆";
        if (item.ArchiveRel.Length == 0)
            return "这条没有原文文件（摘要还在，原文被删过）";

        string path = store.ArchivePath(item);
        if (path.Length == 0 || !File.Exists(path))
            return "原文文件不在了：" + item.ArchiveRel;

        try
        {
            // explorer 的 /select 参数要求路径带引号，否则空格会被截断
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            return "已定位：" + item.ArchiveRel;
        }
        catch (Exception ex)
        {
            return "定位失败：" + ex.Message + "（路径：" + path + "）";
        }
    }

    /// <summary>读原文（看原文按钮）</summary>
    public string? ReadOriginal(string id, int maxChars = 20000)
    {
        MemoryItem? item = FindItem(id);
        return item == null ? null : store.ReadArchive(item, maxChars);
    }

    /// <summary>改标题 / 摘要 / 重要度 / 钉住</summary>
    public async Task<string> SaveItemAsync(string id, string title, string summary, double importance, bool pinned)
    {
        return await Guarded(() => SaveItemCoreAsync(id, title, summary, importance, pinned));
    }

    async Task<string> SaveItemCoreAsync(string id, string title, string summary, double importance, bool pinned)
    {
        MemoryItem? item = FindItem(id);
        if (item == null)
            return "找不到这条记忆（可能已被删除）";

        string text = (summary ?? "").Trim();
        if (text.Length == 0)
            return "摘要不能为空";

        item.Title = MemoryStore.CleanTitle(string.IsNullOrWhiteSpace(title)
            ? MemoryStore.FirstLine(text)
            : title);
        if (item.Title.Length == 0)
            item.Title = MemoryStore.CleanTitle(MemoryStore.FirstLine(text));
        item.Summary = text;
        item.Importance = Math.Clamp(importance, 0, 1);
        item.Pinned = pinned;

        indexer.Build(items);
        store.Save(items);
        store.Ledger("edit", item, "设置页手动编辑");

        if (item.InContext)
        {
            await EditHistoryAsync(thread => ReplaceArchiveMessage(thread, item), "更新记忆存档");
            await SaveHistoryAsync("保存对话历史");
        }
        return "已保存：" + item.Title;
    }

    /// <summary>移出 / 放回上下文。放回时按等级插回阶梯里，不会乱序</summary>
    public async Task<string> ToggleContextAsync(string id)
    {
        return await Guarded(() => ToggleContextCoreAsync(id));
    }

    async Task<string> ToggleContextCoreAsync(string id)
    {
        MemoryItem? item = FindItem(id);
        if (item == null)
            return "找不到这条记忆";

        if (item.InContext)
        {
            item.InContext = false;
            store.Ledger("archive", item, "设置页移出上下文");
            store.Save(items);
            await EditHistoryAsync(thread => RemoveArchiveMessage(thread, item.Id), "移出上下文");
            await SaveHistoryAsync("保存对话历史");
            return "已移出上下文，原文还在库中可检索";
        }

        item.InContext = true;
        store.Ledger("restore", item, "设置页放回上下文");
        store.Save(items);
        await EditHistoryAsync(thread => InsertArchiveMessage(thread, item), "放回上下文");
        await SaveHistoryAsync("保存对话历史");
        return "已放回上下文";
    }

    /// <summary>删除条目。原文文件挪到 tiers/deleted/ 而不是直接删——误删还能捞回来</summary>
    public async Task<string> DeleteItemAsync(string id)
    {
        return await Guarded(() => DeleteItemCoreAsync(id));
    }

    async Task<string> DeleteItemCoreAsync(string id)
    {
        MemoryItem? item = FindItem(id);
        if (item == null)
            return "找不到这条记忆";

        store.Ledger("delete", item, "设置页手动删除");
        string archived = store.ParkArchive(item);
        items.Remove(item);
        indexer.Build(items);
        store.Save(items);
        await EditHistoryAsync(thread => RemoveArchiveMessage(thread, id), "删除记忆");
        await SaveHistoryAsync("保存对话历史");

        return archived.Length > 0
            ? "已删除（原文已移到 " + archived + "，需要可手动恢复）"
            : "已删除（这条没有原文文件）";
    }

    /// <summary>
    /// 手动新增一条（对应官方的 Memorize：等级 / 名字 / 简述 / 内容）。
    /// 简述会一直显示在上下文里，内容写进 L{等级}/{索引}.txt，只有 MemoryRecall 才看得到。
    /// 索引按官方格式生成：`{等级}-{起}-{止}`，同一秒重复就往后顺延一秒，保证文件不撞名。
    /// </summary>
    public async Task<string> CreateItemAsync(int level, string name, string title, string synopsis, string content,
        double importance, bool pinned, bool intoContext)
    {
        return await Guarded(() => CreateItemCoreAsync(level, name, title, synopsis, content, importance, pinned, intoContext));
    }

    async Task<string> CreateItemCoreAsync(int level, string name, string title, string synopsis, string content,
        double importance, bool pinned, bool intoContext)
    {
        string summary = (synopsis ?? "").Trim();
        if (summary.Length == 0)
            return "简述不能为空——它会一直显示在上下文里，内容才是存到外盘的详细部分";

        int useLevel = level > 0 ? Math.Max(1, level) : Math.Max(1, Configuration.MaxLevel);
        DateTime at = DateTime.Now;
        string id = BuildIndex(useLevel, name, at);
        for (int n = 0; items.Any(item => item.Id == id) && n < 120; n++)
        {
            at = at.AddSeconds(1);
            id = BuildIndex(useLevel, name, at);
        }

        MemoryItem created = new() {
            Id = id,
            ShortId = MemoryItem.MakeShortId(id),
            Level = useLevel,
            Title = MemoryStore.CleanTitle(string.IsNullOrWhiteSpace(title) ? MemoryStore.FirstLine(summary) : title),
            Summary = summary,
            StartTs = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            EndTs = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
            Importance = Math.Clamp(importance, 0, 1),
            Pinned = pinned || useLevel >= 100,   // 永久层默认钉住
            InContext = intoContext,
            CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
            Source = "manual",
            MsgCount = 1
        };

        items.Add(created);
        indexer.Add(created);
        store.WriteArchive(created, (content ?? "").Trim().Length > 0 ? content.Trim() : summary);
        store.Ledger("manual", created, "设置页新增");
        store.Save(items);

        if (intoContext)
        {
            await EditHistoryAsync(thread => InsertArchiveMessage(thread, created), "新增记忆");
            await SaveHistoryAsync("保存对话历史");
        }

        await EnforcePermanentBudgetAsync();
        return "已新增 " + created.ShortId + "（" + created.Title + "）";
    }

    /// <summary>
    /// 索引就是文件名：`{等级}-{名称}`。官方 UI 让用户直接填这两段，
    /// 名称留空才退回时间戳（自动压缩用的就是时间戳形式）。
    /// 名称里的文件名非法字符要替换掉，否则写文件会炸。
    /// </summary>
    static string BuildIndex(int level, string name, DateTime at)
    {
        string prefix = level.ToString(CultureInfo.InvariantCulture) + "-";
        string body = (name ?? "").Trim();
        if (body.Length > 0)
        {
            foreach (char bad in Path.GetInvalidFileNameChars())
                body = body.Replace(bad, '_');
            body = body.Replace('/', '_').Replace('\\', '_').Trim('.', ' ');
            if (body.Length > 0)
                return prefix + MemoryStore.Trim(body, 60);
        }

        string stamp = at.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return prefix + stamp + "-" + stamp;
    }

    /// <summary>
    /// 和自动压缩互斥：压缩在跑的时候界面先别动库，否则 items 会被两边同时改。
    /// 用 Wait(0) 而不是排队——等下去界面会卡住，直接回一句更清楚。
    /// </summary>
    async Task<string> Guarded(Func<Task<string>> action)
    {
        if (!await gate.WaitAsync(0))
            return "正在压缩/整理中，稍后再点一次";
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 永久记忆（L100+）也要有上限。没有上限的话它只进不出，几年下来就是几万字常驻上下文。
    /// 超限只做「移出上下文」——条目、原文、钉住状态全保留，随时能放回来或检索到。
    /// </summary>
    async Task EnforcePermanentBudgetAsync()
    {
        List<MemoryItem> perms = items
            .Where(item => item.Level >= 100 && item.InContext)
            .OrderByDescending(item => item.Importance)
            .ThenByDescending(item => item.StartTs)
            .ToList();
        if (perms.Count == 0)
            return;

        int maxItems = Configuration.PermanentMaxItems;
        int maxChars = Configuration.PermanentMaxChars;
        int chars = perms.Sum(item => item.Render().Length + 1);
        bool overCount = maxItems > 0 && perms.Count > maxItems;
        bool overChars = maxChars > 0 && chars > maxChars;
        if (!overCount && !overChars)
            return;

        HashSet<string> keep = perms.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        if (Configuration.PermanentTidyByModel)
        {
            HashSet<string>? picked = await AskKeepFromModelAsync(perms, maxItems, maxChars);
            if (picked != null && picked.Count > 0)
                keep = picked;
        }

        // 模型没给、或者给多了，就按重要度+新旧补齐裁掉
        List<MemoryItem> drop = perms.Where(item => !keep.Contains(item.Id)).ToList();
        if (drop.Count == 0 && overCount)
            drop = perms.Skip(Math.Max(1, maxItems)).ToList();
        if (drop.Count == 0 && overChars)
        {
            int running = chars;
            foreach (MemoryItem item in perms.AsEnumerable().Reverse())
            {
                if (running <= maxChars)
                    break;
                drop.Add(item);
                running -= item.Render().Length + 1;
            }
        }
        if (drop.Count == 0)
            return;

        foreach (MemoryItem item in drop)
        {
            item.InContext = false;
            store.Ledger("archive-permanent", item, "永久记忆超出常驻上限，移出上下文（未删除）");
        }
        store.Save(items);
        await SaveHistoryAsync("整理永久记忆");
        await EditHistoryAsync(thread => {
            foreach (MemoryItem item in drop)
                RemoveArchiveMessage(thread, item.Id);
        }, "整理永久记忆");

        logger.LogInformation("[Memory] 永久记忆超限：移出 {Count} 条（{Items} 条 / {Chars} 字，上限 {MaxItems} 条 / {MaxChars} 字）",
            drop.Count, perms.Count, chars, maxItems, maxChars);
    }

    /// <summary>让模型挑必须保留的永久记忆，返回选中的 Id 集合；失败返回 null 由调用方兜底</summary>
    async Task<HashSet<string>?> AskKeepFromModelAsync(List<MemoryItem> perms, int maxItems, int maxChars)
    {
        try
        {
            StringBuilder list = new();
            foreach (MemoryItem item in perms)
            {
                list.Append(item.ShortId).Append(" [").Append(item.Importance.ToString("0.00", CultureInfo.InvariantCulture))
                    .Append("] ").Append(item.Title).Append(" — ")
                    .AppendLine(MemoryStore.Trim(MemoryStore.Flatten(item.Summary), 80));
            }

            ChatHistory prompt = new();
            prompt.AddSystemMessage("你在整理长期记忆。只输出短码，一行一个，不要解释。");
            prompt.AddUserMessage(
                "以下是必须常驻的永久记忆，但总量超了。请挑出最该留的（其余会被移出上下文，不删除、以后还能检索到）。\n"
                + "上限：" + (maxItems > 0 ? "最多 " + maxItems + " 条" : "条数不限")
                + (maxChars > 0 ? "，合计不超过 " + maxChars + " 字" : "") + "。\n"
                + "取舍原则：约定、原则、身份与关系 > 一次性事件；同等重要时留新的。\n\n" + list);

            ChatResult result = await compressModel.ChatStreamingAsync(
                new ChatHistoryAgentThread(prompt), cancellationToken: DestroyCancellationToken);
            if (result.Exception != null || string.IsNullOrWhiteSpace(result.AIMessage))
                return null;

            HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
            foreach (string line in result.AIMessage.Split('\n'))
            {
                string code = line.Trim().Trim('-', '*', '·', ' ');
                if (code.Length == 0)
                    continue;
                MemoryItem? hit = perms.FirstOrDefault(item => item.ShortId.Equals(code, StringComparison.OrdinalIgnoreCase));
                if (hit != null)
                    keep.Add(hit.Id);
            }
            return keep.Count > 0 ? keep : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    void ReplaceArchiveMessage(ChatHistoryAgentThread thread, MemoryItem item)
    {
        string key = KeyOf(item.Id);
        for (int i = 0; i < thread.ChatHistory.Count; i++)
        {
            if (!IsArchive(thread.ChatHistory[i]))
                continue;
            string? id = IdOf(thread.ChatHistory[i]);
            if (id == null || !(string.Equals(id, item.Id, StringComparison.Ordinal)
                                || string.Equals(KeyOf(id), key, StringComparison.Ordinal)))
                continue;
            thread.ChatHistory[i] = new ChatMessageContent(AuthorRole.Assistant, item.Render());
            return;
        }
        InsertArchiveMessage(thread, item);
    }

    void RemoveArchiveMessage(ChatHistoryAgentThread thread, string id)
    {
        // 按归一化钥匙也认一次：历史里可能是旧写法的索引，按 id 精确比较会漏删
        string key = KeyOf(id);
        for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
        {
            if (!IsArchive(thread.ChatHistory[i]))
                continue;
            string? mid = IdOf(thread.ChatHistory[i]);
            if (mid == null)
                continue;
            if (string.Equals(mid, id, StringComparison.Ordinal)
                || string.Equals(KeyOf(mid), key, StringComparison.Ordinal))
                thread.ChatHistory.RemoveAt(i);
        }
    }

    /// <summary>按等级插回：放在第一个等级更高的存档之前；都很低就放在原文（L0）之前</summary>
    void InsertArchiveMessage(ChatHistoryAgentThread thread, MemoryItem item)
    {
        int insertAt = -1;
        for (int i = 0; i < thread.ChatHistory.Count; i++)
        {
            if (IsArchive(thread.ChatHistory[i]) && LevelOf(thread.ChatHistory[i]) > item.Level)
            {
                insertAt = i;
                break;
            }
        }
        if (insertAt < 0)
        {
            // 没有更高等级：插在最后一条存档之后、第一条原文之前
            for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
            {
                if (IsArchive(thread.ChatHistory[i]))
                {
                    insertAt = i + 1;
                    break;
                }
            }
            if (insertAt < 0)
                insertAt = thread.ChatHistory.Count;
        }
        thread.ChatHistory.Insert(insertAt, new ChatMessageContent(AuthorRole.Assistant, item.Render()));
    }

    static int LevelOf(ChatMessageContent message)
    {
        string? id = IdOf(message);
        if (id == null)
            return 0;
        int dash = id.IndexOf('-');
        return dash > 0 && int.TryParse(id[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) ? level : 0;
    }

    /// <summary>
    /// 存档索引的"同一性"：把新旧两种写法归一化到同一把钥匙。
    ///
    /// 早期版本写进历史的是 `{等级}-{yyyyMMdd}-{HHmm}-{尾号}`（`4-20261104-1314-8966`），
    /// 后来 `MemoryItem.NewId` 改成 `m-{yyyyMMdd}-{HHmm}-{尾号}`。同一条记忆因此有两个身份证：
    /// 删除时按新 id 找不到旧消息，那条记忆就永远留在上下文里——用户看到的就是"删了还在"。
    /// 压缩产出的是 `{等级}-{14位起}-{14位止}`，没有尾号，原样返回，不会被误认成同一条。
    /// </summary>
    static string KeyOf(string id) => MemoryItem.KeyOf(id);

    // ── 工具 ────────────────────────────────────────────────────

    [XmlFunction(FunctionMode.OneShot)]
    [Description("看记忆库现状：多少条、多少在上下文里、各级分布、记忆区占用字数、库里还有多少归档可检索")]
    public async Task MemoryStatus()
    {
        List<MemoryItem> live = Live().ToList();
        string levels = string.Join(" / ", items.Where(item => item.InContext).GroupBy(item => item.Level).OrderBy(group => group.Key)
            .Select(group => "L" + group.Key + " " + group.Count() + " 条"));

        // L0 = 还没压的原文消息。原版就是这么编号的（0 是原话，1 是第一层摘要）
        (int rawCount, int rawTagged) = await CountRawAsync();

        StringBuilder text = new();
        text.Append("记忆库：").Append(items.Count).Append(" 条（上下文 ").Append(live.Count)
            .Append(" 条，归档 ").Append(items.Count - live.Count).Append(" 条）")
            .Append("\n  上下文里的阶梯：L0 ").Append(rawCount).Append(" 条原文")
            .Append(levels.Length > 0 ? " / " + levels : "（还没压过）")
            .Append("\n  记忆区占用 ").Append(CharsOf(live)).Append(" / ").Append(Configuration.MaxBlockChars).Append(" 字")
            .Append("\n  全库等级分布：").Append(
                string.Join(" / ", items.GroupBy(item => item.Level).OrderBy(group => group.Key)
                    .Select(group => "L" + group.Key + " " + group.Count())))
            .Append("\n  时间戳：").Append(rawTagged).Append(" / ").Append(rawCount)
            .Append(rawTagged < rawCount
                ? "（不全——消息过滤按 TimestampInterval 节流注入，密集对话本来就只有少数几条带，正常；没带的按到达时刻算）"
                : "（齐全）")
            .Append("\n  永久层常驻：").Append(items.Count(item => item.Level >= 100 && item.InContext))
            .Append(" / ").Append(Configuration.PermanentMaxItems > 0 ? Configuration.PermanentMaxItems.ToString(CultureInfo.InvariantCulture) : "不限").Append(" 条")
            .Append("\n  已作废（结论被新记忆取代）：").Append(items.Count(item => item.SupersededBy.Length > 0)).Append(" 条")
            .Append("\n  归纳（规律/信念）：").Append(patterns.Count(pattern => pattern.Live)).Append(" 条现行")
            .Append(patterns.Count > 0 ? "（共 " + patterns.Count + " 条，含已失效）" : "（还没归纳过）")
            .Append("\n  带当时状态的存档：").Append(items.Count(item => item.StateMood.HasValue)).Append(" / ").Append(items.Count)
            .Append("\n  检索词条：").Append(indexer.TokenCount)
            .Append("（原文可检索 ").Append(ArchiveCount).Append(" 篇）")
            .Append("\n  原文锚点：").Append(items.Count(item => item.Facts.Length > 0))
            .Append(" 条（摘要没写、但能靠它查回来的路径/版本/错误码）")
            .Append("\n  被检索过：").Append(items.Count(item => item.Hits > 0)).Append(" 条");
        interactor.Poke(text.ToString());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("检索全部记忆（含已移出上下文的归档）。query 找内容，when 按时间翻旧账（昨天/上周三/三天前/上个月/10月31日），两个可以只给一个。摘要里没写但原文有的内容也能搜到，并带出证据片段。短码给 MemoryRecall 取原文")]
    public async Task MemorySearch(
        [Description("关键词，中文直接写；只想按时间翻就留空")] string query = "",
        [Description("只看某一等级，0 = 全部")] int level = 0,
        [Description("返回几条，0 = 用配置默认")] int take = 0,
        [Description("只看最近 N 天，0 = 不限；可以和 when 叠加")] int sinceDays = 0,
        [Description("连这一轮已经给过的也返回，默认 false 只给新情报")] bool allowSeen = false,
        [Description("时间说法：昨天 / 昨晚 / 前天 / 上周三 / 上周 / 三天前 / 上个月 / 10 月 31 日 / 最近 7 天，留空 = 不限")] string when = "")
    {
        int limit = take > 0 ? take : Configuration.SearchTake;

        MemoryText.TimeQuery? range = null;
        if (!string.IsNullOrWhiteSpace(when))
        {
            range = MemoryText.ParseWhen(when, DateTime.Now);
            if (range == null)
            {
                interactor.Poke("没看懂「" + when.Trim() + "」是哪段时间。能认：今天 / 昨天 / 昨晚 / 前天 / 上周三 / 上周 / 三天前 / 上个月 / 10 月 31 日 / 最近 7 天");
                return;
            }
        }

        long since = sinceDays > 0
            ? DateTimeOffset.Now.AddDays(-sinceDays).ToUnixTimeMilliseconds()
            : 0;

        // 情绪偏置：此刻心情离某条存档"当时的心情"越近，越容易被想起。
        // 只改排序，不增不删——检索的召回边界还是关键词说了算。
        Func<MemoryItem, double>? bias = Configuration.MoodAwareSearch
            ? MoodMirror.BiasFor(MoodMirror.Read(Character.StorageKey), Configuration.MoodBiasStrength)
            : null;

        int fetch = Math.Max(limit * 4, limit);
        Func<MemoryItem, bool> filter = item => (level <= 0 || item.Level == level) && (since == 0 || item.EndTs >= since);

        List<MemoryHit> Search(string text) => indexer.Search(
            items,
            text,
            fetch,
            filter,
            range,
            Configuration.SearchArchive ? item => store.ReadArchive(item, 0) : null,
            Configuration.SearchArchive,
            bias);

        List<MemoryHit> all = Search(query ?? "");

        // 字面搜不到才有这一层：二元组匹配对同义词是瞎的（搜「记忆」找不到「记性」）。
        // 放在"结果不够"之后，是为了不让每次检索都多付一次模型调用。
        if (Configuration.QueryExpand && (query ?? "").Trim().Length > 0 && all.Count < limit)
        {
            foreach (string alternate in await ExpandQueryAsync(query!))
            {
                foreach (MemoryHit hit in Search(alternate))
                {
                    if (all.All(existing => existing.Item.Id != hit.Item.Id))
                        all.Add(hit);
                }
            }
            all = all.OrderByDescending(hit => hit.Score).ThenByDescending(hit => hit.Item.EndTs).ToList();
        }

        int skipped = 0;
        List<MemoryHit> hits;
        if (Configuration.SearchOnlyNew && !allowSeen)
        {
            hits = all.Where(hit => !givenToModel.Contains(hit.Item.ShortId)).Take(limit).ToList();
            skipped = all.Count - hits.Count;
            if (hits.Count == 0 && all.Count > 0)
            {
                interactor.Poke("「" + Describe(query, when) + "」命中 " + all.Count + " 条，但都已经给过你了。"
                    + "要看再说一次 allowSeen=true，或者直接 MemoryRecall 短码取原文");
                return;
            }
        }
        else
        {
            hits = all.Take(limit).ToList();
        }

        if (hits.Count == 0)
        {
            // 说清边界。不说的话，"查不到"和"不存在"在模型眼里是一回事，
            // 它就会把"我没有"讲成"从来没有"。
            interactor.Poke("没搜到「" + Describe(query, when) + "」。"
                + "换个说法、去掉等级/时间限制再试；" + Coverage() + "。确实没有就说没有，不要编");
            return;
        }

        long nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        StringBuilder report = new("搜到 " + hits.Count + " 条"
            + (skipped > 0 ? "（另有 " + skipped + " 条已给过，略过）" : "")
            + (range != null ? "（时间：" + range.Value.Label + "）" : "")
            + "。短码给 MemoryRecall 就能读原文：");
        foreach (MemoryHit hit in hits)
        {
            MemoryItem item = hit.Item;
            givenToModel.Add(item.ShortId);
            item.Hits++;
            item.LastHitTs = nowMs;

            report.Append("\n").Append(item.ShortId)
                .Append(" L").Append(item.Level)
                .Append(' ').Append(item.StartLocal.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))
                .Append(' ').Append(item.Title);
            if (item.SupersededBy.Length > 0)
                report.Append("（已被后来的存档取代，结论可能过时）");
            else if (item.InContext)
                // 去重：这条摘要此刻就摆在上下文里，模型看得见。标出来，
                // 免得它把"搜到"当成"又想起一件新事"，或者重复取一遍原文。
                report.Append("〔摘要已在上下文〕");

            if (hit.InArchive && hit.Excerpt.Length > 0)
            {
                // 命中在原文、不在摘要：直接把证据摆出来，省掉一次 Recall
                report.Append("\n    ").Append(hit.Excerpt);
            }
            else if (Configuration.SearchExcerptChars > 0)
            {
                report.Append(" — ").Append(MemoryStore.Trim(MemoryStore.Flatten(item.Summary), Configuration.SearchExcerptChars));
            }
        }
        if (hits.Any(hit => hit.Item.InContext && hit.Item.SupersededBy.Length == 0))
            report.Append("\n（标〔摘要已在上下文〕的几条，摘要你手上就有，不必再说一遍；只有要原文时才 MemoryRecall）");

        report.Append("\n").Append(Coverage());
        store.Save(items);   // 记下命中次数（激活度用），一次检索一次落盘
        interactor.Poke(report.ToString());
    }

    static string Describe(string? query, string? when)
    {
        string q = (query ?? "").Trim();
        string w = (when ?? "").Trim();
        if (q.Length == 0 && w.Length > 0)
            return w;
        if (w.Length == 0)
            return q;
        return q + " " + w;
    }

    /// <summary>
    /// 检索到底覆盖了什么。dsh 的记忆骨架里最值得抄的一条：
    /// 卡片不写边界，模型就会把"我这儿没有"当成"这事没发生过"。
    /// </summary>
    string Coverage()
    {
        if (items.Count == 0)
            return "库里还没有任何记忆";
        int withArchive = items.Count(item => item.ArchiveRel.Length > 0);
        long earliest = items.Min(item => item.StartTs);
        string from = earliest > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(earliest).LocalDateTime.ToString("MM-dd", CultureInfo.InvariantCulture)
            : "?";
        return "(检索范围：摘要 " + items.Count + " 条 + 原文 " + withArchive + " 篇，最早 " + from + ")";
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("看她从多次经历里归纳出的规律与信念（每条都带证据存档）。问「我一般怎么对待…」「你为什么这么想」「这是第几次了」时用这个。留空看全部")]
    public void MemoryBeliefs(
        [Description("只查某一主题，留空 = 全部")] string theme = "",
        [Description("连已失效（被推翻）的也列出来，默认 false")] bool includeRetired = false)
    {
        List<MemoryPattern> pool = patterns
            .Where(pattern => includeRetired || pattern.Live)
            .Where(pattern => theme.Trim().Length == 0
                || pattern.Theme.Contains(theme.Trim(), StringComparison.OrdinalIgnoreCase)
                || pattern.Statement.Contains(theme.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pattern => pattern.Live)
            .ThenByDescending(pattern => pattern.Evidence.Count)
            .ToList();

        int live = patterns.Count(pattern => pattern.Live);

        if (pool.Count == 0)
        {
            // 说清"还没归纳"和"归纳了但没有"的区别，否则模型会把"没有"讲成"从来没这回事"
            interactor.Poke(patterns.Count == 0
                ? "还没有归纳出任何规律——要么存档太少（不到 4 条），要么每件事都只发生过一次。"
                  + "可以让我用 MemoryReflect 当场归纳一次试试"
                : "共 " + live + " 条现行归纳，但没有匹配「" + theme.Trim() + "」的。换个说法，或者留空看全部");
            return;
        }

        StringBuilder report = new("归纳库里 " + live + " 条现行" + (includeRetired ? "（含已失效）" : "") + "：\n");

        // 按八章分组。骨架是固定的，所以顺序也固定——她每次看到的"我是谁"都在同一处
        List<string> chapters = Configuration.ReflectChapters.ToList();
        foreach (string chapter in chapters)
        {
            List<MemoryPattern> inChapter = pool.Where(pattern => pattern.Chapter == chapter).Take(6).ToList();
            if (inChapter.Count == 0)
                continue;

            report.Append('\n').Append("【").Append(chapter).Append("】").AppendLine();
            foreach (MemoryPattern pattern in inChapter)
                AppendPattern(report, pattern);
        }

        // 老数据没有章名，单独放在最后，别混进骨架里
        List<MemoryPattern> unfiled = pool.Where(pattern => !chapters.Contains(pattern.Chapter)).Take(6).ToList();
        if (unfiled.Count > 0)
        {
            report.Append('\n').Append("【未归章】").AppendLine();
            foreach (MemoryPattern pattern in unfiled)
                AppendPattern(report, pattern);
        }

        report.Append("\n这些是从多次经历里归纳出来的，不是此刻的想法。真要引用，说「我以前好像是这样」比说「我就是这样」准。");
        interactor.Poke(report.ToString());
    }

    [Description("现在就把记忆归纳一次：从最近的存档里找反复出现的模式，形成规律/信念。平时攒够会自动做，这个用于手动触发")]
    public async Task MemoryReflect()
    {
        int changed = await ReflectAsync(true);
        interactor.Poke(changed > 0
            ? "归纳完成：新增或更新了 " + changed + " 条规律/信念。想看就说 MemoryBeliefs"
            : "这轮没有归纳出新的东西——多半是证据还不够（同一类事至少要出现两次）");
    }

    /// <summary>一条归纳的展示：主题 + 一句话 + 证据与把握</summary>
    void AppendPattern(StringBuilder report, MemoryPattern pattern)
    {
        report.Append("　").Append(pattern.KindWord).Append('｜').Append(pattern.Theme)
            .Append('：').AppendLine(pattern.Statement);
        report.Append("　　证据 ").Append(pattern.Evidence.Count).Append(" 条　把握 ")
            .Append((int)Math.Round(pattern.Confidence * 100)).Append('%')
            .Append("　").Append(DateTimeOffset.FromUnixTimeMilliseconds(pattern.UpdatedAt)
                .LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (!pattern.Live)
            report.Append("　（已被后来的归纳取代，别当成现在的想法）");
        report.AppendLine();
        report.Append("　　依据：").AppendLine(string.Join(" / ", pattern.Evidence.Take(3).Select(ShortOf)));
    }

    /// <summary>把存档 id 显示成短码，归纳报告里引用证据用</summary>
    string ShortOf(string id)
        => items.FirstOrDefault(item => item.Id == id)?.ShortId ?? id;

    [Description("想起一件旧事：从记忆里挑一条最久没被想起的（不是检索，是它自己浮上来），带摘要和当时的原文片段")]
    public async Task MemoryReminisce()
    {
        await DoRecallAsync();
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("按索引取某条记忆的完整原文（压缩前的对话记录）。索引从 MemorySearch 或上下文里的存档标签拿到")]
    public void MemoryRecall([Description("记忆索引，形如 3-20261031141657-20261101054025")] string index)
    {
        string key = (index ?? "").Trim();
        MemoryItem? item = FindItem(key);
        if (item == null)
        {
            interactor.Poke("没有索引为 " + key + " 的记忆。先用 MemorySearch 找一下");
            return;
        }

        // 被取出来读过，就是"这条还活着"的证据。淘汰顺序靠它（见 MemoryActivation）
        item.Hits++;
        item.LastHitTs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        store.Save(items);

        string? content = store.ReadArchive(item, Math.Max(500, Configuration.RecallMaxChars));
        if (content == null)
        {
            interactor.Poke("这条记忆只剩摘要（原文文件不在了）：\n" + item.Summary
                + (item.Facts.Length > 0 ? "\n[原文锚点] " + item.Facts : ""));
            return;
        }

        string lead = item.InContext ? "（这条的摘要已在上下文里，下面给的是原文）\n" : "";
        interactor.Poke(TagStart + item.Id + "]\n" + lead + content);
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("主动记一件事。适合约定、原则、人物设定这类必须长期记住的东西；会以存档形式写进上下文，pinned=true 则永不被移出")]
    public async void MemoryRemember(
        XmlExecutorContext ctx,
        [Description("标题，不超过 20 字")] string title,
        [Description("等级，0 = 用配置的最高级；想让它比别的更靠前（更晚被移出）就填更大的数，如 100")] int level,
        [Description("重要度 0~1，0.85 以上是必须记住的")] double importance,
        [Description("是否钉住（永不移出上下文）")] bool pinned,
        [XmlContent] string content)
    {
        try
        {
            if (ctx.CallMode != CallMode.Closing)
                return;

            string body = (ctx.FullContent ?? content ?? "").Trim();
            if (body.Length == 0)
            {
                interactor.Poke("内容为空，没记");
                return;
            }

            int useLevel = level > 0 ? Math.Max(1, level) : Math.Max(1, Configuration.MaxLevel);
            DateTime now = DateTime.Now;
            MemoryDraft draft = new(
                MemoryStore.Trim(string.IsNullOrWhiteSpace(title) ? MemoryStore.FirstLine(body) : title, 40),
                MemoryStore.Trim(body, 2000),
                Math.Clamp(importance <= 0 ? 0.8 : importance, 0, 1));

            MemoryItem item = NewItem(useLevel, draft, now, now, 0, body);
            item.Pinned = pinned;
            item.Source = "manual";
            items.Add(item);
            indexer.Add(item);
            store.Save(items);

            await EditHistoryAsync(thread => {
                thread.ChatHistory.Insert(PositionFor(thread, item), new ChatMessageContent(AuthorRole.Assistant, item.Render()));
            }, "记一条记忆");
            await EnforceCapacityAsync();
            interactor.Poke("记住了：" + item.Id + "「" + item.Title + "」" + (pinned ? "（已钉住）" : ""));
        }
        catch (Exception e)
        {
            logger.LogError(e, "[Memory] 写入记忆失败");
            interactor.Poke("没记住：" + e.Message);
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("删除某条记忆（会先写台账再从上下文移除，原文文件保留在磁盘）")]
    public async Task MemoryForget([Description("记忆索引")] string index)
    {
        string key = (index ?? "").Trim();
        MemoryItem? item = items.FirstOrDefault(candidate => candidate.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            interactor.Poke("没有索引为 " + key + " 的记忆");
            return;
        }

        store.Ledger("forget", item, "模型主动遗忘");
        item.InContext = false;
        items.Remove(item);
        indexer.Forget(item.Id);
        store.Save(items);

        await EditHistoryAsync(thread => {
            for (int i = thread.ChatHistory.Count - 1; i >= 0; i--)
            {
                if (IsArchive(thread.ChatHistory[i]) && string.Equals(IdOf(thread.ChatHistory[i]), item.Id, StringComparison.Ordinal))
                    thread.ChatHistory.RemoveAt(i);
            }
        }, "删除记忆");
        interactor.Poke("已删除「" + item.Title + "」（原文仍留在 archive 目录）");
    }
}
