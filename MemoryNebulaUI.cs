using System;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace Suran.Memory;

/// <summary>
/// 星云：把记忆摊成一张能看的图。
///
/// 不是"再做一个列表"——列表已经有了（`MemorySearch`）。这里回答的是
/// **她心里的东西长什么样**：哪些事聚成一团（反复发生的，也就是归纳出来的主题），
/// 哪些散在星云外围（只发生过一次的），哪些亮着（新的、重要的），哪些暗下去。
/// 星的颜色按**当时的心情**——低落偏冷蓝，高兴偏暖。
///
/// 挂载照 `1chuxin.TokenStats` 的 GlobalUI 通道：客户端按模块类型自动挂/卸，
/// 组件零输出，内容全靠 eval 注入。
///
/// 数据用 URL 编码传进脚本（`Uri.EscapeDataString`），
/// 这样摘要里的中文、引号、反斜杠都不用一个个转义——那是这类脚本最容易出错的地方。
/// </summary>
public sealed class MemoryNebulaUI : ComponentBase, IDisposable
{
    [Inject]
    IJSRuntime JS { get; set; } = default!;

    Timer? timer;
    bool disposed;
    bool broken;      // eval 不可用（老客户端）：放弃，别每轮再试
    bool cleared;     // 已经清过一次场：空数据时不要反复重发删除脚本
    string lastJson = "";
    int lastBottom = -1;
    int lastRight = -1;

    /// <summary>
    /// 设置页「打开星云」先叫一声：立刻重推一次数据，别让打开时看到上一轮的快照。
    /// 组件按类型唯一，静态桥只是把「有人要打开」这件事递给活着的那个实例。
    /// </summary>
    static event Action? RefreshRequested;

    public static void RequestRefresh() => RefreshRequested?.Invoke();

    void OnRefreshRequested() => _ = PushAsync(force: true);

    protected override void BuildRenderTree(RenderTreeBuilder b) { }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            RefreshRequested += OnRefreshRequested;
            await PushAsync(force: true);
            // 5 秒而不是 30 秒：星云开着时，刚压出来的新存档要能跟上来。
            // 数据没变会在 PushAsync 里直接返回，不产生额外 eval，轮询只有一次字符串构建。
            timer = new Timer(_ => _ = PushAsync(force: false), null,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        else
        {
            await PushAsync(force: true);
        }
    }

    async Task PushAsync(bool force)
    {
        if (disposed || broken)
            return;

        try
        {
            string json = MemoryService.NebulaJson();
            if (json.Length == 0)
            {
                // 记忆模块被关掉/角色没激活：把按钮和画面一起收掉。
                // 只 return 的话，按钮会带着最后的星数一直留着，看着像没生效
                if (!cleared)
                {
                    cleared = true;
                    lastJson = "";
                    _ = JS.InvokeAsync<string>("eval", CleanupScript);
                }
                return;
            }
            cleared = false;

            // 坐标不在 json 里：只比数据的话，改完配置要等到记忆变化才挪，看着像没生效
            int bottom = MemoryService.NebulaButtonBottomPx();
            int right = MemoryService.NebulaButtonRightPx();
            bool moved = bottom != lastBottom || right != lastRight;

            if (!force && !moved && string.Equals(json, lastJson, StringComparison.Ordinal))
                return;

            lastJson = json;
            lastBottom = bottom;
            lastRight = right;
            await JS.InvokeAsync<string>("eval", BuildScript(json, bottom, right));
        }
        catch (Exception)
        {
            broken = true;   // 星云是锦上添花，不能因为它坏了影响记忆本身
        }
    }

    /// <summary>
    /// 清场脚本：删按钮/画面节点 + 清页面状态。卸载组件与"实例没了"共用同一段。
    /// 只删 DOM 不清 __SN 的话，下次挂载会往已经摘掉的节点里写。
    /// </summary>
    const string CleanupScript =
        "(function(){var a=document.getElementById('sur-an-nebula');if(a)a.remove();"
        + "var b=document.getElementById('sur-an-nebula-wrap');if(b)b.remove();"
        + "window.__SN=null;return 'ok';})()";

    static string BuildScript(string json, int bottomPx, int rightPx)
        => "(function(){var B=" + Math.Clamp(bottomPx, 0, 1200)
           + ";var R=" + Math.Clamp(rightPx, 0, 1200)
           + ";var D=JSON.parse(decodeURIComponent('" + Uri.EscapeDataString(json)
           + "'));" + Body + "})()";

    /// <summary>
    /// 脚本本体。**刻意写成常量而不是插值字符串**：里面全是引号和花括号，
    /// 一旦混进插值，编译照样过，跑起来是错的。
    ///
    /// 状态挂在 window.__SN 上：每次 eval 都只更新数据并重画，
    /// 不重新建节点、不重新绑事件——否则每 30 秒叠一层监听器。
    /// </summary>
    const string Body = """
        var SN = window.__SN || (window.__SN = {});
        SN.data = D;
        // 这批"数据什么时候到的"，不是"界面什么时候画的"——顶栏要显示前者，
        // 否则打开旧快照也会显示当前时间，看着像刚同步过。
        SN.syncedAt = clock();

        function esc(t) {
          return String(t == null ? '' : t).replace(/[&<>]/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c];
          });
        }
        function num(v, d) { return (typeof v === 'number' && isFinite(v)) ? v : (d || 0); }
        function pct(v) { return Math.round(Math.max(0, Math.min(1, num(v, .5))) * 100); }
        function clock() {
          var d = new Date();
          function p(n) { return (n < 10 ? '0' : '') + n; }
          return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
        }

        // ── 右下角按钮 + 悬停小卡 ──────────────────────────────
        var WID = 'sur-an-nebula-wrap';
        var wrap = document.getElementById(WID);
        if (!wrap) {
          wrap = document.createElement('div');
          wrap.id = WID;
          wrap.style.cssText = 'position:fixed;z-index:2147482900;'
            + 'font:12px/1.5 system-ui,-apple-system,Segoe UI,sans-serif;color:#e9e9f0;user-select:none;';

          var btn0 = document.createElement('div');
          btn0.id = 'sur-an-nebula-btn';
          btn0.style.cssText = 'cursor:pointer;border:1px solid rgba(255,255,255,.10);border-radius:12px;'
            + 'padding:8px 10px;background:rgba(28,28,38,.82);box-shadow:0 6px 20px rgba(0,0,0,.28);'
            + 'backdrop-filter:blur(8px);transition:border-color .15s ease,transform .2s ease;';

          var card0 = document.createElement('div');
          card0.style.cssText = 'position:absolute;right:calc(100% + 10px);bottom:0;'
            + 'width:min(260px,72vw);box-sizing:border-box;padding:11px 13px;border-radius:14px;'
            + 'background:linear-gradient(180deg,rgba(30,26,50,.97),rgba(18,16,30,.97));'
            + 'border:1px solid rgba(255,255,255,.12);box-shadow:0 18px 48px rgba(0,0,0,.46);'
            + 'backdrop-filter:blur(10px);color:#e9e9f0;cursor:pointer;'
            + 'opacity:0;visibility:hidden;transform:scale(.96) translateX(8px);'
            + 'transform-origin:right bottom;'
            + 'transition:opacity .16s ease,transform .16s ease,visibility .16s;';

          wrap.appendChild(btn0);
          wrap.appendChild(card0);
          document.body.appendChild(wrap);

          SN.btn = btn0;
          SN.card = card0;
          SN.hideTimer = null;

          SN.showCard = function () {
            if (SN.hideTimer) { clearTimeout(SN.hideTimer); SN.hideTimer = null; }
            card0.style.visibility = 'visible';
            card0.style.opacity = '1';
            card0.style.transform = 'scale(1) translateX(0)';
          };
          SN.hideCard = function () {
            card0.style.opacity = '0';
            card0.style.transform = 'scale(.96) translateX(8px)';
            card0.style.visibility = 'hidden';
          };
          wrap.onmouseenter = function () {
            btn0.style.borderColor = 'rgba(167,139,250,.55)';
            btn0.style.transform = 'translateY(-1px)';
            SN.showCard();
          };
          wrap.onmouseleave = function () {
            btn0.style.borderColor = 'rgba(255,255,255,.10)';
            btn0.style.transform = 'none';
            SN.hideTimer = setTimeout(function () { SN.hideTimer = null; SN.hideCard(); }, 150);
          };
          card0.onclick = function () { if (SN.open) SN.open(); };
        }

        var btn = SN.btn;
        wrap.style.right = R + 'px';
        wrap.style.bottom = B + 'px';
        btn.textContent = '✦ 星云 · ' + ((SN.data.s || []).length) + ' 颗';

        SN.renderCard = function () {
          var s = SN.data.s || [], c = SN.data.c || [];
          var html = '<div style="display:flex;align-items:baseline;gap:8px;margin-bottom:5px">'
            + '<b style="font-size:13px;letter-spacing:.03em">记忆星云</b>'
            + '<span style="margin-left:auto;opacity:.32;font-size:10.5px">' + s.length + ' 颗</span></div>'
            + '<div style="opacity:.72;font-size:11.5px">' + s.length + ' 颗星 · ' + c.length + ' 个主题</div>';
          if (!c.length) {
            html += '<div style="opacity:.45;font-size:10.5px;margin-top:5px;line-height:1.6">'
              + '还没归纳出主题，星都散在外圈</div>';
          } else {
            html += '<div style="margin-top:8px;display:flex;flex-wrap:wrap;gap:4px">';
            for (var i = 0; i < c.length && i < 4; i++) {
              html += '<span style="padding:1px 7px;border-radius:999px;background:rgba(167,139,250,.14);'
                + 'border:1px solid rgba(167,139,250,.28);font-size:10.5px">'
                + esc(c[i].t) + ' · ' + (c[i].n || 0) + '</span>';
            }
            html += '</div>';
          }
          html += '<div style="margin-top:9px;padding-top:7px;border-top:1px solid rgba(255,255,255,.08);'
            + 'opacity:.38;font-size:10.5px">点一下铺开看 · 同步 ' + SN.syncedAt + '</div>';
          SN.card.innerHTML = html;
        };
        SN.renderCard();

        // 数据一到就刷新整页的统计/侧栏/详情——以前只刷按钮，打开后 HUD 是旧数字
        if (SN.inited) {
          SN.renderUI();
          if (SN.visible) SN.redraw();
          return 'ok';
        }
        SN.inited = true;

        // ── 全屏面板 ──────────────────────────────────────────
        var host = document.createElement('div');
        host.id = 'sur-an-nebula';
        host.className = 'snx';
        host.style.cssText = 'position:fixed;inset:0;z-index:2147482950;display:none;'
          + 'background:radial-gradient(ellipse at 50% 44%,#1b1830 0%,#0b0a16 62%,#06060c 100%);'
          + 'font:12px/1.6 system-ui,-apple-system,Segoe UI,sans-serif;color:#e9e9f0;';
        host.innerHTML = '<style>'
          + '.snx *{box-sizing:border-box}'
          + '.snx canvas{position:absolute;inset:0;width:100%;height:100%;cursor:grab}'
          + '.snx-top{position:absolute;left:0;right:0;top:0;height:54px;display:flex;align-items:center;gap:14px;'
          + 'padding:0 18px;border-bottom:1px solid rgba(255,255,255,.07);background:rgba(9,8,18,.76);'
          + 'backdrop-filter:blur(14px);z-index:4}'
          + '.snx-title{font-size:14px;letter-spacing:.12em;font-weight:500;white-space:nowrap}'
          + '.snx-title i{font-style:normal;color:#a78bfa;margin-right:8px}'
          + '.snx-stats{font-size:11.5px;opacity:.55;white-space:nowrap}'
          + '.snx-top .snx-sp{flex:1}'
          + '.snx-search{width:170px;padding:5px 10px;border-radius:8px;outline:none;'
          + 'background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.11);color:#e9e9f0;font-size:11.5px}'
          + '.snx-search::placeholder{color:rgba(233,233,240,.32)}'
          + '.snx-btn{border:1px solid rgba(255,255,255,.12);background:rgba(255,255,255,.05);'
          + 'color:#e9e9f0;border-radius:8px;padding:5px 11px;cursor:pointer;font-size:11.5px;white-space:nowrap}'
          + '.snx-btn:hover{background:rgba(167,139,250,.18);border-color:rgba(167,139,250,.45)}'
          + '.snx-close{width:30px;height:30px;display:flex;align-items:center;justify-content:center;'
          + 'border-radius:8px;cursor:pointer;opacity:.6;font-size:19px;line-height:1}'
          + '.snx-close:hover{opacity:1;background:rgba(255,255,255,.08)}'
          + '.snx-panel{position:absolute;top:68px;bottom:58px;overflow:auto;z-index:3;'
          + 'padding:12px;border-radius:12px;background:rgba(14,12,26,.72);'
          + 'border:1px solid rgba(255,255,255,.09);backdrop-filter:blur(12px)}'
          + '.snx-side{left:14px;width:206px}'
          + '.snx-detail{right:14px;width:252px}'
          + '.snx-h3{font-size:10.5px;letter-spacing:.16em;opacity:.42;margin:0 0 9px}'
          + '.snx-cluster{display:flex;align-items:center;gap:8px;padding:6px 8px;margin:2px 0;'
          + 'border-radius:8px;cursor:pointer;border:1px solid transparent;font-size:11.5px}'
          + '.snx-cluster:hover{background:rgba(255,255,255,.05)}'
          + '.snx-cluster.on{background:rgba(167,139,250,.16);border-color:rgba(167,139,250,.35)}'
          + '.snx-dot{width:8px;height:8px;border-radius:50%;flex:none}'
          + '.snx-cn{margin-left:auto;opacity:.4;font-size:10.5px}'
          + '.snx-empty{opacity:.4;font-size:11px;line-height:1.8}'
          + '.snx-legend{margin-top:16px;padding-top:12px;border-top:1px solid rgba(255,255,255,.07)}'
          + '.snx-ramp{height:6px;border-radius:3px;margin:6px 0 4px;'
          + 'background:linear-gradient(90deg,#6db3f5,#a78bfa,#f0a5c0,#f472b6)}'
          + '.snx-lbl{display:flex;justify-content:space-between;opacity:.4;font-size:10px}'
          + '.snx-dt{font-size:13px;font-weight:500;line-height:1.5;margin-bottom:8px}'
          + '.snx-kv{display:flex;justify-content:space-between;font-size:11px;margin:3px 0;opacity:.7}'
          + '.snx-sum{margin-top:10px;padding-top:9px;border-top:1px solid rgba(255,255,255,.08);'
          + 'font-size:11.5px;line-height:1.75;opacity:.82;white-space:pre-wrap;word-break:break-word}'
          + '.snx-meter{height:4px;border-radius:2px;background:rgba(255,255,255,.10);margin:3px 0 7px}'
          + '.snx-meter i{display:block;height:100%;border-radius:2px}'
          + '.snx-foot{position:absolute;left:0;right:0;bottom:0;height:44px;display:flex;align-items:center;'
          + 'gap:16px;padding:0 18px;border-top:1px solid rgba(255,255,255,.07);'
          + 'background:rgba(9,8,18,.72);backdrop-filter:blur(12px);font-size:11px;opacity:.55;z-index:4}'
          + '.snx-foot b{font-weight:500;opacity:.85}'
          + '.snx-tip{position:absolute;pointer-events:none;padding:4px 9px;border-radius:8px;'
          + 'background:rgba(16,14,28,.95);border:1px solid rgba(255,255,255,.14);display:none;'
          + 'white-space:nowrap;font-size:11px;z-index:5}'
          + '</style>'
          + '<canvas></canvas>'
          + '<div class="snx-top">'
          + '<div class="snx-title"><i>✦</i>记忆星云</div>'
          + '<div class="snx-stats" data-role="stats"></div>'
          + '<div class="snx-sp"></div>'
          + '<input class="snx-search" data-role="search" placeholder="找一件事…" />'
          + '<div class="snx-btn" data-role="reset">重置视图</div>'
          + '<div class="snx-close" data-role="close">×</div>'
          + '</div>'
          + '<div class="snx-panel snx-side" data-role="side"></div>'
          + '<div class="snx-panel snx-detail" data-role="detail"></div>'
          + '<div class="snx-foot" data-role="foot">'
          + '<span><b>拖拽</b> 平移</span><span><b>滚轮</b> 缩放</span>'
          + '<span><b>点星</b> 看它记着什么</span><span><b>点主题</b> 只看这一团</span>'
          + '<span style="margin-left:auto">Esc 关闭</span>'
          + '</div>'
          + '<div class="snx-tip" data-role="tip"></div>';
        document.body.appendChild(host);

        function q(sel) { return host.querySelector(sel); }
        var cv = q('canvas');
        var ctx = cv.getContext('2d');
        var statsEl = q('[data-role=stats]');
        var sideEl = q('[data-role=side]');
        var detailEl = q('[data-role=detail]');
        var searchEl = q('[data-role=search]');
        var tipEl = q('[data-role=tip]');

        SN.ox = 0; SN.oy = 0; SN.k = 1; SN.drag = null; SN.stars = []; SN.centers = [];
        SN.visible = false; SN.pick = null; SN.pickKey = '';
        SN.filter = -1; SN.query = ''; SN.dust = [];

        function hueOf(s) { return 200 - num(s.m, .55) * 160; }
        function clusterColor(i) {
          var hs = [265, 200, 330, 25, 155, 285, 45, 230, 310, 95, 175, 255];
          return 'hsl(' + hs[i % hs.length] + ',72%,68%)';
        }
        function matches(o) {
          if (SN.filter >= 0 && o.s.p !== SN.filter) return false;
          if (SN.query) {
            var t = (o.s.t || '').toLowerCase(), b = (o.s.s || '').toLowerCase();
            if (t.indexOf(SN.query) < 0 && b.indexOf(SN.query) < 0) return false;
          }
          return true;
        }

        // 背景星尘：确定性伪随机，避免每帧乱跳
        SN.makeDust = function (w, h) {
          var out = [], seed = 20261105;
          function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff; }
          for (var i = 0; i < 150; i++) {
            out.push({ x: rnd() * w, y: rnd() * h, r: rnd() * 1.1 + .2, a: rnd() * .32 + .05 });
          }
          SN.dust = out;
        };

        SN.layout = function () {
          var w = host.clientWidth || window.innerWidth || 1280;
          var h = host.clientHeight || window.innerHeight || 800;
          cv.width = w; cv.height = h;
          if (!SN.dust.length) SN.makeDust(w, h);

          var cx = w / 2 + 8, cy = h / 2 + 6;
          var RAD = Math.max(110, Math.min(Math.min(w, h) * .31, (w - 480) * .42));
          var clusters = SN.data.c || [];
          var centers = [];
          for (var i = 0; i < clusters.length; i++) {
            var a = -Math.PI / 2 + i * 2 * Math.PI / Math.max(1, clusters.length);
            var rr = RAD * (0.44 + 0.18 * (i % 2));
            centers.push({ x: cx + Math.cos(a) * rr, y: cy + Math.sin(a) * rr });
          }
          var seen = {};
          var stars = [];
          var list = SN.data.s || [];
          for (var j = 0; j < list.length; j++) {
            var s = list[j], x, y;
            if (s.p >= 0 && centers[s.p]) {
              var k = (seen[s.p] = (seen[s.p] || 0) + 1);
              var ang = k * 2.399;
              var rad = 10 + 4.4 * Math.sqrt(k);
              x = centers[s.p].x + Math.cos(ang) * rad;
              y = centers[s.p].y + Math.sin(ang) * rad;
            } else {
              var a2 = j * 2.399;
              var r2 = RAD * (1.30 + 0.16 * Math.sin(j * 1.7));
              x = cx + Math.cos(a2) * r2;
              y = cy + Math.sin(a2) * r2;
            }
            stars.push({ x: x, y: y, s: s, key: (s.t || '') + '|' + (s.d || '') });
          }
          SN.stars = stars;
          SN.centers = centers;

          // 数据刷新后按 key 找回选中态；记忆被删了就自然失效
          SN.pick = null;
          if (SN.pickKey) {
            for (var q1 = 0; q1 < stars.length; q1++) {
              if (stars[q1].key === SN.pickKey) { SN.pick = stars[q1]; break; }
            }
            if (!SN.pick) SN.pickKey = '';
          }

          var links = [];
          var byCluster = {};
          for (var m = 0; m < stars.length; m++) {
            var p = stars[m].s.p;
            if (p < 0) continue;
            (byCluster[p] = byCluster[p] || []).push(m);
          }
          for (var key in byCluster) {
            var list2 = byCluster[key], linkColor = clusterColor(Number(key));
            for (var a3 = 0; a3 < list2.length; a3++) {
              var best = -1, bd2 = 1e18;
              for (var b = 0; b < list2.length; b++) {
                if (a3 === b) continue;
                var dx = stars[list2[a3]].x - stars[list2[b]].x;
                var dy = stars[list2[a3]].y - stars[list2[b]].y;
                var d2 = dx * dx + dy * dy;
                if (d2 < bd2) { bd2 = d2; best = b; }
              }
              if (best >= 0 && bd2 < 90 * 90) {
                var i0 = list2[a3], i1 = list2[best];
                if (i0 < i1) links.push([i0, i1, linkColor]);
              }
            }
          }
          SN.links = links;
          // 重排可能让选中态失效（记忆被删了）或内容更新——详情必须跟着走。
          // 只在 renderUI 里刷是不够的：面板开着时外层先 renderUI 后 redraw，
          // 详情会停在旧内容上，看起来像「删了还在」。
          if (SN.renderDetail) SN.renderDetail();
        };

        SN.draw = function () {
          var w = cv.width, h = cv.height, cx = w / 2 + 8, cy = h / 2 + 6;
          ctx.clearRect(0, 0, w, h);

          // 星尘与轨道环铺在最底层，给星图一个"空间"
          for (var d = 0; d < SN.dust.length; d++) {
            var u = SN.dust[d];
            ctx.fillStyle = 'rgba(220,215,255,' + u.a + ')';
            ctx.beginPath(); ctx.arc(u.x, u.y, u.r, 0, 6.2832); ctx.fill();
          }
          ctx.save();
          ctx.setLineDash([2, 7]);
          ctx.strokeStyle = 'rgba(170,160,235,.10)';
          ctx.lineWidth = 1;
          var RAD = Math.max(110, Math.min(Math.min(w, h) * .31, (w - 480) * .42));
          for (var ring = 1; ring <= 3; ring++) {
            ctx.beginPath(); ctx.arc(cx, cy, RAD * (.45 + ring * .32), 0, 6.2832); ctx.stroke();
          }
          ctx.restore();

          ctx.save();
          ctx.translate(SN.ox, SN.oy);
          ctx.scale(SN.k, SN.k);

          var clusters = SN.data.c || [];
          for (var i = 0; i < SN.centers.length; i++) {
            var c = SN.centers[i], col = clusterColor(i);
            var dim = SN.filter >= 0 && SN.filter !== i;
            var g = ctx.createRadialGradient(c.x, c.y, 0, c.x, c.y, 140);
            g.addColorStop(0, dim ? 'rgba(120,115,170,.03)' : 'rgba(150,130,245,.15)');
            g.addColorStop(1, 'rgba(150,130,245,0)');
            ctx.fillStyle = g;
            ctx.beginPath(); ctx.arc(c.x, c.y, 140, 0, 6.2832); ctx.fill();
            ctx.fillStyle = dim ? 'rgba(190,186,220,.22)' : col;
            ctx.font = '500 12px system-ui';
            ctx.textAlign = 'center';
            ctx.fillText(clusters[i].t || '', c.x, c.y + 158);
          }

          var links = SN.links || [];
          ctx.lineWidth = .8;
          for (var L = 0; L < links.length; L++) {
            var A = SN.stars[links[L][0]], Bs = SN.stars[links[L][1]];
            if (!A || !Bs || !matches(A) || !matches(Bs)) continue;
            var hot = SN.pick && (SN.pick === A || SN.pick === Bs);
            ctx.strokeStyle = hot ? 'rgba(220,210,255,.55)' : links[L][2].replace('hsl', 'hsla').replace(')', ',.18)');
            ctx.beginPath(); ctx.moveTo(A.x, A.y); ctx.lineTo(Bs.x, Bs.y); ctx.stroke();
          }

          for (var j = 0; j < SN.stars.length; j++) {
            var o = SN.stars[j], s = o.s, on = matches(o);
            var hue = hueOf(s);
            var alpha = on ? (0.32 + 0.66 * num(s.w, .5)) : 0.07;
            var r = 1.6 + num(s.w, .5) * 3.4;
            if (on) {
              ctx.shadowBlur = 10 + num(s.w, .5) * 12;
              ctx.shadowColor = 'hsla(' + hue + ',85%,68%,.55)';
            } else {
              ctx.shadowBlur = 0;
            }
            ctx.beginPath();
            ctx.fillStyle = 'hsla(' + hue + ',78%,66%,' + alpha + ')';
            ctx.arc(o.x, o.y, r, 0, 6.2832); ctx.fill();
            ctx.shadowBlur = 0;
            if (SN.pick === o) {
              ctx.strokeStyle = 'rgba(255,255,255,.9)';
              ctx.lineWidth = 1.2;
              ctx.beginPath(); ctx.arc(o.x, o.y, r + 5, 0, 6.2832); ctx.stroke();
              ctx.strokeStyle = 'rgba(255,255,255,.28)';
              ctx.lineWidth = .8;
              ctx.beginPath();
              ctx.moveTo(o.x - r - 11, o.y); ctx.lineTo(o.x - r - 6, o.y);
              ctx.moveTo(o.x + r + 6, o.y); ctx.lineTo(o.x + r + 11, o.y);
              ctx.stroke();
            }
          }
          ctx.restore();
        };

        SN.redraw = function () { SN.layout(); SN.draw(); };

        // ── 侧栏 / 详情 / 顶栏：每轮数据到达都重画 ─────────────
        SN.renderDetail = function () {
          if (!SN.pick) {
            detailEl.innerHTML = '<div class="snx-h3">这一颗</div>'
              + '<div class="snx-empty">点一颗星，看它记着什么。<br>'
              + '颜色是当时的心情，亮度是重要与新鲜。</div>';
            return;
          }
          var s = SN.pick.s, hue = hueOf(s);
          var html = '<div class="snx-h3">这一颗</div>'
            + '<div class="snx-dt">' + esc(s.t) + '</div>'
            + '<div class="snx-kv"><span>时间</span><span>' + esc(s.d || '—') + '</span></div>'
            + '<div class="snx-kv"><span>主题</span><span>'
            + (s.p >= 0 && (SN.data.c || [])[s.p] ? esc(SN.data.c[s.p].t) : '散星') + '</span></div>'
            + '<div class="snx-kv"><span>当时的心情</span><span>' + pct(s.m) + '%</span></div>'
            + '<div class="snx-meter"><i style="width:' + pct(s.m)
            + '%;background:hsl(' + hue + ',78%,66%)"></i></div>'
            + '<div class="snx-kv"><span>亮度（重要＋新鲜）</span><span>' + pct(s.w) + '%</span></div>'
            + '<div class="snx-meter"><i style="width:' + pct(s.w)
            + '%;background:linear-gradient(90deg,#6d5fc0,#c4b5fd)"></i></div>'
            + '<div class="snx-sum">' + esc(s.s) + '</div>';
          detailEl.innerHTML = html;
        };

        SN.renderUI = function () {
          var s = SN.data.s || [], c = SN.data.c || [];
          statsEl.textContent = s.length + ' 颗星 · ' + c.length + ' 个主题 · 同步 ' + SN.syncedAt;

          var html = '<div class="snx-h3">主题 · ' + c.length + '</div>';
          if (!c.length) {
            html += '<div class="snx-empty">还没归纳出主题，星都散在外圈。<br>'
              + '多聊一阵、攒够证据后会自动成团。</div>';
          }
          for (var i = 0; i < c.length; i++) {
            html += '<div class="snx-cluster' + (SN.filter === i ? ' on' : '') + '" data-i="' + i + '">'
              + '<span class="snx-dot" style="background:' + clusterColor(i) + '"></span>'
              + '<span>' + esc(c[i].t) + '</span>'
              + '<span class="snx-cn">' + (c[i].n || 0) + '</span></div>';
          }
          html += '<div class="snx-legend"><div class="snx-h3">当时心情</div>'
            + '<div class="snx-ramp"></div><div class="snx-lbl"><span>低落 · 冷</span><span>高兴 · 暖</span></div>'
            + '<div class="snx-h3" style="margin-top:14px">亮度</div>'
            + '<div class="snx-empty">越亮越重要、越近。<br>同团的星用线连着最近的那颗。</div></div>';
          sideEl.innerHTML = html;
          SN.renderDetail();
        };

        // 事件委托：侧栏 innerHTML 会重建，绑在容器上才不会掉
        sideEl.onclick = function (e) {
          var t = e.target && e.target.getAttribute ? e.target.getAttribute('data-i') : null;
          if (t == null) return;
          var i = Number(t);
          SN.filter = (SN.filter === i) ? -1 : i;
          SN.renderUI();
          SN.draw();
        };
        searchEl.oninput = function () {
          SN.query = String(searchEl.value || '').trim().toLowerCase();
          SN.draw();
        };
        q('[data-role=reset]').onclick = function () {
          SN.ox = 0; SN.oy = 0; SN.k = 1; SN.filter = -1;
          SN.query = ''; searchEl.value = '';
          SN.renderUI(); SN.draw();
        };

        function hit(mx, my) {
          var x = (mx - SN.ox) / SN.k, y = (my - SN.oy) / SN.k;
          var best = null, bd = 1e9;
          for (var i = 0; i < SN.stars.length; i++) {
            var o = SN.stars[i];
            if (!matches(o)) continue;
            var d = (o.x - x) * (o.x - x) + (o.y - y) * (o.y - y);
            if (d < bd) { bd = d; best = o; }
          }
          return bd < 110 ? best : null;
        }

        cv.onmousemove = function (e) {
          var r = cv.getBoundingClientRect();
          var mx = e.clientX - r.left, my = e.clientY - r.top;
          if (SN.drag) {
            SN.ox += e.clientX - SN.drag.x;
            SN.oy += e.clientY - SN.drag.y;
            SN.drag = { x: e.clientX, y: e.clientY };
            SN.draw();
            return;
          }
          var o = hit(mx, my);
          if (o) {
            tipEl.style.display = 'block';
            tipEl.style.left = (mx + 14) + 'px';
            tipEl.style.top = (my + 12) + 'px';
            tipEl.textContent = o.s.t;
            cv.style.cursor = 'pointer';
          } else {
            tipEl.style.display = 'none';
            cv.style.cursor = 'grab';
          }
        };
        cv.onmousedown = function (e) { SN.drag = { x: e.clientX, y: e.clientY }; };
        cv.onmouseup = function () { SN.drag = null; };
        cv.onmouseleave = function () { SN.drag = null; tipEl.style.display = 'none'; };
        cv.onclick = function (e) {
          var r = cv.getBoundingClientRect();
          var o = hit(e.clientX - r.left, e.clientY - r.top);
          if (!o) { SN.pick = null; SN.pickKey = ''; }
          else { SN.pick = o; SN.pickKey = o.key; }
          SN.renderDetail();
          SN.draw();
        };
        cv.onwheel = function (e) {
          e.preventDefault();
          SN.k = Math.max(0.4, Math.min(3, SN.k * (e.deltaY > 0 ? 0.92 : 1.08)));
          SN.draw();
        };

        SN.close = function () {
          host.style.display = 'none';
          SN.visible = false;
          tipEl.style.display = 'none';
        };
        SN.open = function () {
          SN.hideCard();
          host.style.display = 'block';
          SN.visible = true;
          SN.ox = 0; SN.oy = 0; SN.k = 1;
          SN.redraw();
          SN.renderUI();
        };
        q('[data-role=close]').onclick = SN.close;
        btn.onclick = SN.open;
        document.addEventListener('keydown', function (e) {
          if (e.key === 'Escape' && SN.visible) SN.close();
        });
        window.addEventListener('resize', function () {
          SN.dust = [];
          if (SN.visible) SN.redraw();
        });
        SN.renderUI();
        SN.close();
        return 'ok';
        """;

    public void Dispose()
    {
        disposed = true;
        RefreshRequested -= OnRefreshRequested;
        timer?.Dispose();
        timer = null;
        try
        {
            _ = JS.InvokeAsync<string>("eval", CleanupScript);
        }
        catch
        {
            // 页面可能已经关了
        }
    }
}
