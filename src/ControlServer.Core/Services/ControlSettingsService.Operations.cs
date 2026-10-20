using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>
/// <see cref="ControlSettingsService"/> 的配置操作部分：
/// 按钮 / 子按钮集 / 子按钮的增删改复制、排序、清空与恢复出厂。
/// <para>
/// <b>实现约定</b>：全部经由 <see cref="ControlSettingsService.MutateAsync"/> 走「锁内读 → 改 → 归一化 → 写」，
/// 因此天然满足乐观锁语义，且每次写入后 Order 都会被重新编号 0..N-1。
/// </para>
/// </summary>
public sealed partial class ControlSettingsService
{
    // ═══════════════════════════════════════════════════════════
    // 按钮
    // ═══════════════════════════════════════════════════════════

    /// <summary>新增按钮（自动排到末尾）。</summary>
    public Task<ControlSettingsView> AddButtonAsync(
        ButtonConfig button,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(button);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            button.Order = settings.Buttons.Count + settings.SubButtonSets.Count;
            settings.Buttons.Add(button);
        }, $"新增按钮「{button.Label}」", cancellationToken);
    }

    /// <summary>更新按钮（按 Id 定位；不存在则新增）。</summary>
    public Task<ControlSettingsView> UpsertButtonAsync(
        ButtonConfig button,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(button);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            var index = settings.Buttons.FindIndex(b => b.Id == button.Id);
            if (index >= 0)
            {
                settings.Buttons[index] = button;
            }
            else
            {
                button.Order = settings.Buttons.Count + settings.SubButtonSets.Count;
                settings.Buttons.Add(button);
            }
        }, $"保存按钮「{button.Label}」", cancellationToken);
    }

    /// <summary>删除按钮。</summary>
    public Task<ControlSettingsView> DeleteButtonAsync(
        string buttonId,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            settings.Buttons.RemoveAll(b => b.Id == buttonId);
        }, $"删除按钮 {buttonId}", cancellationToken);
    }

    /// <summary>复制按钮（深拷贝 + 新 Id，插到原按钮之后）。</summary>
    public Task<ControlSettingsView> DuplicateButtonAsync(
        string buttonId,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            var index = settings.Buttons.FindIndex(b => b.Id == buttonId);
            if (index < 0) return;

            var clone = ConfigCloner.CloneButton(settings.Buttons[index]);
            clone.Label += " 副本";

            // 插到原按钮之后：把它与后续所有元素的 Order 各 +1，再占用原 Order+1。
            var insertOrder = settings.Buttons[index].Order + 1;
            foreach (var b in settings.Buttons) if (b.Order >= insertOrder) b.Order++;
            foreach (var s in settings.SubButtonSets) if (s.Order >= insertOrder) s.Order++;
            clone.Order = insertOrder;

            settings.Buttons.Insert(index + 1, clone);
        }, $"复制按钮 {buttonId}", cancellationToken);
    }

    /// <summary>清空所有按钮（不动子按钮集、图片与设置）。</summary>
    public Task<ControlSettingsView> ClearButtonsAsync(
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
        => MutateAsync(expectedVersion, updatedBy, settings =>
        {
            settings.Buttons.Clear();
        }, "清空所有按钮", cancellationToken);

    // ═══════════════════════════════════════════════════════════
    // 子按钮集
    // ═══════════════════════════════════════════════════════════

    /// <summary>新增子按钮集（自动排到末尾）。</summary>
    public Task<ControlSettingsView> AddSubButtonSetAsync(
        SubButtonSet set,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            set.Order = settings.Buttons.Count + settings.SubButtonSets.Count;
            settings.SubButtonSets.Add(set);
        }, $"新增子按钮集「{set.Label}」", cancellationToken);
    }

    /// <summary>更新子按钮集（按 Id 定位；不存在则新增）。</summary>
    public Task<ControlSettingsView> UpsertSubButtonSetAsync(
        SubButtonSet set,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            var index = settings.SubButtonSets.FindIndex(s => s.Id == set.Id);
            if (index >= 0)
            {
                settings.SubButtonSets[index] = set;
            }
            else
            {
                set.Order = settings.Buttons.Count + settings.SubButtonSets.Count;
                settings.SubButtonSets.Add(set);
            }
        }, $"保存子按钮集「{set.Label}」", cancellationToken);
    }

    /// <summary>删除子按钮集（连同其内全部子按钮）。</summary>
    public Task<ControlSettingsView> DeleteSubButtonSetAsync(
        string setId,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            settings.SubButtonSets.RemoveAll(s => s.Id == setId);
        }, $"删除子按钮集 {setId}", cancellationToken);
    }

    /// <summary>复制子按钮集（深拷贝 + 全部新 Id，插到原集合之后）。</summary>
    public Task<ControlSettingsView> DuplicateSubButtonSetAsync(
        string setId,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            var index = settings.SubButtonSets.FindIndex(s => s.Id == setId);
            if (index < 0) return;

            var clone = ConfigCloner.CloneSubButtonSet(settings.SubButtonSets[index]);
            clone.Label += " 副本";

            var insertOrder = settings.SubButtonSets[index].Order + 1;
            foreach (var b in settings.Buttons) if (b.Order >= insertOrder) b.Order++;
            foreach (var s in settings.SubButtonSets) if (s.Order >= insertOrder) s.Order++;
            clone.Order = insertOrder;

            settings.SubButtonSets.Insert(index + 1, clone);
        }, $"复制子按钮集 {setId}", cancellationToken);
    }

    /// <summary>清空所有子按钮集（含集内全部子按钮）。</summary>
    public Task<ControlSettingsView> ClearSubButtonSetsAsync(
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
        => MutateAsync(expectedVersion, updatedBy, settings =>
        {
            settings.SubButtonSets.Clear();
        }, "清空所有子按钮集", cancellationToken);

    // ═══════════════════════════════════════════════════════════
    // 子按钮
    // ═══════════════════════════════════════════════════════════

    /// <summary>在指定集合内新增 / 更新子按钮。</summary>
    public Task<ControlSettingsView> UpsertSubButtonAsync(
        string setId,
        SubButton subButton,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        ArgumentNullException.ThrowIfNull(subButton);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            var set = settings.SubButtonSets.FirstOrDefault(s => s.Id == setId);
            if (set is null) return;

            var index = set.SubButtons.FindIndex(s => s.Id == subButton.Id);
            if (index >= 0)
            {
                set.SubButtons[index] = subButton;
            }
            else
            {
                set.SubButtons.Add(subButton);
            }
        }, $"保存子按钮「{subButton.Label}」", cancellationToken);
    }

    /// <summary>删除指定集合内的子按钮。</summary>
    public Task<ControlSettingsView> DeleteSubButtonAsync(
        string setId,
        string subButtonId,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subButtonId);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            var set = settings.SubButtonSets.FirstOrDefault(s => s.Id == setId);
            set?.SubButtons.RemoveAll(s => s.Id == subButtonId);
        }, $"删除子按钮 {subButtonId}", cancellationToken);
    }

    // ═══════════════════════════════════════════════════════════
    // 排序 / 维护
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 统一重排序：<paramref name="orderedIds"/> 是按钮与子按钮集的**混合** Id 序列（按新顺序）。
    /// 未出现在列表中的元素保持原有相对次序，排在后面。
    /// </summary>
    public Task<ControlSettingsView> ReorderAsync(
        IReadOnlyList<string> orderedIds,
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        return MutateAsync(expectedVersion, updatedBy, settings =>
        {
            // 先按「给定顺序 → 原先 Order → 按钮优先」算出一个名次，再回写 Order。
            var rank = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < orderedIds.Count; i++)
            {
                rank[orderedIds[i]] = i;
            }

            var fallbackBase = orderedIds.Count;

            foreach (var b in settings.Buttons)
            {
                b.Order = rank.TryGetValue(b.Id, out var r) ? r : fallbackBase + b.Order;
            }

            foreach (var s in settings.SubButtonSets)
            {
                s.Order = rank.TryGetValue(s.Id, out var r) ? r : fallbackBase + s.Order;
            }
        }, "重排按钮 / 子按钮集", cancellationToken);
    }

    /// <summary>清除全部自定义图片（默认按钮图 + 每个按钮 / 集合 / 子按钮的图），并清空「统一默认图」。</summary>
    public Task<ControlSettingsView> ClearCustomImagesAsync(
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
        => MutateAsync(expectedVersion, updatedBy, settings =>
        {
            settings.DefaultButtonImage = "";

            foreach (var b in settings.Buttons)
            {
                b.ButtonImage = "";
            }

            foreach (var s in settings.SubButtonSets)
            {
                s.ButtonImage = "";
                foreach (var sub in s.SubButtons)
                {
                    sub.ButtonImage = "";
                }
            }
        }, "清除自定义图片", cancellationToken);

    /// <summary>恢复出厂：清空按钮 / 子按钮集 / 图片与外观项，回到首次启动状态。</summary>
    public Task<ControlSettingsView> FactoryResetAsync(
        int? expectedVersion,
        string? updatedBy,
        CancellationToken cancellationToken = default)
        => MutateAsync(expectedVersion, updatedBy, settings =>
        {
            settings.AppName = "中控";
            settings.BackgroundImagePath = "";
            settings.DefaultButtonImage = "";
            settings.FreeLayoutEnabled = false;
            settings.ListenPort = ControlSettingsNormalizer.DefaultListenPort;
            settings.Buttons.Clear();
            settings.SubButtonSets.Clear();
        }, "恢复出厂设置", cancellationToken);

    /// <summary>数量统计（设置页展示用）。</summary>
    public async Task<(int ButtonCount, int SetCount, int SubButtonCount)> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        var view = await GetAsync(cancellationToken).ConfigureAwait(false);
        var sub = 0;
        foreach (var set in view.Settings.SubButtonSets)
        {
            sub += set.SubButtons.Count;
        }

        return (view.Settings.Buttons.Count, view.Settings.SubButtonSets.Count, sub);
    }
}
