import { describe, expect, it } from 'vitest'
import {
  MAX_CONCURRENT,
  breadcrumbOf,
  canRetry,
  failJob,
  initialPhase,
  isAbsolutePath,
  isTerminal,
  joinPath,
  leafOf,
  nextPhase,
  overallProgress,
  parentOf,
  pickRunnable,
  retryPhase,
  separatorOf,
  type TransferJob,
} from './fileBrowser'

/**
 * 这一组钉的是「文件」页里那些**会静默错**的规则。
 *
 * 最要紧的一条在最前面：客户机路径是**客户机的坐标系**，而这段代码跑在浏览器里、
 * 中心还可能跑在 Linux 上 —— 任何"按运行平台分隔符"的取末段实现都会在 Linux 上
 * 把整串 Windows 路径原样返回（见 `DistributionsController.LeafNameOf` 的同一条注释）。
 */

describe('客户机路径：取末段', () => {
  it('从 Windows 路径里取文件名，与运行平台无关', () => {
    expect(leafOf('D:\\logs\\app-20261020.log')).toBe('app-20261020.log')
  })

  it('正斜杠的路径同样能取（客户机可能是 Unix）', () => {
    expect(leafOf('/var/log/app.log')).toBe('app.log')
  })

  it('盘根没有"名字" —— 返回空串，调用方据此不要把盘根当文件', () => {
    expect(leafOf('C:\\')).toBe('')
    expect(leafOf('/')).toBe('')
  })

  it('没有分隔符时整串就是名字', () => {
    expect(leafOf('report.zip')).toBe('report.zip')
  })

  it('末尾带分隔符也只取它前面那段', () => {
    expect(leafOf('D:\\logs\\')).toBe('logs')
  })
})

describe('客户机路径：上一级', () => {
  it('普通目录回到父目录', () => {
    expect(parentOf('D:\\logs\\sub')).toBe('D:\\logs')
  })

  it('盘下的第一层回到盘根（保留那个分隔符）', () => {
    expect(parentOf('C:\\logs')).toBe('C:\\')
  })

  it('盘根没有上一级 —— null 表示"该回盘符列表了"，不是失败', () => {
    expect(parentOf('C:\\')).toBeNull()
    expect(parentOf('C:')).toBeNull()
  })

  it('UNC 根没有上一级', () => {
    expect(parentOf('\\\\server\\share')).toBeNull()
    expect(parentOf('\\\\server\\share\\dir')).toBe('\\\\server\\share')
  })

  it('类 Unix 根的上一级是 /，/ 本身没有上一级', () => {
    expect(parentOf('/home')).toBe('/')
    expect(parentOf('/')).toBeNull()
  })
})

describe('客户机路径：拼接', () => {
  it('盘根后面不再补一个分隔符', () => {
    expect(joinPath('C:\\', 'a.txt')).toBe('C:\\a.txt')
  })

  it('普通目录用同一个分隔符拼', () => {
    expect(joinPath('C:\\logs', 'a.txt')).toBe('C:\\logs\\a.txt')
    expect(joinPath('/home/x', 'a.txt')).toBe('/home/x/a.txt')
  })
})

describe('客户机路径：绝对性判定', () => {
  it('盘符 / UNC / 类 Unix 根都算绝对路径', () => {
    expect(isAbsolutePath('C:\\logs')).toBe(true)
    expect(isAbsolutePath('\\\\server\\share\\a')).toBe(true)
    expect(isAbsolutePath('/home')).toBe(true)
  })

  it('短标识与相对路径不算', () => {
    expect(isAbsolutePath('inbox')).toBe(false)
    expect(isAbsolutePath('..\\logs')).toBe(false)
    expect(isAbsolutePath('')).toBe(false)
  })
})

describe('面包屑', () => {
  it('盘符路径逐段展开，第一段是盘根', () => {
    const crumbs = breadcrumbOf('C:\\logs\\sub')

    expect(crumbs.map((c) => c.name)).toEqual(['C:\\', 'logs', 'sub'])
    expect(crumbs.map((c) => c.path)).toEqual(['C:\\', 'C:\\logs', 'C:\\logs\\sub'])
  })

  it('类 Unix 路径以 / 开头', () => {
    expect(breadcrumbOf('/home/x').map((c) => c.path)).toEqual(['/', '/home', '/home/x'])
  })

  it('UNC 路径的根是 \\\\server\\share（两段）', () => {
    expect(breadcrumbOf('\\\\server\\share\\dir').map((c) => c.path)).toEqual([
      '\\\\server\\share',
      '\\\\server\\share\\dir',
    ])
  })
})

describe('传输阶段：发到客户机', () => {
  it('完整链路是 上传 → 建单 → 投递 → 完成', () => {
    expect(initialPhase('to-agent')).toBe('uploading')
    expect(nextPhase('to-agent', 'uploading')).toBe('publishing')
    expect(nextPhase('to-agent', 'publishing')).toBe('delivering')
    expect(nextPhase('to-agent', 'delivering')).toBe('done')
    expect(nextPhase('to-agent', 'done')).toBeNull()
  })

  it('"上传成功但建单失败"重试只重建单，不重传文件', () => {
    const job = makeJob({ direction: 'to-agent', phase: 'publishing', fileId: 42 })

    const failed = failJob(job, '授权未生效')

    expect(failed.phase).toBe('failed')
    expect(canRetry(failed)).toBe(true)
    expect(retryPhase(failed)).toBe('publishing')
    expect(failed.fileId).toBe(42)
  })

  it('上传阶段失败从上传重来（服务端会话会续上已传的字节）', () => {
    const failed = failJob(makeJob({ direction: 'to-agent', phase: 'uploading' }), '网络断了')

    expect(retryPhase(failed)).toBe('uploading')
  })

  it('连续失败不会把 resumeFrom 覆盖成 failed 本身', () => {
    const first = failJob(makeJob({ direction: 'to-agent', phase: 'publishing' }), '第一次')
    const second = failJob(first, '第二次')

    expect(second.resumeFrom).toBe('publishing')
    expect(retryPhase(second)).toBe('publishing')
  })
})

describe('传输阶段：取回本机', () => {
  it('完整链路是 机器上传 → 下载 → 完成', () => {
    expect(initialPhase('to-local')).toBe('collecting')
    expect(nextPhase('to-local', 'collecting')).toBe('downloading')
    expect(nextPhase('to-local', 'downloading')).toBe('done')
    expect(nextPhase('to-local', 'done')).toBeNull()
  })

  it('下载失败从下载重来（仓库里那份还在）', () => {
    const failed = failJob(makeJob({ direction: 'to-local', phase: 'downloading' }), '写盘失败')

    expect(retryPhase(failed)).toBe('downloading')
  })
})

describe('终态', () => {
  it('done / failed 是终态，其余不是', () => {
    expect(isTerminal('done')).toBe(true)
    expect(isTerminal('failed')).toBe(true)
    expect(isTerminal('uploading')).toBe(false)
    expect(isTerminal('delivering')).toBe(false)
  })
})

describe('队列调度', () => {
  it('最多同时跑 MAX_CONCURRENT 条', () => {
    const jobs = [
      makeJob({ key: 'a', phase: 'pending' }),
      makeJob({ key: 'b', phase: 'pending' }),
      makeJob({ key: 'c', phase: 'pending' }),
    ]

    expect(MAX_CONCURRENT).toBe(2)
    expect(pickRunnable(jobs).map((j) => j.key)).toEqual(['a', 'b'])
  })

  it('已有两条在跑时不再开工（避免全部变慢、进度条一起卡住）', () => {
    const jobs = [
      makeJob({ key: 'a', phase: 'uploading' }),
      makeJob({ key: 'b', phase: 'delivering' }),
      makeJob({ key: 'c', phase: 'pending' }),
    ]

    expect(pickRunnable(jobs)).toEqual([])
  })

  it('失败的条目不占并发', () => {
    const jobs = [
      makeJob({ key: 'a', phase: 'failed' }),
      makeJob({ key: 'b', phase: 'pending' }),
    ]

    expect(pickRunnable(jobs).map((j) => j.key)).toEqual(['b'])
  })
})

describe('整体进度', () => {
  it('全部大小未知时返回 null（页面渲染成不确定態，而不是 0%）', () => {
    expect(overallProgress([makeJob({ size: 0 }), makeJob({ size: 0 })])).toBeNull()
  })

  it('按已知总字节汇总，已收字节不超过单位大小', () => {
    const progress = overallProgress([
      makeJob({ size: 100, doneBytes: 100 }),
      makeJob({ size: 100, doneBytes: 250 }),
    ])

    expect(progress).toEqual({ done: 200, total: 200, pct: 100 })
  })
})

describe('分隔符推断', () => {
  it('含反斜杠按 Windows，否则按 /', () => {
    expect(separatorOf('C:\\logs')).toBe('\\')
    expect(separatorOf('/home')).toBe('/')
    expect(separatorOf('inbox')).toBe('/')
  })
})

/** 造一条任务；不传的字段给安全默认值。 */
function makeJob(overrides: Partial<TransferJob> = {}): TransferJob {
  return {
    key: 'k',
    direction: 'to-agent',
    fileName: 'a.txt',
    size: 10,
    phase: 'pending',
    doneBytes: 0,
    error: null,
    resumeFrom: null,
    taskId: null,
    fileId: null,
    resultFileId: null,
    ...overrides,
  }
}
