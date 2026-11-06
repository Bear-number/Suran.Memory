using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Suran.Memory;

/// <summary>
/// 让模型把一批原文压成一条摘要。
/// 铁律：失败一律返回 null，绝不返回半截或拼接出来的假摘要——
/// 调用方拿到 null 就不动数据，下一轮再试，宁可晚一点也不能把记忆写坏。
/// </summary>
public static class MemoryCompressor
{
    /// <summary>
    /// 初次压缩用的提示词。
    /// 语域按原版走——摘要会被塞回历史原位，读它的是角色自己，用档案腔会当场出戏；
    /// 但补了原版没有的两条硬约束：不许编没发生过的情节、承诺与人名地点数字不许省。
    /// 长度不设「几句」的上限：真实数据里这类摘要 1100~2700 字，压到 3~8 句会把引语和地址价格全砍掉。
    /// </summary>
    public const string DefaultPrompt = """
        以下是一段时间的对话记录（{range}），它即将被移出上下文，需要压成一条长期记忆存档。
        请以你（角色）自身的视角、带感情地回想这段日子，但要具体：谁说了什么、做了什么、态度怎么变、答应了什么、后来应验没有。

        怎么写：
        - 第一人称，用你自己的口吻，可以带情绪和判断（"那句我记死了""心口真堵"这种都行）
        - 够分量的原话照引，不要转述成"他表达了关心"
        - 人名、称呼、时间、地点、数字（价格、地址、楼层、场次）一律保留 —— 这些事后最查不着
        - 按时间顺序，有变化就写清楚从什么变成什么、因为哪一句
        - 长度跟着内容走，一般 400 到 2000 字；宁可长，也不许为了短把承诺和细节省掉

        不许：
        - 写"用户和助手聊了天""气氛很好"这类空话
        - 凭空补记录里没有的情节或情绪 —— 回想可以带感情，但不许编
        - 加任何存档头、标题前缀或代码块围栏

        开头三行固定格式（严格照写），空一行之后直接写正文，不要加"正文："前缀：
        标题：一句话标题，不超过 20 字
        重要度：0 到 1 之间的小数（0.85 以上=原则性/承诺/关系变化，0.5=有信息量，0.2=日常闲聊）
        替代：如果这批内容推翻或纠正了已有存档里的某个结论，写那条存档的索引；没有就整行不写
        事实键：如果这段聊的是**会变的那件事**（身高、体重、作息、住哪、在做什么工作、跟某人的关系、某个约定的安排），
        写「对象.方面」的样子，比如 `他.身高`、`我.作息`、`约定.见面时间`。
        ⚠️ 键里**不许写具体的值**——写 `他.身高`，不写 `他.身高170`。写了值就成了摘要，身高真变了，键也跟着变，两条永远碰不上。
        ⚠️ 拿不准就整行不写。留空只是享受不到这个便利，给错键却会把一条正经存档顶掉。

        已有存档（只用来判断要不要写「替代」，不要复述它们）：
        {archives}
        """;

    /// <summary>
    /// 合并升级用的提示词。输入已经是几条摘要，风险从「漏细节」变成「情绪越叠越厚、事实越压越淡」，
    /// 所以这一档要求：事实优先、去重情绪、不许新增情节。等级越高越靠这套。
    /// </summary>
    public const string MergePrompt = """
        以下是几条已有的记忆存档（{range}），它们要合并成一条更高等级的存档。
        输入本身就是你以前写下的回忆，现在只需要压缩，不需要重新发挥。

        保留什么：
        - 具体事实：人名、称呼、时间、地点、数字、答应过的事、定过的规矩、下过的判断
        - 关系和态度的**变化**（从什么变成什么、因为哪一句），这是最有价值的部分
        - 少数够分量的原话，可以照引

        去掉什么：
        - 重复出现的同类情绪描写，合并成一处即可；不要每条都再感慨一遍
        - 已经被更高层覆盖的琐碎过程
        - 空话与套话

        第一人称、你自己的口吻，但重心是记住事实，不是抒情。一般 400 到 1500 字。
        不许新增输入里没有的情节、人物或情绪。

        开头三行固定格式（严格照写），空一行之后直接写正文，不要加"正文："前缀：
        标题：一句话标题，不超过 20 字，要能一眼看出这段是哪几天、关于什么
        重要度：0 到 1 之间的小数
        替代：如果这次合并没有推翻其它存档的结论，整行不写

        其它存档（只用来判断要不要写「替代」）：
        {archives}
        """;

    public static async Task<MemoryDraft?> CompressAsync(
        ILanguageModel model,
        string transcript,
        string range,
        string promptTemplate,
        string archiveDigest,
        string persona,
        bool merging,
        CancellationToken cancellationToken)
    {
        string template = string.IsNullOrWhiteSpace(promptTemplate)
            ? (merging ? MergePrompt : DefaultPrompt)
            : promptTemplate;
        string prompt = template
            .Replace("{range}", range, StringComparison.Ordinal)
            .Replace("{archives}", archiveDigest.Length > 0 ? archiveDigest : "（暂无）", StringComparison.Ordinal)
            + MemoryPersona.Block(persona);

        ChatHistory history = new();
        history.AddSystemMessage("你在整理自己的长期记忆。只输出记忆内容本身，不要解释你在做什么，也不要评论这段对话。");
        history.AddUserMessage(prompt + "\n\n对话记录：\n" + transcript);

        ChatResult result;
        try
        {
            result = await model.ChatStreamingAsync(new ChatHistoryAgentThread(history), cancellationToken: cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }

        if (result.Exception != null || string.IsNullOrWhiteSpace(result.AIMessage))
        {
            return null;
        }

        return MemoryDraftParser.Parse(result.AIMessage);
    }
}
