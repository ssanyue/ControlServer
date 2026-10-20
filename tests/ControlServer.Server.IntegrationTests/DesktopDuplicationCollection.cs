namespace ControlServer.Server.IntegrationTests;

/// <summary>
/// <b>所有需要独占桌面 duplication 的冒烟用例，共用这一个 collection。</b>
///
/// <para>
/// 🔥🔥 <b>这个文件存在的理由是一个真实的"偶发红"</b>：Windows 同一时刻
/// <b>只允许一个进程对一块 output 建 duplication</b>。而 xunit 默认让
/// <b>不同测试类并行</b> —— 于是四个都真的建 duplication 的冒烟类（抓屏、色彩转换、
/// 指针叠加、硬件编码）会互相抢同一块屏，谁后到谁拿到
/// <c>E_ACCESSDENIED (0x80070005)</c>。
/// </para>
///
/// <para>
/// 症状极易误诊：<b>单跑那一个类 8/8 全绿，全量跑偶发一到三条红</b>，
/// 而失败信息说的是"DXGI 抓屏失败" —— 一条与真因（测试自己抢自己）完全无关的话。
/// 本仓在 M5.5 之前一直把它记成"GPU 裁剪未证明"，就是被这个形状骗的。
/// </para>
///
/// <para>
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> 让这个 collection
/// 与其它 collection <b>也不并行</b>：桌面 duplication 是<b>进程内 + 全机</b>的独占资源，
/// 只跟"同 collection 串行"还不够（别的类一样会来抢）。
/// </para>
///
/// <para>
/// ⚠️ 新增任何一个真的建 duplication 的测试类，<b>必须</b>挂上这个 collection ——
/// 否则偶发红会回来，而且看起来仍然像产品缺陷。
/// </para>
///
/// <para>
/// 🔥🔥 <b>怎么判断"真的建了 duplication"</b>：<b>不能</b>只 grep
/// <c>new WindowsDxgiScreenCapturer</c> —— <c>WindowsGpuFrameEncoder.TryCreate</c>
/// 会在内部建一个，而调用它的测试类从字面上完全看不出这件事。
/// 本文件第一次被引入时正是靠这条教训补上的：<c>WindowsGpuFrameEncoderSmokeTests</c>
/// 就因为这个盲点漏在外面，然后以一条"灰度切换没有立刻开新 GOP"的失败出现 ——
/// 一条与真因（并行抢屏）毫无关系的断言。
/// 所以判据是：<b>只要这个类会走到抓屏（直接或间接），就必须挂进来。</b>
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopDuplicationCollection
{
    /// <summary>collection 名（各测试类用 <c>[Collection(DesktopDuplicationCollection.Name)]</c> 引用）。</summary>
    public const string Name = "desktop-duplication";
}
