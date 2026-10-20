/**
 * 验证 `web/src/deviceIdentity.ts` 的硬件身份归并规则。
 *
 * ═══════════════════════════════════════════════════════════════
 * 为什么值得单独跑一遍
 *
 *   这里的判据**反直觉**：身份居然用"名字"而不是"键"。而它要修的病症
 *   （同一块硬件在列表里出现好几条同名选项）**看起来很像"这台机器真装了那么多块卡"**，
 *   所以一旦被人改回去，不会有人觉得不对 —— 这条脚本就是拦住那一下的。
 *
 * ═══════════════════════════════════════════════════════════════
 * 样本从哪来
 *
 *   **真实数据**。从 `C:\ProgramData\ControlServer\control.db` 里那台
 *   `DESKTOP-1UKRNGI`（只装了 **1** 块 GTX 1060，一天里重启 6 次）的 962 条遥测中，
 *   挑出**覆盖全部身份漂移**的 9 帧：6 个不同的 GPU `LUID`、5 个不同的网卡 `GUID`
 *   （名字全都叫 `Hyper-V Virtual Ethernet Adapter（vEthernet (Default Switch)）`），
 *   外加一帧"采集抖动、没有 GPU"的。
 *
 *   背景与证据（LUID 区间 × uptime 归零）见 `.workbuddy/memory/2026-10-15.md` §5。
 *
 * ═══════════════════════════════════════════════════════════════
 * 用法
 *
 *   node web/tools/check-device-identity.mjs
 *
 * 需要 Node ≥ 22.18（默认启用 TS 类型擦除）。脚本会把 `deviceIdentity.ts` 里的
 * 值导入换成字面量后写进临时目录再 import —— 因为 Node 的 ESM 解析要求带扩展名，
 * 而源码里写的是 bundler 风格的 `'./types'`。
 */
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const srcPath = path.join(here, '..', 'src', 'deviceIdentity.ts')

// ── 载入被测模块 ──────────────────────────────────────────────
const [major, minor] = process.versions.node.split('.').map(Number)
if (major < 22 || (major === 22 && minor < 18)) {
  console.error(`需要 Node ≥ 22.18（当前 ${process.versions.node}）：类型擦除默认启用才行`)
  process.exit(2)
}

/**
 * 变异开关 —— 用来证明**断言咬得动**。
 *
 * 一个"永远会通过"的断言等于没写。这里的每一个变异都关掉一条关键判据；
 * 跑 `--verify-mutations` 会逐个套用，并要求**至少有一条断言变红**。
 */
const MUTATIONS = {
  /** 把身份退回"实例 Key"（＝本次修复前的行为）。 */
  'key-as-identity': (s) => s.replace('${displayNameOf(d)}', '${d.key}'),

  /** 不再给"真的并存"编号。 */
  'no-twin-numbering': (s) =>
    s.replace('const shared = (maxConcurrent.get(g) ?? 1) > 1', 'const shared = false'),

  /** 不允许回看：只认最后一帧（采集抖动时整类硬件会消失）。 */
  'no-lookback': (s) => s.replace('lookback: number = LATEST_FRAME_LOOKBACK', 'lookback: number = 0'),
}

let source = fs
  .readFileSync(srcPath, 'utf8')
  .replace(/^import type .*$/gm, '')
  .replace(
    /^import \{ AGENT_DEVICE_KINDS \} from '\.\/types'$/m,
    "const AGENT_DEVICE_KINDS = ['cpu', 'disk', 'net', 'gpu']",
  )

// `--verify-mutations`：跑自己 N 次，每次都套一个变异，要求断言红了。
if (process.argv.includes('--verify-mutations')) {
  const { spawnSync } = await import('node:child_process')
  let blind = 0

  for (const name of Object.keys(MUTATIONS)) {
    const r = spawnSync(process.execPath, [fileURLToPath(import.meta.url)], {
      env: { ...process.env, MUTATE: name },
      encoding: 'utf8',
    })

    const out = `${r.stdout ?? ''}${r.stderr ?? ''}`
    const m = out.match(/(\d+) PASS \/ (\d+) FAIL/)
    const red = m ? Number(m[2]) : -1

    if (red > 0) {
      console.log(`[PASS] 变异 ${name} 被逮住（${red} 条断言变红）`)
    } else {
      blind += 1
      console.log(`[FAIL] 变异 ${name} 一条都没红 —— 对应的断言是瞎的（输出：${out.trim().split('\n').at(-1)}）`)
    }
  }

  console.log(`\n${Object.keys(MUTATIONS).length - blind} 个变异被逮住 / ${blind} 个没被逮住`)
  process.exit(blind === 0 ? 0 : 1)
}

const mutate = process.env.MUTATE ?? ''

if (mutate) {
  const apply = MUTATIONS[mutate]

  if (!apply) {
    console.error(`未知变异：${mutate}（可选：${Object.keys(MUTATIONS).join(', ')}）`)
    process.exit(2)
  }

  const before = source
  source = apply(source)

  if (source === before) {
    console.error(`变异 ${mutate} 没有生效 —— deviceIdentity.ts 是不是改过了？`)
    process.exit(2)
  }

  console.log(`⚠️ 变异模式：${mutate}（下面**应当**出现 FAIL）\n`)
}

const tmp = path.join(os.tmpdir(), `deviceIdentity.check.${process.pid}.ts`)
fs.writeFileSync(tmp, source, 'utf8')

const { identifyPhysicalDevices, pickReading } = await import(pathToFileURL(tmp).href)
fs.rmSync(tmp, { force: true })

// ── 断言小工具 ────────────────────────────────────────────────
let pass = 0
let fail = 0

function check(name, ok, detail = '') {
  if (ok) {
    pass += 1
    console.log(`[PASS] ${name}`)
  } else {
    fail += 1
    console.log(`[FAIL] ${name}${detail ? ` —— ${detail}` : ''}`)
  }
}

/** 造一帧：只带 devices，其余字段给 null（标识与取值都只看 devices）。 */
function frame(at, devices) {
  return {
    at,
    cpuPct: null,
    memPct: null,
    memUsedMb: null,
    memTotalMb: null,
    diskReadBps: null,
    diskWriteBps: null,
    netUpBps: null,
    netDownBps: null,
    gpuPct: null,
    uptimeSeconds: null,
    cpuTempC: null,
    gpuTempC: null,
    storageTempC: null,
    devices,
  }
}

function dev(kind, key, name, extra = {}) {
  return {
    kind,
    key,
    name,
    cpuPct: null,
    readBps: null,
    writeBps: null,
    upBps: null,
    downBps: null,
    gpuPct: null,
    ...extra,
  }
}

const ids = (list) => list.map((d) => d.id)
const names = (list) => list.map((d) => d.name)
const ofKind = (list, kind) => list.filter((d) => d.kind === kind)

// ═══════════════════════════════════════════════════════════════
// ① 真实机器：1 块卡、重启 6 次
// ═══════════════════════════════════════════════════════════════
const REAL = JSON.parse(
  fs.readFileSync(path.join(here, 'device-identity-real-frames.json'), 'utf8'),
)

console.log('── ① 真实样本：DESKTOP-1UKRNGI（1 块 GTX 1060，重启 6 次）──')

const real = identifyPhysicalDevices(REAL)
const gpus = ofKind(real, 'gpu')
const nets = ofKind(real, 'net')
const disks = ofKind(real, 'disk')
const cpus = ofKind(real, 'cpu')

check(
  `显卡只有 1 条（样本里有 ${new Set(REAL.flatMap((f) => (f.devices ?? []).filter((d) => d.kind === 'gpu').map((d) => d.key))).size} 个不同 LUID）`,
  gpus.length === 1,
  `得到 ${gpus.length} 条：${names(gpus).join(' | ')}`,
)

check(
  '  这条显卡的名字**不带** #序号（没有真的并存，就不该编号）',
  gpus[0] !== undefined && !gpus[0].name.includes('#'),
  gpus[0]?.name,
)

check(
  '  它把窗口内用过的 6 个 LUID 全收进来了（曲线才接得上）',
  gpus[0]?.keys.length === 6,
  `keys=${gpus[0]?.keys.length}`,
)

check(
  `网卡 2 条（${new Set(REAL.flatMap((f) => (f.devices ?? []).filter((d) => d.kind === 'net').map((d) => d.key))).size} 个不同 GUID 里，5 个都叫同一个名字）`,
  nets.length === 2,
  `得到 ${nets.length} 条：${names(nets).join(' | ')}`,
)

check('  磁盘仍是 3 条（disk0/1/2 的键重启后稳定，本来就没问题）', disks.length === 3, ids(disks).join(','))

check('  处理器仍是 1 条', cpus.length === 1, ids(cpus).join(','))

check(
  '  总数 = 7（修复前是 17：3 磁盘 + 1 CPU + 7 网卡 + 6 显卡）',
  real.length === 7,
  `得到 ${real.length}`,
)

// 🔥 取值必须"按当时活着的那一个键"穿过重启点 —— 这是"曲线接得上"的另一半。
const gpu = gpus[0]
if (gpu) {
  const withKey = REAL.find((f) => (f.devices ?? []).some((d) => d.key === gpu.keys.at(-1)))
  check(
    '  按键取值：最后一个 LUID 那一帧能取到读数',
    withKey !== undefined && pickReading({ ...withKey, devices: withKey.devices }, gpu) !== null,
  )

  const onlyOld = REAL.find(
    (f) =>
      (f.devices ?? []).some((d) => gpu.keys.includes(d.key)) &&
      !(f.devices ?? []).some((d) => d.key === gpu.keys.at(-1)),
  )
  check(
    '  按键取值：只有"上一代 LUID"的那一帧同样取得到（不是只认最新的那个键）',
    onlyOld !== undefined && pickReading({ ...onlyOld, devices: onlyOld.devices }, gpu) !== null,
  )
}

// ═══════════════════════════════════════════════════════════════
// ② 反例：真的两块同型号卡**同时**存在于同一帧
// ═══════════════════════════════════════════════════════════════
console.log('\n── ② 真的并存（同帧两块同型号卡）──')

const twin = identifyPhysicalDevices([
  frame(1, [
    dev('gpu', 'luid_0x0_0xAAA', 'NVIDIA GeForce GTX 1060 6GB'),
    dev('gpu', 'luid_0x0_0xBBB', 'NVIDIA GeForce GTX 1060 6GB'),
  ]),
  frame(2, [
    dev('gpu', 'luid_0x0_0xAAA', 'NVIDIA GeForce GTX 1060 6GB'),
    dev('gpu', 'luid_0x0_0xBBB', 'NVIDIA GeForce GTX 1060 6GB'),
  ]),
])

check('同帧并存 ⇒ 分成 2 条（不能合并，否则两块卡的读数会被混成一条）', twin.length === 2, ids(twin).join(' | '))
check('  两条各自带 #1 / #2', names(twin).sort().join(' | ') === 'NVIDIA GeForce GTX 1060 6GB #1 | NVIDIA GeForce GTX 1060 6GB #2', names(twin).join(' | '))
check('  两条的键各自独立（不交叉）', twin.every((d) => d.keys.length === 1), twin.map((d) => d.keys.join(',')).join(' | '))

// ═══════════════════════════════════════════════════════════════
// ③ 采集抖动：最后几帧暂时没有显卡，条目不能消失
// ═══════════════════════════════════════════════════════════════
console.log('\n── ③ 采集抖动（末帧缺某一类）──')

const jitter = identifyPhysicalDevices([
  frame(1, [dev('gpu', 'luid_0x0_0xAAA', 'GTX 1060')]),
  frame(2, []),
  frame(3, []),
])

check('末两帧没有显卡，仍按回看窗口列出它（否则条目会一闪一闪）', jitter.length === 1, ids(jitter).join(' | '))

// ═══════════════════════════════════════════════════════════════
// ④ 占位名：不含身份信息，不归并
// ═══════════════════════════════════════════════════════════════
console.log('\n── ④ 型号认不出来时（占位名）──')

const placeholder = identifyPhysicalDevices([
  frame(1, [dev('gpu', 'luid_0x0_0xAAA', 'GPU 适配器 1')]),
  frame(2, [dev('gpu', 'luid_0x0_0xBBB', 'GPU 适配器 1')]),
])

check(
  '占位名跨帧（LUID 变了、名字没变）⇒ 仍归并成 1 条（认不出型号也不该每次重启多一条）',
  placeholder.length === 1 && placeholder[0]?.keys.length === 2,
  ids(placeholder).join(' | '),
)

const placeholderTwin = identifyPhysicalDevices([
  frame(1, [
    dev('gpu', 'luid_0x0_0xAAA', 'GPU 适配器 1'),
    dev('gpu', 'luid_0x0_0xBBB', 'GPU 适配器 1'),
  ]),
])

check(
  '  同帧两个同名占位 ⇒ 2 条带 #1/#2（不至于又是一排分不清的同名条目）',
  placeholderTwin.length === 2 &&
    names(placeholderTwin).sort().join(' | ') === 'GPU 适配器 1 #1 | GPU 适配器 1 #2',
  names(placeholderTwin).join(' | '),
)

const nameless = identifyPhysicalDevices([
  frame(1, [dev('gpu', 'luid_0x0_0xAAA', ''), dev('gpu', 'luid_0x0_0xBBB', '')]),
])

check(
  '  名字**为空**才退回用键 ⇒ 2 条（空名字不含任何身份信息，绝不合并）',
  nameless.length === 2,
  ids(nameless).join(' | '),
)

// ═══════════════════════════════════════════════════════════════
// ⑤ 已知代价：给一块网卡改名，曲线会断成两截
// ═══════════════════════════════════════════════════════════════
console.log('\n── ⑤ 已知代价（把代价钉住，免得被当成新 bug）──')

const renamed = identifyPhysicalDevices([
  frame(1, [dev('net', '{SAME-GUID}', '以太网', { upBps: 123 })]),
  frame(2, [dev('net', '{SAME-GUID}', '展厅上联', { upBps: 456 })]),
])

check(
  '改名 ⇒ 仍是 1 条（列表只反映当前那一代名字）',
  renamed.length === 1 && renamed[0]?.name === '展厅上联',
  names(renamed).join(' | '),
)

check(
  '  而"改名之前"那一段的数据**仍然取得到**（取值走 Key，不走名字）',
  renamed[0] !== undefined &&
    pickReading(frame(1, [dev('net', '{SAME-GUID}', '以太网', { upBps: 123 })]), renamed[0])
      ?.upBps === 123,
)

// ═══════════════════════════════════════════════════════════════
// ⑥ 已消失的硬件不单列
// ═══════════════════════════════════════════════════════════════
console.log('\n── ⑥ 窗口内出现过、现已不在 ──')

const gone = identifyPhysicalDevices([
  frame(1, [dev('disk', 'disk0', '物理磁盘 0'), dev('disk', 'disk9', '物理磁盘 9')]),
  frame(2, [dev('disk', 'disk0', '物理磁盘 0')]),
  frame(3, [dev('disk', 'disk0', '物理磁盘 0')]),
  frame(4, [dev('disk', 'disk0', '物理磁盘 0')]),
  frame(5, [dev('disk', 'disk0', '物理磁盘 0')]),
])

check('拔掉的盘不在列表里（超出回看窗口即消失）', ids(gone).join(',') === 'disk\u0001物理磁盘 0', ids(gone).join(' | '))

// ── 汇总 ─────────────────────────────────────────────────────
console.log(`\n${pass} PASS / ${fail} FAIL`)
process.exit(fail === 0 ? 0 : 1)
