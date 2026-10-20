using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// 扫描配置里对某张图片的引用（M2-G1）。
/// <para>
/// <b>为什么需要它</b>：一张图可以被多处引用 —— 「默认按钮图片」天然是全体的兜底、
/// 复制按钮会共享同一张图、子按钮集与其子按钮也可能用同一张。
/// 若无条件删除，被引用的那些按钮会**静默**退化成纯色瓦片（前端对 404 的处理是回退，不报错），
/// 用户看到的是"我删了一张没用的图，怎么别的按钮的图也没了"。
/// 删除端点据此在"仍被引用"时返回 409 并列出引用方，由用户决定是否强行删除。
/// </para>
/// <para>
/// <b>匹配口径</b>：按**文件名的最后一段**比较（忽略大小写），而不是整串绝对路径。
/// 这必须与前端 <c>format.ts</c> 的 <c>imageUrl()</c> 以及
/// <c>GET /api/config/images/{fileName}</c> 的口径完全一致 ——
/// 三处若用了不同的匹配方式，"能显示的图"与"被认为被引用的图"就会错位。
/// </para>
/// </summary>
public static class ImageReferenceScanner
{
    /// <summary>
    /// 找出配置里所有引用 <paramref name="fileName"/> 的位置，返回**给人看的**描述。
    /// 无引用时返回空列表（调用方据 <c>Count == 0</c> 判定可以安全删除）。
    /// </summary>
    /// <param name="settings">当前配置。</param>
    /// <param name="fileName">服务端生成的文件名（如 <c>img3f2a….png</c>）。</param>
    public static IReadOnlyList<string> FindReferrers(ControlSettings? settings, string? fileName)
    {
        if (settings is null || string.IsNullOrWhiteSpace(fileName)) return Array.Empty<string>();

        // 🔥 走同一个字典，而不是在这里再写一遍四个字段的遍历：
        //    "删除端点认为没人用" 与 "对账认为没人用" 只要有一处漏了新字段，
        //    结果就是**删掉一张正在使用的图**（前端对 404 是静默回退，用户只看到图没了）。
        return CollectReferences(settings).TryGetValue(fileName.Trim(), out var referrers)
            ? referrers
            : Array.Empty<string>();
    }

    /// <summary>
    /// 配置里**所有**被引用的图片文件名 → 引用方描述（<b>保序</b>：背景 → 默认按钮 → 按钮 → 子按钮集 → 子按钮）。
    ///
    /// <para>
    /// 🔥 <b>为什么需要"正向"这一份</b>：图片对账（<c>ImageRepositoryGroomer</c>）要回答两个相反的问题 ——
    /// 盘上这个文件<b>还有没有人用</b>（→ 孤儿），以及配置里这张图<b>盘上还在不在</b>（→ 断链）。
    /// 后者必须从"配置引用了什么"出发，只靠逐文件调 <see cref="FindReferrers"/> 是枚举不出来的
    /// （配置里写了一个盘上根本没有的文件名，逐文件扫永远扫不到它）。
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>新增图片字段时必须加在这里</b>：这是全仓唯一一处"配置里哪些字面量是图片引用"的定义。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> CollectReferences(ControlSettings? settings)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (settings is null)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        Add(settings.BackgroundImagePath, "背景图片");
        Add(settings.DefaultButtonImage, "默认按钮图片");

        foreach (var button in settings.Buttons)
        {
            Add(button.ButtonImage, $"按钮「{Label(button.Label, "未命名按钮")}」");
        }

        foreach (var set in settings.SubButtonSets)
        {
            Add(set.ButtonImage, $"子按钮集「{Label(set.Label, "未命名集合")}」");

            foreach (var sub in set.SubButtons)
            {
                Add(sub.ButtonImage, $"子按钮「{Label(sub.Label, "未命名子按钮")}」");
            }
        }

        return map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);

        void Add(string? configuredPath, string referrer)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return;

            // 与前端 imageUrl() 以及 GET /api/config/images/{fileName} 同一口径：按最后一段比较。
            var name = Path.GetFileName(configuredPath.Trim());

            if (string.IsNullOrWhiteSpace(name)) return;

            if (!map.TryGetValue(name, out var list))
            {
                list = [];
                map[name] = list;
            }

            list.Add(referrer);
        }
    }

    /// <summary>标签为空时给一个可读占位，避免出现「按钮「」」这种空引号。</summary>
    private static string Label(string? label, string fallback)
        => string.IsNullOrWhiteSpace(label) ? fallback : label.Trim();
}
