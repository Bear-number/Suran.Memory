using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Alife.Framework;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Suran.Memory;

/// <summary>
/// 设置页里的「记忆库」：列出全部条目，能改标题/摘要/重要度/钉住、移出或放回上下文、看原文、删除。
/// 所有改动都回到 MemoryService 的写入口，不在界面里直接摸 items——
/// 否则历史里那条存档消息不会跟着变，模型看到的还是旧文本。
/// </summary>
public partial class MemoryServiceUI
{
    bool _itemsOpen;   // 记忆库默认收起
    bool _showCreate;
    int _newLevel;
    string _newName = "";
    string _newTitle = "";
    string _newSynopsis = "";
    string _newContent = "";
    double _newImportance = 0.7;
    bool _newPinned;
    bool _newIntoContext = true;

    string? _editingId;
    string _editTitle = "";
    string _editSummary = "";
    double _editImportance = 0.5;
    bool _editPinned;

    string? _confirmDeleteId;
    string? _originalId;
    string _originalText = "";
    string _notice = "";
    string _topNote = "";
    string _listQuery = "";   // 记忆库列表的搜索词
    bool _busy;

    /// <summary>列表搜索：标题、摘要、短码、锚点都算。大小写不敏感，中文直接包含匹配。</summary>
    static bool Matches(MemoryItem item, string query)
    {
        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.ShortId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Facts.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    void BuildItemsSection(RenderTreeBuilder b, ref int i)
    {
        MemoryService? module = Module;
        int total = module?.ItemCount ?? 0;
        int live = module?.LiveCount ?? 0;
        int raw = module?.RawCount ?? 0;

        // 折叠标题：默认收起，免得改一项配置要滑很久
        CollapsibleTitle(b, ref i,
            "记忆库", total + " 条 · 上下文 " + live + " 条 · L0 原文 " + raw,
            _itemsOpen,
            () => { _itemsOpen = !_itemsOpen; StateHasChanged(); });

        if (_notice.Length > 0)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-notice");
            b.AddContent(i++, _notice);
            b.CloseElement();
        }

        // 打开目录：收起状态下也能点，不用展开
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-actions mem-actions-top");
        Button(b, ref i, "打开库存位置", "ghost", OpenFolder);
        Button(b, ref i, "刷新", "ghost", async () =>
        {
            // 真刷新：重读 items.json、重建索引、重数待压原文。以前这里只重渲染一遍，
            // 磁盘上改过的东西根本不会反映出来。
            _topNote = module == null ? "模块未就绪，等角色加载完再试" : await module.RescanAsync();
            _notice = _topNote;
            StateHasChanged();
        });
        b.CloseElement();

        if (!_itemsOpen)
        {
            AddHint(b, ref i, "点标题展开：编辑标题/摘要/重要度、移出或放回上下文、看原文、删除、新增。");
            if (module != null && raw > 0)
                AddHint(b, ref i, "L0 是还没压的原文消息，只存在于当前对话里，不是存档，所以不在下面的列表（也不会写进 L{n}/）。");
            return;
        }

        if (module == null)
        {
            AddHint(b, ref i, "模块未就绪，等角色加载完再刷新。");
            return;
        }

        AddHint(b, ref i, "改完立刻生效；在上下文里的条目会同步改掉历史中的那条存档消息。按等级从高到低排（L100 → L1），同级按时间倒序，最多显示 300 条。");

        // ── 新增一条（对应官方的 Memorize）──
        Button(b, ref i, _showCreate ? "收起新增" : "＋ 新增一条记忆", _showCreate ? "ghost" : "primary",
            () => { _showCreate = !_showCreate; _notice = ""; StateHasChanged(); return Task.CompletedTask; });

        if (_showCreate)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor");

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor-row");

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor-col");
            AddLabel(b, ref i, "等级（0 = 用配置的最高级）");
            b.OpenComponent<InputNumber<int>>(i++);
            b.AddAttribute(i++, "Value", _newLevel);
            b.AddAttribute(i++, "Min", 0);
            b.AddAttribute(i++, "Max", 100);
            b.AddAttribute(i++, "Style", "width:100%;");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<int>(this, v => _newLevel = v));
            b.CloseComponent();
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor-col");
            AddLabel(b, ref i, "重要度（0~1）");
            b.OpenComponent<InputNumber<double>>(i++);
            b.AddAttribute(i++, "Value", _newImportance);
            b.AddAttribute(i++, "Min", 0.0);
            b.AddAttribute(i++, "Max", 1.0);
            b.AddAttribute(i++, "Step", 0.05);
            b.AddAttribute(i++, "Style", "width:100%;");
            b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<double>(this, v => _newImportance = v));
            b.CloseComponent();
            b.CloseElement();

            b.CloseElement();

            AddInputSmall(b, ref i, "名称（可选，作为索引与文件名：{等级}-{名称}）", _newName, v => _newName = v);
            AddHint(b, ref i, "留空就用时间戳（自动压缩那种形式）。填了会落在 L{等级}/100-你的名字.txt，MemoryRecall 也用它作索引。");

            AddInputSmall(b, ref i, "标题（留空则取简述首行）", _newTitle, v => _newTitle = v);

            AddLabel(b, ref i, "简述（一直显示在上下文里，模型看得到）");
            AddTextAreaPlain(b, ref i, _newSynopsis, v => _newSynopsis = v, 3);

            AddLabel(b, ref i, "内容（存到 L{n}/ 里，只有 MemoryRecall 才看得到）");
            AddTextAreaPlain(b, ref i, _newContent, v => _newContent = v, 6);
            AddHint(b, ref i, "内容留空就把简述当原文存。分两栏的意义：简述短、常驻上下文；内容长、按需翻。" );

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor-row");

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor-col");
            AddLabel(b, ref i, "钉住（永不被淘汰）");
            b.OpenComponent<Switch>(i++);
            b.AddAttribute(i++, "Checked", _newPinned);
            b.AddAttribute(i++, "CheckedChildren", "开");
            b.AddAttribute(i++, "UnCheckedChildren", "关");
            b.AddAttribute(i++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => _newPinned = v));
            b.CloseComponent();
            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-editor-col");
            AddLabel(b, ref i, "立刻放进上下文");
            b.OpenComponent<Switch>(i++);
            b.AddAttribute(i++, "Checked", _newIntoContext);
            b.AddAttribute(i++, "CheckedChildren", "开");
            b.AddAttribute(i++, "UnCheckedChildren", "关");
            b.AddAttribute(i++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => _newIntoContext = v));
            b.CloseComponent();
            b.CloseElement();

            b.CloseElement();

            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-actions");
            Button(b, ref i, "创建", "primary", CreateItem);
            Button(b, ref i, "取消", "ghost", () =>
            {
                _showCreate = false;
                _notice = "";
                StateHasChanged();
                return Task.CompletedTask;
            });
            b.CloseElement();

            b.CloseElement();
        }

        // 列表搜索：最多 300 条平铺时，靠肉眼看找一条是很糟的体验
        b.OpenElement(i++, "div");
        b.AddAttribute(i++, "class", "mem-search");
        b.OpenComponent<Input<string>>(i++);
        b.AddAttribute(i++, "Value", _listQuery);
        b.AddAttribute(i++, "Placeholder", "在记忆库里搜：标题、摘要、短码、锚点");
        b.AddAttribute(i++, "AllowClear", true);
        b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<string>(this, value =>
        {
            _listQuery = value ?? "";
            StateHasChanged();
        }));
        b.CloseComponent();
        b.CloseElement();

        IReadOnlyList<MemoryItem> list;
        try
        {
            list = module.ListItems();
            string query = _listQuery.Trim();
            if (query.Length > 0)
                list = list.Where(item => Matches(item, query)).ToList();
        }
        catch (InvalidOperationException)
        {
            // 后台正在压缩（集合被改），这一帧渲染跳过；点「刷新」即可
            AddHint(b, ref i, "库里正在被改写（压缩进行中），点上面的「刷新」再看。");
            return;
        }

        if (list.Count == 0)
        {
            AddHint(b, ref i, _listQuery.Trim().Length > 0
                ? "没有匹配「" + _listQuery.Trim() + "」的条目。清空搜索看全部。"
                : "库里还没有条目。对话框里聊够一轮、或让角色 MemoryRemember 记一条就会有了。");
            return;
        }

        if (_listQuery.Trim().Length > 0)
            AddHint(b, ref i, "筛出 " + list.Count + " 条（共 " + total + " 条）。");

        foreach (MemoryItem item in list)
        {
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-item");

            // 第一行：等级徽标 + 时间 + 标题 + 状态
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-item-head");

            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "mem-badge");
            b.AddContent(i++, "L" + item.Level);
            b.CloseElement();

            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "mem-short");
            b.AddContent(i++, string.IsNullOrEmpty(item.ShortId) ? "------" : item.ShortId);
            b.CloseElement();

            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "mem-time");
            b.AddContent(i++, item.StartLocal.ToString("MM-dd HH:mm"));
            b.CloseElement();

            b.OpenElement(i++, "span");
            b.AddAttribute(i++, "class", "mem-title");
            b.AddContent(i++, string.IsNullOrWhiteSpace(item.Title) ? "（无标题）" : item.Title);
            b.CloseElement();

            if (item.InContext)
                Badge(b, ref i, "上下文", "on");
            if (item.Pinned)
                Badge(b, ref i, "钉住", "pin");
            if (item.Importance >= 0.85)
                Badge(b, ref i, "重要", "hot");
            if (item.SupersededBy.Length > 0)
                Badge(b, ref i, "已作废", "off");
            if (item.Facts.Length > 0)
                Badge(b, ref i, "锚点", "facts");
            if (item.ArchiveRel.Length == 0)
                Badge(b, ref i, "无原文", "off");

            b.CloseElement();

            // 第二行：摘要预览（可折叠，这里只给两行）
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-preview");
            b.AddContent(i++, MemoryStore.Trim(item.Summary.Replace("\r", "").Replace("\n", " "), 180));
            b.CloseElement();

            // 摘要没写到、但猜不回来的路径/版本/开关，单独一行让用户看见
            if (item.Facts.Length > 0)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-facts");
                b.AddContent(i++, "锚点：" + MemoryStore.Trim(item.Facts, 160));
                b.CloseElement();
            }

            // 第三行：操作
            b.OpenElement(i++, "div");
            b.AddAttribute(i++, "class", "mem-actions");

            Button(b, ref i, "编辑", "edit", async () => BeginEdit(item));
            Button(b, ref i, item.InContext ? "移出上下文" : "放回上下文", "ghost",
                async () => await Run(() => module.ToggleContextAsync(item.Id)));
            if (item.ArchiveRel.Length > 0)
            {
                Button(b, ref i, _originalId == item.Id ? "收起原文" : "看原文", "ghost",
                    async () => await ShowOriginal(item.Id));
                Button(b, ref i, "定位原文", "ghost",
                    async () => { _notice = Module?.RevealArchive(item.Id) ?? ""; StateHasChanged(); await Task.CompletedTask; });
            }
            Button(b, ref i, _confirmDeleteId == item.Id ? "确认删除" : "删除", "danger",
                async () =>
                {
                    if (_confirmDeleteId != item.Id)
                    {
                        _confirmDeleteId = item.Id;
                        _notice = "再点一次「确认删除」就会删掉这条记忆（原文挪到 tiers/deleted/，可手动恢复）";
                        return;
                    }
                    _confirmDeleteId = null;
                    await Run(() => module.DeleteItemAsync(item.Id));
                });

            b.CloseElement();

            // 编辑器
            if (_editingId == item.Id)
            {
                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-editor");

                AddInputSmall(b, ref i, "标题", _editTitle, v => _editTitle = v);
                AddLabel(b, ref i, "摘要");
                AddTextAreaPlain(b, ref i, _editSummary, v => _editSummary = v, 10);

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-editor-row");

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-editor-col");
                AddLabel(b, ref i, "重要度（0~1）");
                b.OpenComponent<InputNumber<double>>(i++);
                b.AddAttribute(i++, "Value", _editImportance);
                b.AddAttribute(i++, "Min", 0.0);
                b.AddAttribute(i++, "Max", 1.0);
                b.AddAttribute(i++, "Step", 0.05);
                b.AddAttribute(i++, "Style", "width:100%;");
                b.AddAttribute(i++, "ValueChanged", EventCallback.Factory.Create<double>(this, v => _editImportance = v));
                b.CloseComponent();
                b.CloseElement();

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-editor-col");
                AddLabel(b, ref i, "钉住（永不被淘汰）");
                b.OpenComponent<Switch>(i++);
                b.AddAttribute(i++, "Checked", _editPinned);
                b.AddAttribute(i++, "CheckedChildren", "开");
                b.AddAttribute(i++, "UnCheckedChildren", "关");
                b.AddAttribute(i++, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => _editPinned = v));
                b.CloseComponent();
                b.CloseElement();

                b.CloseElement();

                if (item.Level >= 100)
                    AddHint(b, ref i, "L100 是永久层：即使把「钉住」关掉，也不会被容量淘汰或合并升级。想让它离开上下文请用「移出上下文」。");

                b.OpenElement(i++, "div");
                b.AddAttribute(i++, "class", "mem-actions");
                Button(b, ref i, "保存", "primary", async () => await SaveEdit(item.Id));
                Button(b, ref i, "取消", "ghost", async () =>
                {
                    _editingId = null;
                    await Task.CompletedTask;
                });
                b.CloseElement();

                b.CloseElement();
            }

            // 原文
            if (_originalId == item.Id)
            {
                b.OpenElement(i++, "pre");
                b.AddAttribute(i++, "class", "mem-original");
                b.AddContent(i++, _originalText);
                b.CloseElement();
            }

            b.CloseElement();
        }
    }

    async Task OpenFolder()
    {
        if (Module == null)
            return;
        _notice = Module.OpenStoreFolder();
        StateHasChanged();
        await Task.CompletedTask;
    }

    async Task CreateItem()
    {
        if (Module == null)
            return;
        await Run(() => Module.CreateItemAsync(_newLevel, _newName, _newTitle, _newSynopsis, _newContent,
            _newImportance, _newPinned, _newIntoContext));
        if (_notice.StartsWith("已新增", StringComparison.Ordinal))
        {
            _showCreate = false;
            _newName = "";
            _newTitle = "";
            _newSynopsis = "";
            _newContent = "";
            _newLevel = 0;
            _newImportance = 0.7;
            _newPinned = false;
            _newIntoContext = true;
            StateHasChanged();   // Run 里已经刷过一次，字段清空要再刷一次
        }
    }

    void BeginEdit(MemoryItem item)
    {
        _editingId = item.Id;
        _editTitle = item.Title;
        _editSummary = item.Summary;
        _editImportance = item.Importance;
        _editPinned = item.Pinned;
        _confirmDeleteId = null;
        _notice = "正在编辑 " + item.Id;
    }

    async Task SaveEdit(string id)
    {
        if (Module == null)
            return;
        await Run(() => Module.SaveItemAsync(id, _editTitle, _editSummary, _editImportance, _editPinned));
        _editingId = null;
    }

    async Task ShowOriginal(string id)
    {
        if (_originalId == id)
        {
            _originalId = null;
            _originalText = "";
            return;
        }
        _originalId = id;
        _originalText = Module?.ReadOriginal(id) ?? "原文不在（可能已删除）";
        await Task.CompletedTask;
    }

    async Task Run(Func<Task<string>> action)
    {
        _busy = true;
        try
        {
            _notice = await action();
        }
        catch (Exception ex)
        {
            _notice = "操作失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
        StateHasChanged();
    }

    // ── 小控件 ──────────────────────────────────────────────

    /// <summary>可点击收起/展开的小节标题</summary>
    void CollapsibleTitle(RenderTreeBuilder b, ref int seq, string title, string summary, bool open, Action toggle)
    {
        b.OpenElement(seq++, "div");
        b.AddAttribute(seq++, "class", "mem-sec mem-sec-click");
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, (MouseEventArgs e) => toggle()));

        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-caret");
        b.AddContent(seq++, open ? "▾" : "▸");
        b.CloseElement();

        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-sec-name");
        b.AddContent(seq++, title);
        b.CloseElement();

        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-sec-hint");
        b.AddContent(seq++, summary);
        b.CloseElement();

        b.CloseElement();
    }

    static void Badge(RenderTreeBuilder b, ref int seq, string text, string kind)
    {
        b.OpenElement(seq++, "span");
        b.AddAttribute(seq++, "class", "mem-tag mem-tag-" + kind);
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void Button(RenderTreeBuilder b, ref int seq, string text, string kind, Func<Task> onClick)
    {
        b.OpenElement(seq++, "button");
        b.AddAttribute(seq++, "type", "button");
        b.AddAttribute(seq++, "class", "mem-btn mem-btn-" + kind);
        b.AddAttribute(seq++, "disabled", _busy);
        b.AddAttribute(seq++, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, async (MouseEventArgs e) => await onClick()));
        b.AddContent(seq++, text);
        b.CloseElement();
    }

    void AddInputSmall(RenderTreeBuilder b, ref int seq, string label, string value, Action<string> setter)
    {
        AddLabel(b, ref seq, label);
        b.OpenComponent<Input<string>>(seq++);
        b.AddAttribute(seq++, "Value", value);
        b.AddAttribute(seq++, "ValueChanged", EventCallback.Factory.Create<string>(this, setter));
        b.CloseComponent();
    }

    void AddTextAreaPlain(RenderTreeBuilder b, ref int seq, string value, Action<string> setter, int rows)
    {
        b.OpenElement(seq++, "textarea");
        b.AddAttribute(seq++, "value", value);
        b.AddAttribute(seq++, "rows", rows);
        b.AddAttribute(seq++, "class", "mem-textarea");
        b.AddAttribute(seq++, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => setter(e.Value?.ToString() ?? "")));
        b.AddContent(seq++, value);
        b.CloseElement();
    }
}
