using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Suran.Memory;

/// <summary>
/// 「模型这轮到底看到了什么」的可读快照。
///
/// 借鉴 dsh-memory-tiers 的 `/memory context`：把系统提示、记忆存档、对话消息
/// 分段原样铺开，头部给各段字数。区别是**不冒充请求报文**——
/// Alife 的请求是在运行时组装的，插件只能读到 ChatHistory 这一份历史，
/// 所以文件里明确写了哪些内容可能不在这份快照里。
///
/// 纯函数：不碰文件、不碰宿主，方便用探针验证分类和统计。
/// </summary>
public static class ContextDump
{
    public sealed record Line(
        string Role,
        string Content,
        string Stamp = "",
        int Level = 0,
        string ArchiveId = "");

    public static string Build(IReadOnlyList<Line> messages, string character, DateTime at)
    {
        List<Line> all = messages?.ToList() ?? [];
        List<Line> system = all.Where(m => m.Role == "system").ToList();
        List<Line> archives = all.Where(m => m.ArchiveId.Length > 0).ToList();
        List<Line> chat = all
            .Where(m => m.Role != "system" && m.ArchiveId.Length == 0)
            .ToList();

        int SysChars(List<Line> list) => list.Sum(m => m.Content.Length);
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.Append(character).Append(" · ").Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append(" · 系统提示 ").Append(system.Count).Append(" 条 / ").Append(N(SysChars(system))).Append(" 字")
          .Append(" · 记忆存档 ").Append(archives.Count).Append(" 条 / ").Append(N(SysChars(archives))).Append(" 字")
          .Append(" · 对话 ").Append(chat.Count).Append(" 条 / ").Append(N(SysChars(chat))).Append(" 字")
          .Append(" · 合计 ").Append(all.Count).Append(" 条 / ").Append(N(SysChars(all))).Append(" 字")
          .Append("\n\n");

        sb.Append("【说明】\n")
          .Append("· 这是从 ChatHistory 读出的历史快照，不是请求报文原文。\n")
          .Append("· 插件在 ChatSend 上临时拼进用户消息的状态/提示，以及宿主在请求组装时才追加的运行时段，\n")
          .Append("  是否出现在这里取决于宿主写回历史的时机；下面正文即当前历史里的内容。\n")
          .Append("· 以 [记忆存档(id)] 开头的是压缩后的旧对话，原文在 Memory/L{等级}/ 下，可用 MemoryRecall 取回。\n")
          .Append("· 每次导出覆盖同一份 context-dump.txt；要留档请自行另存。\n\n");

        sb.Append("【系统提示 · ").Append(system.Count).Append(" 条 / ")
          .Append(N(SysChars(system))).Append(" 字】\n");
        if (system.Count == 0)
            sb.Append("（当前历史里没有系统消息）\n");
        foreach (Line m in system)
            sb.Append(m.Content).Append("\n\n");

        sb.Append('\n').Append("【记忆存档 · ").Append(archives.Count).Append(" 条 / ")
          .Append(N(SysChars(archives))).Append(" 字】\n");
        if (archives.Count == 0)
            sb.Append("（上下文里没有存档消息）\n");
        for (int i = 0; i < archives.Count; i++)
        {
            Line m = archives[i];
            sb.Append("[").Append(i + 1).Append("] ")
              .Append("L").Append(m.Level).Append(" ")
              .Append(m.ArchiveId).Append(" · ").Append(N(m.Content.Length)).Append(" 字")
              .Append(m.Stamp.Length > 0 ? " · " + m.Stamp : "")
              .Append('\n')
              .Append(m.Content).Append("\n\n");
        }

        sb.Append('\n').Append("【对话消息 · ").Append(chat.Count).Append(" 条 / ")
          .Append(N(SysChars(chat))).Append(" 字】\n");
        if (chat.Count == 0)
            sb.Append("（没有对话消息）\n");
        for (int j = 0; j < chat.Count; j++)
        {
            Line m = chat[j];
            sb.Append("[").Append(j + 1).Append("] ")
              .Append(RoleLabel(m.Role)).Append(" · ").Append(m.Stamp.Length > 0 ? m.Stamp : "—")
              .Append(" · ").Append(N(m.Content.Length)).Append(" 字\n")
              .Append(m.Content).Append("\n\n");
        }

        return sb.ToString();
    }

    static string RoleLabel(string role) => role switch {
        "user" => "用户",
        "assistant" => "她",
        "system" => "系统",
        "tool" => "工具",
        _ => role,
    };
}
