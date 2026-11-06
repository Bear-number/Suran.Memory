using System;
using System.Collections.Generic;
using System.Linq;

namespace Suran.Memory;

/// <summary>
/// 检索的情绪偏置。纯函数，不依赖宿主——这样它能脱离 Alife 单测，
/// 也能被"记忆"以外的插件直接拿去用。
///
/// 它做的事只有一件：给每条存档乘一个 1±strength 的系数。
/// 乘出来的是顺序，不是取舍——召回边界仍然由关键词和过滤器决定。
/// </summary>
public static class MemoryBias
{
    /// <summary>
    /// 情绪一致性：此刻心情离某条存档"当时的心情"越近，越容易被想起。
    ///
    /// 这不是玄学——人本来就这样，难过的时候先想起来的是难过的事。
    /// 系数限制在 1±strength，不该让一条情绪相近但关键词无关的存档
    /// 压过一条真正命中的。没有快照的条目一律 1.0，不奖不罚。
    /// </summary>
    public static Func<MemoryItem, double> Of(double? nowMood, double strength)
    {
        if (nowMood is not { } now || strength <= 0)
            return _ => 1.0;

        double s = Math.Clamp(strength, 0, 0.6);
        return item => {
            if (item.StateMood is not { } then)
                return 1.0;
            double match = 1.0 - Math.Abs(now - then);   // 0..1
            return 1.0 + s * (2 * match - 1);
        };
    }
}
