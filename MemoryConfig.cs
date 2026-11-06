using System.Collections.Generic;
using System.ComponentModel;

namespace Suran.Memory;

/// <summary>
/// 配置。原则：每一项都必须真的有人读（这份里没有"留着以后用"的字段）。
/// 全部改完需要重载角色才生效——压缩阈值这类参数只在下次触发时读，不存在热更。
/// </summary>
public record MemoryConfig
{
    // ── 抓取与压缩 ──────────────────────────────────────────────

    [DisplayName("抓取·启用")]
    [Description("关掉就完全不压缩（已有存档仍在上下文里，工具仍可用）")]
    public bool CaptureEnabled { get; set; } = true;

    [DisplayName("压缩·触发条数")]
    [Description("未压原文超过多少条触发压缩，一问一答算 2 条。")]
    public int TriggerMessages { get; set; } = 120;

    [DisplayName("压缩·每批条数")]
    [Description("每次压缩最多处理多少条最旧的原文。")]
    public int BatchMessages { get; set; } = 100;

    [DisplayName("压缩·触发概率")]
    [Description("到了触发条数后，每轮有多大概率真的压（原版默认 0.4；填 1 = 每次必压）")]
    public float Probability { get; set; } = 0.4f;

    [DisplayName("压缩·使用思考通道")]
    [Description("压缩时租用思考通道，摘要质量更好但更慢更贵（原版默认开）")]
    public bool ThinkingCompress { get; set; } = true;

    [DisplayName("压缩·分批字数上限")]
    [Description("单次压缩最多送多少字原文，超了拆批。")]
    public int BatchMaxChars { get; set; } = 50000;

    [DisplayName("压缩·最高等级")]
    [Description("摘要最多叠到第几级。到顶之后同级的旧摘要会合并进最高级，不再升")]
    public int MaxLevel { get; set; } = 6;

    [DisplayName("升级·同级条数")]
    [Description("同一级攒够这么多条，就把它们再压成一条更高等级的摘要")]
    public int GroupSize { get; set; } = 4;

    [DisplayName("压缩·提示词")]
    [Description("留空用内置提示词（初次压缩与合并升级各一套语域）。可用 {range}（时间范围）和 {archives}（已有存档清单）两个占位；填了则两条路径都用这一份")]
    public string CompressPrompt { get; set; } = "";

    [DisplayName("压缩·注入角色人设")]
    [Description("压缩时把角色的人设语气带上，摘要按她的口吻写；关掉只用内置提示词")]
    public bool CompressPersona { get; set; } = true;

    [DisplayName("压缩·人设只取语气段")]
    [Description("只取人设里讲性格与说话方式的那一节；关掉用整段")]
    public bool CompressPersonaStyleOnly { get; set; } = true;

    [DisplayName("压缩·人设字数上限")]
    [Description("注入的人设最多多少字，默认 1500；0 = 不限")]
    public int CompressPersonaMaxChars { get; set; } = 1500;

    [DisplayName("压缩·模型类名")]
    [Description("留空 = 用当前角色的语言模型。填一个模块类名则可以指定更便宜的模型来压缩")]
    public string CompressModelClassName { get; set; } = "";

    [DisplayName("压缩·补原文锚点")]
    [Description("摘要里没提到、但猜不回来的东西（路径、版本号、命令行开关、错误码）另外附一行到存档末尾。花几十字，避免以后想找却搜不到")]
    public bool FactsEnabled { get; set; } = true;

    [DisplayName("压缩·锚点字数上限")]
    [Description("每条存档最多附多少字的原文锚点，0 = 不限")]
    public int FactsMaxChars { get; set; } = 200;

    [DisplayName("压缩·隐去密钥")]
    [Description("落盘前把 sk-/ghp_ 这类 token、password=… 、Bearer 头换成占位符。用户在聊天里贴密钥是常事，摘要和原文都会带进去")]
    public bool RedactSecrets { get; set; } = true;

    // ── 上下文容量 ──────────────────────────────────────────────

    [DisplayName("容量·存档总字数上限")]
    [Description("上下文存档总字数上限，超出把未钉住的移出上下文。")]
    public int MaxBlockChars { get; set; } = 24000;

    [DisplayName("容量·最少保留")]
    [Description("淘汰时至少保留这么多条摘要，再紧也不清空（防止刚压完就被淘汰到看不见）")]
    public int MinKeepItems { get; set; } = 4;

    [DisplayName("容量·按激活度淘汰")]
    [Description("开：常被检索到的旧记忆能顶住，刚压出来但没人提过的先走（ACT-R 遗忘曲线，纯本地计算）。关：退回旧的「重要度最低 + 最老」规则")]
    public bool ActivationEvict { get; set; } = true;

    // ── 永久记忆 ────────────────────────────────────────────────

    [DisplayName("永久·最多常驻条数")]
    [Description("L100 及以上最多几条留在上下文里。超了把不重要的移出上下文（不删除，仍可检索/放回）。0 = 不限")]
    public int PermanentMaxItems { get; set; } = 12;

    [DisplayName("永久·常驻字数上限")]
    [Description("L100 常驻部分的总字数上限，超了同样移出上下文。0 = 不限")]
    public int PermanentMaxChars { get; set; } = 4000;

    [DisplayName("永久·用模型判取舍")]
    [Description("超限时让模型决定留哪几条（keep/archive），失败或没配模型就按重要度自动取舍")]
    public bool PermanentTidyByModel { get; set; } = false;

    // ── 检索 ────────────────────────────────────────────────────

    [DisplayName("检索·默认返回条数")]
    [Description("MemorySearch 不传 take 时返回几条")]
    public int SearchTake { get; set; } = 8;

    [DisplayName("检索·只给新情报")]
    [Description("同一轮里搜过的条目不再重复返回（模型容易反复搜同一件事）。要看旧的可以让调用方传 allowSeen=true")]
    public bool SearchOnlyNew { get; set; } = true;

    [DisplayName("检索·单条摘要摘录字数")]
    [Description("检索结果每条附带的摘要摘录长度，0 = 只给短码和标题")]
    public int SearchExcerptChars { get; set; } = 60;

    [DisplayName("检索·原文单次上限字数")]
    [Description("MemoryRecall 单个存档最多回多少字，超出截断（防止一口气灌满上下文）")]
    public int RecallMaxChars { get; set; } = 4000;

    [DisplayName("检索·也扫原文")]
    [Description("关键词只在原文里出现、摘要没提到时也能搜到，命中会带出证据片段。关掉只搜摘要，更快但会漏")]
    public bool SearchArchive { get; set; } = true;

    [DisplayName("检索·原文扫描上限")]
    [Description("一次检索最多扫多少篇原文（按时间取最新的）。库很大时调小可以防卡顿")]
    public int ArchiveScanMax { get; set; } = 400;

    [DisplayName("检索·自动提醒去查")]
    [Description("用户这句话和当前记忆卡片几乎对不上时，在消息后追一句「先检索，别凭印象」。解决的是「有检索能力但想不起来用」")]
    public bool AutoRecallHint { get; set; } = true;

    [DisplayName("检索·提醒冷却秒数")]
    [Description("两次自动提醒至少隔这么久，免得每句都挂一句提示")]
    public int AutoRecallHintCooldownSeconds { get; set; } = 120;

    // ── 回忆（主动想起，不是被检索）──────────────────────────────

    [DisplayName("回忆·启用")]
    [Description("不定期从记忆里挑一条很久没被想起的旧事，自己浮上来（不是被提问才去翻）。想安静就关掉")]
    public bool RecallEnabled { get; set; } = true;

    [DisplayName("回忆·最短间隔（分钟）")]
    [Description("两次回忆之间的最短间隔。到点后还要看 AI 是否空闲，正在回复时会顺延")]
    public int RecallMinMinutes { get; set; } = 120;

    [DisplayName("回忆·最长间隔（分钟）")]
    [Description("超过这个间隔必定回忆一次；实际触发时刻在最短与最长之间随机，避免准点报时的机械感")]
    public int RecallMaxMinutes { get; set; } = 720;

    [DisplayName("回忆·每次条数")]
    [Description("每次回忆挑几条。挑的是「可得性最低」的，也就是尘封最久的")]
    public int RecallCount { get; set; } = 2;

    [DisplayName("回忆·最高层级")]
    [Description("只回忆这个等级以内的存档。L100 永久层不受此限——它是人工钉住的约定，数字大不代表更抽象")]
    public int RecallMaxLevel { get; set; } = 6;

    [DisplayName("回忆·单条冷却（小时）")]
    [Description("同一条被回忆过之后，这么多小时内不再挑它。否则就不是想起，是复读")]
    public int RecallCooldownHours { get; set; } = 24;

    [DisplayName("回忆·带当时的原文片段")]
    [Description("回忆时附一句原文里最有信息量的片段（约 180 字），比只有摘要更有温度。关掉更省字数")]
    public bool RecallWithDetail { get; set; } = true;

    // ── 星云 ────────────────────────────────────────────────────

    [DisplayName("星云·按钮距底部像素")]
    [Description("右下角「✦ 星云」按钮离窗口底部多远，默认 300——要落在状态挂件（默认 190 起、约高 84）之上，"
        + "两者叠在一起会互相压住。各自独立调")]
    public int NebulaButtonBottomPx { get; set; } = 300;

    [DisplayName("星云·按钮距右侧像素")]
    [Description("「✦ 星云」按钮离窗口右边多远，默认 14。和状态挂件各调各的，想并排摆就改其中一项")]
    public int NebulaButtonRightPx { get; set; } = 14;

    // ── 与状态插件（Suran.Mood）的连接 ────────────────────────────

    [DisplayName("状态·压缩时记下当时的心情")]
    [Description("压缩时读一次 Storage/<角色>/Mood/state.json，把心情/体力/亲近记进存档。"
        + "读不到就留空（不是记成中性）——以后归纳「她一般怎么看这件事」要用它")]
    public bool MoodAwareSearch { get; set; } = true;

    [DisplayName("状态·检索按情绪重排")]
    [Description("此刻心情离某条存档当时的心情越近，越容易被想起（难过时先想起难过的事）。"
        + "只改顺序、一条不删。0 = 不偏置；0.25（默认）= 最高 ×1.25、最低 ×0.75；0.6 已很强势")]
    public double MoodBiasStrength { get; set; } = 0.25;

    // ── 归纳（规律 / 信念）─────────────────────────────────────

    [DisplayName("归纳·启用")]
    [Description("是否从新存档中归纳规律与信念。")]
    public bool ReflectEnabled { get; set; } = true;

    [DisplayName("归纳·每攒多少条存档提炼一次")]
    [Description("默认 6。太小会把单次经历当成规律，太大则很久才更新一次")]
    public int ReflectAfterArchives { get; set; } = 6;

    [DisplayName("归纳·最多看多少条存档")]
    [Description("默认 40。给多了既贵，也容易把不相干的事硬凑成一条")]
    public int ReflectMaxArchives { get; set; } = 40;

    [DisplayName("归纳·章节骨架")]
    [Description("归纳时必须归到这八章之一，不许自由新开。骨架借自 dsh-xingxi 的记忆卡结构——"
        + "主题自由生长会碎成几十条同义条目，散主题也答不了「我是谁」")]
    public List<string> ReflectChapters { get; set; } = new()
    {
        "我是谁",
        "我是怎么说话的",
        "对方是谁",
        "我们怎么走到今天的",
        "我们的约定",
        "难忘的时刻",
        "怎么跟对方相处",
        "我们一起做的东西",
    };

    [DisplayName("归纳·用模型类名")]
    [Description("留空 = 用当前角色的模型。填一个模块类名可以指定更便宜或更强的")]
    public string ReflectModelClassName { get; set; } = "";

    // ── 检索增强 ────────────────────────────────────────────────

    [DisplayName("检索·同义扩展")]
    [Description("字面搜不到时，让小模型把关键词扩成几个近义说法再搜一遍。只在结果不够时才触发，"
        + "不会每次检索都多花一次调用")]
    public bool QueryExpand { get; set; } = true;

    [DisplayName("检索·扩展用模型类名")]
    [Description("留空 = 用当前角色的模型。填一个模块类名可以指定更便宜的")]
    public string QueryExpandModelClassName { get; set; } = "";

    // ── 告警与提示 ──────────────────────────────────────────────

    [DisplayName("告警·上下文 token 线")]
    [Description("上下文超过这个 token 数就在日志里警告（原版默认 20 万）")]
    public int TokenWarningThreshold { get; set; } = 200000;

    [DisplayName("提示·回忆关键词")]
    [Description("用户消息里出现这些词时，在其后追加一句「可以回忆往事」的提示。原版叫 RecallHintKeywords")]
    public List<string> RecallHintKeywords { get; set; } = new();

    // ── 旧数据 ──────────────────────────────────────────────────

    [DisplayName("启动·保存对话历史")]
    [Description("把整段对话存进 Memory/History.json，重启后恢复（含还没压的 L0 原文）。Alife 自己不持久化会话，关掉它重启就只剩存档摘要")]
    public bool PersistHistory { get; set; } = true;

    [DisplayName("启动·索引旧存档")]
    [Description("把官方持久记忆留下的 Memory/L{n}/*.txt 索引成条目。文件不动、幂等。两个插件不能同时启用——都挂 ChatHistoryAdd 会各压一份")]
    public bool ImportLegacy { get; set; } = true;
}
