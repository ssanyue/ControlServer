using ControlServer.Core.Models;

namespace ControlServer.Core.Services;

/// <summary>一个「盘上有、配置里没人引用」的图片文件（孤儿）。</summary>
/// <param name="Name">文件名（单段，无路径）。</param>
/// <param name="Bytes">文件长度。</param>
/// <param name="LastWriteUtc">最后写入时刻（UTC）。判龄只用它。</param>
public sealed record ImageOrphan(string Name, long Bytes, DateTimeOffset LastWriteUtc);

/// <summary>
/// 一条「配置里引用了、盘上没有」的图片（断链）。
/// </summary>
/// <param name="Name">配置里写的文件名。</param>
/// <param name="Referrers">谁在引用它（人话描述，可能多处）。</param>
public sealed record ImageLost(string Name, IReadOnlyList<string> Referrers);

/// <summary>
/// 一次图片对账的结论。<b>只读</b> —— 这里不删任何东西。
/// </summary>
/// <param name="DiskFiles"><c>images/</c> 下的文件数（只数顶层，不含 <c>.tmp</c>）。</param>
/// <param name="DiskBytes"><c>images/</c> 下的实际字节（不含 <c>.tmp</c>）。</param>
/// <param name="Orphans">可回收的孤儿（<b>已按宽限期过滤</b>）。</param>
/// <param name="StaleTemps">残留的 <c>.tmp</c>（原子写的中间态，<b>已按宽限期过滤</b>）。</param>
/// <param name="Lost">配置引用了但盘上没有的图（<b>永不自动处理</b>，只报警）。</param>
/// <param name="UnexpectedDirectories"><c>images/</c> 下出现的子目录（<b>不碰</b>，只报）。</param>
public sealed record ImageSurvey(
    int DiskFiles,
    long DiskBytes,
    IReadOnlyList<ImageOrphan> Orphans,
    IReadOnlyList<ImageOrphan> StaleTemps,
    IReadOnlyList<ImageLost> Lost,
    IReadOnlyList<string> UnexpectedDirectories)
{
    /// <summary>孤儿合计字节（"能腾出多少"）。</summary>
    public long OrphanBytes => Orphans.Sum(o => o.Bytes);

    /// <summary>残留 <c>.tmp</c> 合计字节。</summary>
    public long StaleTempBytes => StaleTemps.Sum(o => o.Bytes);

    /// <summary>有没有任何值得动手/值得报警的东西。</summary>
    public bool HasFindings => Orphans.Count > 0 || StaleTemps.Count > 0 || Lost.Count > 0
                               || UnexpectedDirectories.Count > 0;
}

/// <summary>
/// <b>图片目录（<c>images/</c>）的对账</b>（M7.1 补：此前只对账了 <c>files/</c>）。
///
/// <para>
/// <b>为什么图片那半边不能不管</b>：孤儿图片与孤儿仓库文件产生的机理不同，但后果一样 ——
/// 目录只增不减，而且<b>没有任何人会去看它</b>（页面上图片正常显示，说明"看起来没事"）。
/// 图片孤儿有三条来路：
/// </para>
/// <list type="number">
/// <item>用户上传了图、没保存配置就离开（<c>ImageStorageService.Save</c> 已经落盘，配置里没提过它）；</item>
/// <item>用户换了一张图，旧图从配置里被替换掉，但文件还在；</item>
/// <item>删按钮 / 删子按钮集时只改配置，不删文件。</item>
/// </list>
///
/// <para>
/// 🔴 <b>断链（<see cref="ImageSurvey.Lost"/>）必须报警，而且不能只靠"页面看起来不对"发现</b>：
/// 前端对图片 404 的处理是<b>静默回退成纯色瓦片</b>，用的还是同一个按钮名字 ——
/// 现场看到的只是"这个按钮的图怎么没了"，不会有人想到去查数据目录。
/// 它与文件仓库的断链同理：<b>删掉引用等于替用户放弃</b>，所以只报不删。
/// </para>
///
/// <para>
/// 🔥 <b>宽限期是必须的，不是保守</b>：上传的落盘与"配置里写上这个名字"是
/// <b>两次独立的写</b>（先存盘拿到文件名，再由用户点保存）。中间这段时间里，
/// 刚上传的图在配置里<b>必然</b>还没有引用 —— 照"没人引用就删"会删掉用户刚传的图，
/// 而那时他正在编辑页面上看着它。
/// </para>
///
/// <para>
/// ⚠️ <b>判龄一律 UTC</b>：与 <see cref="RepositoryGroomer"/> 同一条规矩，
/// 用本地时间在非 +08:00 的部署上会整体偏几小时（偏早误删、偏晚永不回收，两种都不报错）。
/// </para>
/// </summary>
public static class ImageRepositoryGroomer
{
    /// <summary>原子写的临时文件后缀（见 <c>ImageStorageService</c> 的 <c>dest + ".tmp"</c>）。</summary>
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// 扫一遍图片目录，给出结论。<b>不改任何东西</b>（连日志都不写）。
    /// </summary>
    /// <param name="settings">当前配置（决定"哪些图还有人用"）。<c>null</c> = 配置读不出来 ⇒ 一律不判孤儿。</param>
    /// <param name="imagesDirectory"><c>images/</c> 的绝对路径。</param>
    /// <param name="nowUtc">判龄基准，<b>必须是 UTC</b>。</param>
    /// <param name="gracePeriod">宽限期：比它新的文件一律不碰。</param>
    public static ImageSurvey Scan(
        ControlSettings? settings,
        string imagesDirectory,
        DateTime nowUtc,
        TimeSpan gracePeriod)
    {
        var references = ImageReferenceScanner.CollectReferences(settings);

        // 🔴 配置读不出来时**一个孤儿都不判**：那时"没人引用"只说明我们不知道谁在引用，
        //    不能推出"可以删"。文件仓库那边没有这个风险（它的依据是库里的记录），图片这边有。
        var canJudge = settings is not null;

        var cutoffUtc = nowUtc - gracePeriod;

        var orphans = new List<ImageOrphan>();
        var staleTemps = new List<ImageOrphan>();
        var unexpectedDirs = new List<string>();

        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        long bytes = 0;
        var files = 0;

        foreach (var file in EnumerateTopLevelFiles(imagesDirectory))
        {
            if (file.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // 残留的临时文件：它**不是**一张能用图片，任何宽限之后都可以删。
                if (file.LastWriteTimeUtc <= cutoffUtc)
                {
                    staleTemps.Add(new ImageOrphan(file.Name, file.Length, AsUtc(file.LastWriteTimeUtc)));
                }

                continue;
            }

            files++;
            bytes += file.Length;
            onDisk.Add(file.Name);

            if (!canJudge) continue;
            if (references.ContainsKey(file.Name)) continue;

            // 太新的不碰：可能正处在"已落盘、配置还没保存"的窗口里。
            if (file.LastWriteTimeUtc > cutoffUtc) continue;

            orphans.Add(new ImageOrphan(file.Name, file.Length, AsUtc(file.LastWriteTimeUtc)));
        }

        foreach (var dir in EnumerateTopLevelDirectories(imagesDirectory))
        {
            unexpectedDirs.Add(dir.Name);
        }

        // ── 断链：配置里引用了、盘上没有 ──
        // ⚠️ 这里**不判宽限期**：引用已经写进配置了，盘上没有就是没有。
        var lost = new List<ImageLost>();
        foreach (var pair in references)
        {
            if (onDisk.Contains(pair.Key)) continue;

            lost.Add(new ImageLost(pair.Key, pair.Value));
        }

        return new ImageSurvey(files, bytes, orphans, staleTemps, lost, unexpectedDirs);
    }

    /// <summary>
    /// 按一份已经算好的结论删掉孤儿与残留 <c>.tmp</c>。
    /// <para>
    /// 只删 <paramref name="survey"/> 里<b>已经列出来</b>的东西，不再重新判断 ——
    /// 重判会引入"两次扫描之间状态变了"的窗口，而删文件不该有第二次机会。
    /// </para>
    /// </summary>
    public static RepositoryGroomingResult Collect(ImageSurvey survey, string imagesDirectory)
    {
        var failures = new List<string>();
        var deleted = 0;
        long bytes = 0;

        foreach (var orphan in survey.Orphans)
        {
            if (RepositoryGroomer.TryDelete(Path.Combine(imagesDirectory, orphan.Name), orphan.Bytes, failures))
            {
                deleted++;
                bytes += orphan.Bytes;
            }
        }

        foreach (var temp in survey.StaleTemps)
        {
            if (RepositoryGroomer.TryDelete(Path.Combine(imagesDirectory, temp.Name), temp.Bytes, failures))
            {
                deleted++;
                bytes += temp.Bytes;
            }
        }

        return new RepositoryGroomingResult(deleted, bytes, failures);
    }

    private static IEnumerable<FileInfo> EnumerateTopLevelFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists) continue;
            }
            catch (Exception)
            {
                // 枚举期间被别的进程删掉 / 权限变化：这一轮跳过它，下一轮再说。
                continue;
            }

            yield return info;
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateTopLevelDirectories(string directory)
    {
        if (!Directory.Exists(directory)) yield break;

        foreach (var path in Directory.EnumerateDirectories(directory))
        {
            yield return new DirectoryInfo(path);
        }
    }

    private static DateTimeOffset AsUtc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
}
