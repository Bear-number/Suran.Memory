using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace Suran.Memory;

/// <summary>
/// 记忆插件的设置页。
/// 手写在 RenderTreeBuilder 上，不用 .razor——宿主的 CSharpCompiler 只编译 .cs，
/// 走 razor 得靠离线预生成，改一个控件都要重新出文件，不值得。
/// 控件沿用 EmotionTTS 那套写法（AntDesign Input/InputNumber/Switch），保存按钮由框架渲染。
/// </summary>
public partial class MemoryServiceUI : ModuleUIBase<MemoryService, MemoryConfig>
{
    [Inject]
    IJSRuntime JS { get; set; } = default!;

    /// <summary>内置提示词展开状态。默认收起——它有两千多字，展开会把整页撑长</summary>
    bool _showPrompt;

    /// <summary>提示词区的临时反馈（填入编辑框后给一句，不然点了没反应）</summary>
    string _promptNote = "";

    /// <summary>设置页是否已经自动刷过一次</summary>
    bool _rescanned;

    /// <summary>
    /// 设置页第一次渲染后自动从磁盘刷一次。
    ///
    /// 放在渲染之后而不是 OnInitialized：那时 Module 可能还没注入，
    /// 而且「L0 原文 N 条」这类统计必须等历史线程可用才能数——不数就一直是 0，
    /// 看起来像"没有待压的"。
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (_rescanned)
            return;

        MemoryService? module = Module;
        if (module == null)
            return;   // 模块还没就绪，下一次渲染再试

        _rescanned = true;   // 无条件标记，否则被拒时会 StateHasChanged → 再渲染 → 再刷，转成死循环
        string note = await module.RescanAsync();
        // 正常刷新不提示——列表本身就是结果，每次打开设置页多一句话反而吵。
        // 只有被拒（正在压缩）才说一声，让用户知道现在看到的可能是旧的。
        if (!note.StartsWith("已刷新", StringComparison.Ordinal))
            _topNote = note;
        StateHasChanged();
    }

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        if (Configuration == null)
        {
            b.AddContent(0, "Configuration NULL");
            return;
        }

        MemoryConfig cfg = Configuration;
        MemoryService? module = Module;

        int i = 0;
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-root");
        InjectStyles(b, ref i);

        // ── 顶栏 ──────────────────────────────────────────────
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-h1");
        b.AddContent(i++, "记忆（分层归档）");
        b.CloseElement();

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-sub");
        b.AddContent(i++, "旧对话在原位置压成摘要，超出容量自动移出上下文（原文仍在库中可检索）。");
        b.CloseElement();

        if (module != null)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-stat");
            b.AddContent(i++, "库里 " + module.ItemCount + " 条 · 在上下文 " + module.LiveCount
                + " 条 · 有原文 " + module.ArchiveCount + " 条 · 带锚点 " + module.FactsCount + " 条");
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-actions mem-actions-top");
            Button(b, ref i, "现在回忆一次", "ghost", async () =>
            {
                _topNote = await module.RecallNow();
                StateHasChanged();
            });
            Button(b, ref i, "打开星云", "ghost", OpenNebula);
            Button(b, ref i, "导出完整上下文", "ghost", async () =>
            {
                _topNote = await module.DumpContextAsync();
                StateHasChanged();
            });
            b.CloseElement();

            if (_topNote.Length > 0)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-hint");
                b.AddContent(i++, _topNote);
                b.CloseElement();
            }
        }

        // ── 记忆库（可编辑）────────────────────────────────────
        BuildItemsSection(b, ref i);

        // ── 压缩 ──────────────────────────────────────────────
        SectionTitle(b, ref i, "压缩", "什么时候压、一次压多少。跟着官方那套：L0 攒够就压最旧的一批。");
        AddSwitch(b, ref i, "启用自动归档", cfg.CaptureEnabled, v => cfg.CaptureEnabled = v);
        AddHint(b, ref i, "关掉就完全不压缩，已有存档仍留在上下文里，工具照常可用。");
        AddNumberInput(b, ref i, "L0 触发条数", cfg.TriggerMessages, v => cfg.TriggerMessages = v, 4, 2000);
        AddHint(b, ref i, "还没压的原文（L0）攒到这么多条就压一批，一问一答算 2 条。官方 120。");
        AddNumberInput(b, ref i, "每批压缩条数", cfg.BatchMessages, v => cfg.BatchMessages = v, 2, 2000);
        AddHint(b, ref i, "每次压最旧的多少条，剩下的继续以原文留在上下文。官方 100（120 触发时留 20 条原文）。");
        AddNumberInputPercentage(b, ref i, "触发概率", cfg.Probability, v => cfg.Probability = v);
        AddHint(b, ref i, "到阈值后每轮有多大概率真的压。0.4 = 平均两三轮才压一次，1 = 每次必压。");
        AddNumberInput(b, ref i, "单批字数上限", cfg.BatchMaxChars, v => cfg.BatchMaxChars = v, 2000, 200000);
        AddHint(b, ref i, "一批原文超这个字数就少取几条，避免把模型上下文撑爆。");
        AddNumberInput(b, ref i, "最高等级", cfg.MaxLevel, v => cfg.MaxLevel = v, 2, 20);
        AddNumberInput(b, ref i, "同级合并条数", cfg.GroupSize, v => cfg.GroupSize = v, 2, 20);
        AddHint(b, ref i, "同级攒够这么多条就压成一条更高等级；压「条数-1」条，最新那条留在原层，阶梯不断层。");
        AddSwitch(b, ref i, "压缩时用思考通道", cfg.ThinkingCompress, v => cfg.ThinkingCompress = v);
        AddHint(b, ref i, "开着摘要更准，但更慢更贵；关掉租轻量通道。");

        // ── 容量 ──────────────────────────────────────────────
        SectionTitle(b, ref i, "容量", "上下文里最多放多少字，超了怎么淘汰。");
        AddNumberInput(b, ref i, "存档总字数上限", cfg.MaxBlockChars, v => cfg.MaxBlockChars = v, 2000, 300000);
        AddHint(b, ref i, "实测一条存档 1100~2700 字，24000 大约是 8 条的余量。超了把最不重要的移出上下文。");
        AddNumberInput(b, ref i, "最少保留条数", cfg.MinKeepItems, v => cfg.MinKeepItems = v, 1, 50);
        AddNumberInput(b, ref i, "永久记忆最多常驻条数", cfg.PermanentMaxItems, v => cfg.PermanentMaxItems = v, 0, 200);
        AddHint(b, ref i, "L100 及以上超过这个条数，就把不重要的移出上下文（不删除、仍可检索/放回）。0 = 不限。");
        AddNumberInput(b, ref i, "永久记忆常驻字数上限", cfg.PermanentMaxChars, v => cfg.PermanentMaxChars = v, 0, 200000);
        AddSwitch(b, ref i, "超限时让模型决定留哪几条", cfg.PermanentTidyByModel, v => cfg.PermanentTidyByModel = v);
        AddHint(b, ref i, "关着就按「重要度优先、同等留新的」自动取舍，不花模型调用。");
        AddHint(b, ref i, "淘汰时至少留这么多条，再紧也不清空。");

        // ── 星云（挂件位置）──────────────────────────────────
        // 这一节是手动列的：本页不像心情插件那样反射生成，新增配置项不会自己冒出来。
        SectionTitle(b, ref i, "星云图", "记忆摊成一张星图。这里调右下角「✦ 星云」按钮的位置。");
        AddNumberInput(b, ref i, "按钮距底部像素", cfg.NebulaButtonBottomPx, v => cfg.NebulaButtonBottomPx = v, 0, 1200);
        AddHint(b, ref i, "默认 300——要落在状态挂件（默认 190 起、约高 84）之上。两个都贴右下角，位置写死会互相压住。");
        AddNumberInput(b, ref i, "按钮距右侧像素", cfg.NebulaButtonRightPx, v => cfg.NebulaButtonRightPx = v, 0, 1200);
        AddHint(b, ref i, "默认 14。和状态挂件各调各的：想让两枚按钮并排，就改这一项或那一项。保存后最多 30 秒生效（按钮每 30 秒自己刷一次），不用重载。");

        // ── 检索 ──────────────────────────────────────────────
        SectionTitle(b, ref i, "检索", "角色回头找旧事时的默认行为。");
        AddNumberInput(b, ref i, "默认返回条数", cfg.SearchTake, v => cfg.SearchTake = v, 1, 50);
        AddSwitch(b, ref i, "只给新情报", cfg.SearchOnlyNew, v => cfg.SearchOnlyNew = v);
        AddHint(b, ref i, "同一轮里已返回过的条目不再重复给（模型常反复搜同一件事）。调用方传 allowSeen=true 可以看旧的。");
        AddNumberInput(b, ref i, "每条摘要摘录字数", cfg.SearchExcerptChars, v => cfg.SearchExcerptChars = v, 0, 400);
        AddNumberInput(b, ref i, "单条原文上限字数", cfg.RecallMaxChars, v => cfg.RecallMaxChars = v, 500, 50000);
        AddHint(b, ref i, "MemoryRecall 单条最多回多少字，超出截断，防止一口气灌满上下文。");

        // ── 提示与告警 ────────────────────────────────────────
        SectionTitle(b, ref i, "提示与告警", "只影响日志和一句追加提示，不改数据。");
        AddNumberInput(b, ref i, "上下文 token 警告线", cfg.TokenWarningThreshold, v => cfg.TokenWarningThreshold = v, 1000, 2000000);
        AddInput(b, ref i, "回忆关键词（顿号或逗号分隔）",
            string.Join("、", cfg.RecallHintKeywords ?? new List<string>()),
            v => cfg.RecallHintKeywords = SplitKeywords(v),
            "例如：还记得、上次、以前说过");
        AddHint(b, ref i, "用户消息里出现这些词时，在其后追加一句「记不清可以检索记忆」。留空则不追加。");

        // ── 高级 ──────────────────────────────────────────────
        SectionTitle(b, ref i, "高级", "一般不用动。");
        AddInput(b, ref i, "压缩模型类名", cfg.CompressModelClassName, v => cfg.CompressModelClassName = v,
            "留空 = 用角色当前的语言模型");
        AddHint(b, ref i, "填一个模块类名可以让压缩走更便宜的模型；留空就用角色主模型。");

        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-label");
        b.AddContent(i++, "自定义压缩提示词");
        b.CloseElement();
        AddTextArea(b, ref i, cfg.CompressPrompt, v => cfg.CompressPrompt = v,
            "留空则用内置提示词（初次压缩 / 合并升级各一套）");
        AddHint(b, ref i, "可用占位：{range} = 这段时间范围，{archives} = 已有存档清单（用于判断要不要写「替代」）。填了就两条路径都用这一份。");

        // 内置提示词：折叠展示 + 一键填入。不给看的话，这个输入框等于要求人凭空写两千字
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-actions mem-actions-top");
        Button(b, ref i, _showPrompt ? "收起内置提示词" : "查看内置提示词", "ghost", () =>
        {
            _showPrompt = !_showPrompt;
            _promptNote = "";
            StateHasChanged();
            return Task.CompletedTask;
        });
        b.CloseElement();

        if (_promptNote.Length > 0)
            AddHint(b, ref i, _promptNote);

        if (_showPrompt)
        {
            AddPromptBlock(b, ref i, "初次压缩（原始对话 → L1）",
                "把一批还没压过的原文压成一条存档。要第一人称、带感情但具体——原话照引、人物时间地点数字一律保留、不许编没发生过的情节。",
                MemoryCompressor.DefaultPrompt, cfg);

            AddPromptBlock(b, ref i, "合并升级（同级摘要 → 更高一级）",
                "输入已经是以前写过的摘要，风险从「漏细节」变成「情绪越叠越厚、事实越压越淡」，所以这一档要事实优先、去重情绪、不许新增情节。",
                MemoryCompressor.MergePrompt, cfg);
        }

        // ── 旧数据 ────────────────────────────────────────────
        SectionTitle(b, ref i, "旧数据", "对话历史的保存开关，和从官方「持久记忆」换过来的那一次。");
        AddSwitch(b, ref i, "保存对话历史（重启后恢复）", cfg.PersistHistory, v => cfg.PersistHistory = v);
        AddHint(b, ref i, "Alife 自己不持久化会话。开着它，整段对话（含还没压的 L0 原文）会写进 Memory\\History.json，重启后原样恢复；关掉则重启只剩存档摘要。");
        AddSwitch(b, ref i, "启动时索引旧存档", cfg.ImportLegacy, v => cfg.ImportLegacy = v);
        AddHint(b, ref i, "把官方留下的 Memory\\L{n}\\*.txt 索引成条目。文件不动、可重复执行，只是让这里的列表认识它们。");
        AddHint(b, ref i, "注意：两个插件**不能同时启用**——都挂 ChatHistoryAdd、都会压缩历史，同时开会各压一份、产生重复存档。要换就先把官方那个停用。");

        b.CloseElement();
    }

    /// <summary>
    /// 设置页里直接铺开星云：刚调完位置能当场验证，不用跑回聊天页找那颗按钮。
    /// 星云本体挂在聊天页的 GlobalUI 组件上，这里只借用它暴露的 `window.__SN` 入口，
    /// 不重复造一套渲染——否则两份代码迟早长歪。
    /// 打开前先叫组件重推一次数据，避免看到的是上一轮的快照。
    /// </summary>
    async Task OpenNebula()
    {
        MemoryNebulaUI.RequestRefresh();
        try
        {
            string res = (await JS.InvokeAsync<string>("eval",
                "(function(){if(window.__SN&&window.__SN.open){window.__SN.open();return 'ok'}"
                + "return 'none'})()")) ?? "";
            res = res.Trim().Trim('"');
            _topNote = res == "ok"
                ? "已铺开星云，右上角 × 关掉"
                : "星云还没挂上——先在聊天页看一眼那个「✦ 星云」按钮，或重载角色";
        }
        catch (Exception ex)
        {
            _topNote = "打不开：" + ex.Message;
        }
        StateHasChanged();
    }

    // ── 小节与控件 ──────────────────────────────────────────

    static void SectionTitle(RenderTreeBuilder b, ref int seq, string title, string hint)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-sec");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-sec-name");
        b.AddContent(seq++, title);
        b.CloseElement();
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-sec-hint");
        b.AddContent(seq++, hint);
        b.CloseElement();
        b.CloseElement();
    }

    static void AddLabel(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-label");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    static void AddHint(RenderTreeBuilder b, ref int seq, string text)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-hint");
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    /// <summary>
    /// 一块内置提示词：标题 + 为什么这么写 + 原文 + 填入按钮。
    /// 「填入」是把模板原样（含 {range} / {archives} 占位）拷进上面的输入框，方便在它基础上改，
    /// 而不是让人对着一个空白框从零写两千字。
    /// </summary>
    void AddPromptBlock(RenderTreeBuilder b, ref int seq, string title, string why, string body, MemoryConfig cfg)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-prompt");

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-prompt-head");
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-prompt-name");
        b.AddContent(seq++, title);
        b.CloseElement();
        b.CloseElement();

        AddHint(b, ref seq, why);

        b.OpenElement(seq++, "pre");
        b.AddAttribute(seq++, "class", "mem-prompt-body");
        b.AddContent(seq++, body);
        b.CloseElement();

        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-actions");
        Button(b, ref seq, "填入上面的输入框", "ghost", () =>
        {
            cfg.CompressPrompt = body;
            _promptNote = "已填入「" + title + "」。改完点保存生效；把输入框清空就回到用内置的。";
            StateHasChanged();
            return Task.CompletedTask;
        });
        b.CloseElement();

        b.CloseElement();
    }

    void AddInput(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> setter, string placeholder)
    {
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-field");
        b.OpenComponent<Input<string>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "Placeholder", placeholder);
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddTextArea(RenderTreeBuilder b, ref int seq, string value, Action<string> setter, string placeholder)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-field");
        b.OpenElement(seq++, "textarea");
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "placeholder", placeholder);
        b.AddAttribute(seq++, "rows", "8");
        b.AddAttribute(seq++, "class", "mem-textarea");
        b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => setter(e.Value?.ToString() ?? "")));
        b.AddContent(seq++, value);
        b.CloseElement();
        b.CloseElement();
    }

    void AddNumberInput(RenderTreeBuilder b, ref int seq, string label, int value, Action<int> setter, int min, int max)
    {
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-field");
        b.OpenComponent<InputNumber<int>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "Min", min);
        b.AddAttribute(seq++, "Max", max);
        b.AddAttribute(seq++, "Style", "width:100%;");
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<int>(this, setter));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddNumberInputPercentage(RenderTreeBuilder b, ref int seq, string label, float value, Action<float> setter)
    {
        AddLabel(b, ref seq, label);
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-field");
        b.OpenComponent<InputNumber<double>>(seq++);
        b.AddAttribute(seq++, "Value", (double)value);
        b.AddAttribute(seq++, "Min", 0.0);
        b.AddAttribute(seq++, "Max", 1.0);
        b.AddAttribute(seq++, "Step", 0.1);
        b.AddAttribute(seq++, "Style", "width:100%;");
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<double>(this, v => setter((float)v)));
        b.CloseComponent();
        b.CloseElement();
    }

    void AddSwitch(RenderTreeBuilder b, ref int seq, string label, bool value, Action<bool> setter)
    {
        AddLabel(b, ref seq, label);
        b.OpenComponent<Switch>(seq++);
        b.AddAttribute(seq++, "Checked", value);
        b.AddAttribute(seq++, "CheckedChildren", "开");
        b.AddAttribute(seq++, "UnCheckedChildren", "关");
        b.AddAttribute(seq++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, setter));
        b.CloseComponent();
    }

    static List<string> SplitKeywords(string text)
    {
        return (text ?? "")
            .Split(new[] { '、', ',', '，', ';', '；', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim())
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    static void InjectStyles(RenderTreeBuilder b, ref int seq)
    {
        b.OpenElement(seq++, "style");
        b.AddContent(seq++, """
            .mem-root{
                max-width:760px;padding:20px 24px 24px;
                background:var(--alife-bg-container,#fff);border-radius:12px;
                border:1px solid var(--alife-border,#eee);
            }
            .mem-h1{font-size:19px;font-weight:700;margin-bottom:6px;}
            .mem-sub{font-size:13px;opacity:.65;line-height:1.6;margin-bottom:10px;}
            .mem-stat{
                font-size:12px;opacity:.8;padding:8px 12px;margin-bottom:16px;border-radius:8px;
                background:var(--alife-bg-layout,#f6f6f8);
            }
            .mem-sec{display:flex;align-items:baseline;gap:10px;margin:22px 0 10px;}
            .mem-sec-click{cursor:pointer;user-select:none;padding:6px 8px;margin:18px -8px 8px;border-radius:8px;}
            .mem-sec-click:hover{background:var(--alife-bg-layout,#f5f5f7);}
            .mem-caret{font-size:12px;opacity:.6;width:10px;display:inline-block;}
            .mem-sec-name{font-size:14px;font-weight:700;}
            .mem-sec-hint{font-size:12px;opacity:.55;}
            .mem-label{font-size:13px;margin:12px 0 6px;font-weight:500;}
            .mem-field{margin-bottom:2px;}
            .mem-hint{font-size:12px;opacity:.6;line-height:1.6;margin:4px 0 2px;}
            .mem-notice{
                font-size:12px;padding:8px 12px;margin:10px 0;border-radius:8px;
                background:var(--alife-bg-layout,#f6f6f8);color:inherit;
            }
            .mem-item{
                border:1px solid var(--alife-border,#eee);border-radius:10px;
                padding:10px 12px;margin-bottom:8px;
            }
            .mem-item-head{display:flex;align-items:center;gap:8px;flex-wrap:wrap;}
            .mem-badge{
                font-size:11px;font-weight:700;padding:1px 6px;border-radius:5px;
                background:var(--alife-bg-layout,#f0f0f2);
            }
            .mem-time{font-size:11px;opacity:.55;}
            .mem-short{font-size:11px;font-family:ui-monospace,Consolas,monospace;opacity:.75;
                padding:0 4px;border-radius:4px;background:var(--alife-bg-layout,#f2f2f4);}
            .mem-title{font-size:13px;font-weight:600;flex:1 1 auto;min-width:120px;}
            .mem-tag{font-size:11px;padding:1px 6px;border-radius:5px;border:1px solid transparent;}
            .mem-tag-on{background:#e8f7ee;color:#12804a;border-color:#bfe7cf;}
            .mem-tag-pin{background:#fff4e5;color:#96590b;border-color:#f5d9ae;}
            .mem-tag-hot{background:#fdeaea;color:#a12a2a;border-color:#f3c6c6;}
            .mem-tag-off{background:#f2f2f4;color:#888;border-color:#e2e2e6;}
            .mem-tag-facts{background:#eef3ff;color:#2f5dbd;border-color:#c9d8f7;}
            .mem-preview{font-size:12px;opacity:.62;line-height:1.6;margin:6px 0 8px;}
            .mem-facts{font-size:11px;font-family:ui-monospace,Consolas,monospace;opacity:.7;
                line-height:1.6;margin:-4px 0 8px;word-break:break-all;}
            .mem-hint{font-size:12px;opacity:.72;margin:2px 0 6px;}
            .mem-prompt{margin:10px 0 6px;padding:10px 12px;border-radius:8px;
                border:1px solid var(--alife-border,#e3e3e6);background:var(--alife-bg-layout,#fafafa);}
            .mem-prompt-head{display:flex;align-items:center;gap:10px;}
            .mem-prompt-name{font-size:12px;font-weight:600;}
            .mem-prompt-body{margin:6px 0 8px;font-size:11px;line-height:1.75;opacity:.86;
                white-space:pre-wrap;word-break:break-word;max-height:300px;overflow:auto;
                font-family:ui-monospace,Consolas,"Cascadia Mono",monospace;}
            .mem-actions{display:flex;gap:6px;flex-wrap:wrap;}
            .mem-actions-top{margin:8px 0 2px;}
            .mem-btn{
                font-size:12px;padding:2px 10px;border-radius:6px;cursor:pointer;line-height:1.7;
                border:1px solid var(--alife-border,#d9d9d9);background:transparent;color:inherit;
            }
            .mem-btn:hover{background:var(--alife-bg-layout,#f5f5f7);}
            .mem-btn-primary{background:#2f6fed;border-color:#2f6fed;color:#fff;}
            .mem-btn-danger{color:#c62828;border-color:#e9b7b7;}
            .mem-editor{
                margin-top:10px;padding:10px 12px;border-radius:8px;
                background:var(--alife-bg-layout,#fafafa);
            }
            .mem-editor-row{display:flex;gap:14px;}
            .mem-editor-col{flex:1 1 0;min-width:140px;}
            .mem-original{
                white-space:pre-wrap;word-break:break-word;font-size:12px;line-height:1.6;
                max-height:320px;overflow:auto;margin:10px 0 0;padding:10px 12px;border-radius:8px;
                background:var(--alife-bg-layout,#f6f6f8);
            }
            .mem-textarea{
                width:100%;padding:8px 10px;font-size:13px;line-height:1.6;border-radius:6px;
                border:1px solid var(--alife-border,#d9d9d9);background:var(--alife-bg-container,#fff);
                color:inherit;font-family:inherit;resize:vertical;
            }
            .mem-search{margin:6px 0 10px;max-width:420px;}
            /* 标签底色是硬编码的浅色，深色主题下会白得刺眼。这里只调标签，不动正文。 */
            @media (prefers-color-scheme:dark){
                .mem-tag-on{background:#10301f;color:#5fd394;border-color:#1d5136;}
                .mem-tag-pin{background:#331f0a;color:#e0a864;border-color:#5a3813;}
                .mem-tag-hot{background:#331414;color:#e58a8a;border-color:#5c2323;}
                .mem-tag-off{background:#24262b;color:#9aa1ab;border-color:#33373e;}
                .mem-tag-facts{background:#132441;color:#7ba7f0;border-color:#1f3a63;}
                .mem-btn-primary{background:#3b7bf0;border-color:#3b7bf0;}
                .mem-btn-danger{color:#ef8a8a;border-color:#5c2a2a;}
            }
            """);
        b.CloseElement();
    }
}
