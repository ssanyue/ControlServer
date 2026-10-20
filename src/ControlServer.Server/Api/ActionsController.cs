using ControlServer.Contracts;
using ControlServer.Core.Models;
using ControlServer.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.Server.Api;

/// <summary>
/// 动作端点（M2-B）：临时执行动作 / 预览编码结果。
/// <para>
/// 语义上 <b>不修改配置</b> —— 用于编辑页的「测试发送」与「对接码联调」。
/// 真正"执行已保存按钮"的 <c>POST /api/buttons/{id}/execute</c> 在 M2-F 收口，
/// 那时只是把本控制器已有的能力套一层薄壳。
/// </para>
/// <para>
/// 支持两种粒度：<b>整组</b>（<c>buttonId</c> / <c>subButtonId</c>）与
/// <b>单条</b>（叠加 <c>actionId</c>）—— 编辑页每条动作右侧的「执行」按钮走的是
/// 内联路径（草稿可能尚未保存），脚本 / 运维则可用 <c>actionId</c> 按已保存配置精确触发。
/// </para>
/// </summary>
[ApiController]
[Route("api/actions")]
[Authorize(Policy = Policies.CanOperate)]
public sealed class ActionsController : ControllerBase
{
    private readonly ActionExecutionService _execution;
    private readonly ControlSettingsService _settings;

    public ActionsController(ActionExecutionService execution, ControlSettingsService settings)
    {
        _execution = execution;
        _settings = settings;
    }

    /// <summary>
    /// 执行一组动作（或按 <c>actionId</c> 只执行其中一条）。
    /// <para>
    /// 请求体用法：<c>{ "buttonId": "..." }</c> / <c>{ "subButtonId": "..." }</c>
    /// （按已保存配置执行整组）、再叠加 <c>"actionId"</c> 可只执行其中一条；
    /// 或 <c>{ "actions": [ ... ] }</c>（内联，测试发送 —— 编辑草稿尚未保存时用这条）。
    /// </para>
    /// </summary>
    [HttpPost("execute")]
    public async Task<IActionResult> Execute(
        [FromBody] ActionExecuteRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "请求体不能为空。" });

        // actionId 是「限定范围」而不是「来源」：光有一个动作 Id，服务端无从判断它属于哪个按钮，
        // 猜一个等于凭空执行任意数据。所以要跟宿主按钮一起给。
        if (!string.IsNullOrWhiteSpace(request.ActionId)
            && string.IsNullOrWhiteSpace(request.ButtonId)
            && string.IsNullOrWhiteSpace(request.SubButtonId))
        {
            return BadRequest(new
            {
                message = "actionId 必须与 buttonId 或 subButtonId 一起使用（用于在已保存配置里定位那一条动作）。",
            });
        }

        // ── 组合按钮：走**另一条**执行链（2026-10-15，需求⑤）──
        //
        // ⚠️ 放在最前面：一旦给了 comboId，后面那套"挑动作 / override"的语义全部不适用
        //    （组合的每一段用它自己那个按钮的动作与它自己的目标地址）。
        if (!string.IsNullOrWhiteSpace(request.ComboId))
        {
            if (!string.IsNullOrWhiteSpace(request.ButtonId) || !string.IsNullOrWhiteSpace(request.SubButtonId))
            {
                return BadRequest(new
                {
                    message = "comboId 不能与 buttonId / subButtonId 同时使用：组合是「一串按钮」，"
                              + "同时指定会让「到底执行了什么」变得含糊。",
                });
            }

            var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var combo = view.Settings.ComboButtons.FirstOrDefault(c => c.Id == request.ComboId);

            if (combo is null)
            {
                return NotFound(new { message = $"未找到组合按钮「{request.ComboId}」。" });
            }

            // 用**当前配置**解析成员（悬空成员会被剔除）——与对接码那条路走的是同一个纯函数。
            var plan = ComboResolver.Resolve(combo, view.Settings.Buttons);

            var comboResult = await _execution
                .ExecuteComboAsync(plan, cancellationToken)
                .ConfigureAwait(false);

            return Ok(ActionDtoMapper.ToDto(comboResult));
        }

        IReadOnlyList<ButtonAction> actions;
        string label;
        string? overrideIp = request.OverrideIp;
        var overridePort = request.OverridePort;

        if (!string.IsNullOrWhiteSpace(request.ButtonId))
        {
            var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            var button = view.Settings.Buttons.FirstOrDefault(b => b.Id == request.ButtonId);
            if (button is null)
            {
                return NotFound(new { message = $"未找到按钮「{request.ButtonId}」。" });
            }

            var picked = SelectActions(button.Actions ?? new List<ButtonAction>(), request.ActionId);
            if (picked is null)
            {
                return NotFound(new
                {
                    message = $"按钮「{button.Label}」里没有动作「{request.ActionId}」（可能已被删除，或尚未保存）。",
                });
            }

            actions = picked;
            label = string.IsNullOrWhiteSpace(request.Label)
                ? DefaultLabel(button.Label, request.ActionId, actions)
                : request.Label!;
        }
        else if (!string.IsNullOrWhiteSpace(request.SubButtonId))
        {
            var view = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

            SubButtonSet? ownerSet = null;
            SubButton? sub = null;
            foreach (var set in view.Settings.SubButtonSets)
            {
                var hit = set.SubButtons?.FirstOrDefault(s => s.Id == request.SubButtonId);
                if (hit is null) continue;
                ownerSet = set;
                sub = hit;
                break;
            }

            if (ownerSet is null || sub is null)
            {
                return NotFound(new { message = $"未找到子按钮「{request.SubButtonId}」。" });
            }

            var picked = SelectActions(sub.Actions ?? new List<ButtonAction>(), request.ActionId);
            if (picked is null)
            {
                return NotFound(new
                {
                    message = $"子按钮「{sub.Label}」里没有动作「{request.ActionId}」（可能已被删除，或尚未保存）。",
                });
            }

            actions = picked;
            label = string.IsNullOrWhiteSpace(request.Label)
                ? DefaultLabel(sub.Label, request.ActionId, actions)
                : request.Label!;

            // 子按钮语义：IP/端口一律由所属集合提供（客户端传的 override 不允许"逃逸"）。
            overrideIp = ownerSet.TargetIp;
            overridePort = ownerSet.TargetPort;
        }
        else
        {
            var inline = request.Actions;
            if (inline is null || inline.Count == 0)
            {
                return BadRequest(new { message = "请提供 buttonId、subButtonId 或至少一条 actions。" });
            }

            actions = inline.Select(ToAction).ToList();
            label = string.IsNullOrWhiteSpace(request.Label)
                ? DefaultLabel("临时动作", null, actions)
                : request.Label!;
        }

        var result = await _execution
            .ExecuteAsync(actions, label, overrideIp, overridePort, cancellationToken)
            .ConfigureAwait(false);

        return Ok(ActionDtoMapper.ToDto(result));
    }

    /// <summary>
    /// 预览编码结果：把「数据类型 + 内容」编码成字节，返回 Hex 与按该类型还原的文本。
    /// <para>
    /// 编码失败（如 Hex 长度为奇数）不返回 4xx —— 这是「查编码结果」的接口，
    /// 失败本身就是要展示的信息，因此放在 <c>error</c> 字段里返回 200。
    /// </para>
    /// </summary>
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] EncodePreviewRequest? request)
    {
        var dataType = string.IsNullOrWhiteSpace(request?.DataType) ? DataTypes.String : request!.DataType!;
        var content = request?.DataContent ?? string.Empty;

        var ok = DataEncoder.TryEncode(dataType, content, out var bytes, out var error);

        var preview = ok
            ? DataEncoder.Preview(dataType, bytes, content)
            : string.Empty;

        var dto = new EncodePreviewDto(
            dataType,
            preview,
            DataEncoder.ToSpacedHex(bytes),
            DataEncoder.ToHexString(bytes),
            bytes.Length,
            DataEncoder.AnsiEncodingName,
            error);

        return Ok(dto);
    }

    // ═══════════════════════════════════════════════════════════
    // 映射
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 从一组动作里挑出要执行的那些。
    /// <para>
    /// <paramref name="actionId"/> 为空 ⇒ 整组原样返回（可能是空列表，那是合法的"没有可执行的动作"，
    /// 由 <c>ActionExecutionService</c> 返回 0 条结果，<b>不是</b> 404）；
    /// 指定了却找不到 ⇒ 返回 <c>null</c>，由调用方转 404。
    /// 这两种"空"的含义完全不同，所以不能合并成一个返回值。
    /// </para>
    /// </summary>
    private static IReadOnlyList<ButtonAction>? SelectActions(
        IReadOnlyList<ButtonAction> all,
        string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return all;

        var hit = all.FirstOrDefault(a => string.Equals(a.Id, actionId, StringComparison.Ordinal));
        return hit is null ? null : new[] { hit };
    }

    /// <summary>
    /// 结果里的展示名。
    /// <para>
    /// 单条执行时用「宿主名 · 动作名」——单条执行与整组执行在收发日志里长得一样，
    /// 只有名字能区分，不然后面排查"这条到底是谁发的"会很难受。
    /// </para>
    /// </summary>
    private static string DefaultLabel(
        string ownerLabel,
        string? actionId,
        IReadOnlyList<ButtonAction> actions)
    {
        var owner = string.IsNullOrWhiteSpace(ownerLabel) ? "未命名" : ownerLabel;

        if (string.IsNullOrWhiteSpace(actionId) || actions.Count != 1) return owner;

        var one = actions[0];
        var actionName = string.IsNullOrWhiteSpace(one.Name) ? one.Protocol : one.Name;
        return $"{owner} · {actionName}";
    }

    /// <summary>
    /// 内联 DTO → 领域动作。**未提供的字段保留模型默认值**（与旧版"新建动作"一致），
    /// 而不是填 null / 空串，避免前端漏字段导致行为漂移。
    /// </summary>
    private static ButtonAction ToAction(ActionExecuteItem item)
    {
        var action = new ButtonAction();

        if (item.Name is not null) action.Name = item.Name;
        if (item.TargetIp is not null) action.TargetIp = item.TargetIp;
        if (item.TargetPort is int port) action.TargetPort = port;
        if (item.Protocol is not null) action.Protocol = item.Protocol;
        if (item.DataType is not null) action.DataType = item.DataType;
        if (item.DataContent is not null) action.DataContent = item.DataContent;
        if (item.DelayMs is int delay) action.DelayMs = delay;
        if (item.PairingCode is not null) action.PairingCode = item.PairingCode;
        if (item.PairingCodeType is not null) action.PairingCodeType = item.PairingCodeType;

        return action;
    }
}
