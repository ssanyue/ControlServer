/**
 * 远控音频的播放缓冲（M5.3）—— 跑在 AudioWorkletGlobalScope 里。
 *
 * ⚠️ **这个文件必须是纯 JS 且不能 import 任何东西**：它由
 * `audioWorklet.addModule('/remote-audio-worklet.js')` 加载，走的是浏览器自己的
 * worklet 加载器，**不经过打包器**（所以它放在 `public/`，原样拷到 wwwroot）。
 * 写成 TS 或写 import 的话，产物里那行 import 会在 worklet 里直接抛错。
 *
 * ─────────────────────────────────────────────────────────────
 * 它为什么存在：**把"什么时候响"这件事交给音频时钟**
 *
 * 主线程只能算"这个数据包应该在 ctx 的第几秒响"，然后把它塞进来；
 * 真正的输出由音频设备的时钟按 128 帧一块地要。中间隔着一个**不能被 JS 阻塞**的边界
 * —— 主线程卡一下（画一帧 4K 的 JPEG 就是几到几十毫秒），音频不能跟着卡，
 * 否则听感是"噼啪"而不是"卡顿"。worklet 跑在音频线程上，正好把这个边界做出来。
 *
 * ─────────────────────────────────────────────────────────────
 * 环形缓冲用**绝对帧号**索引，而不是"队列头 / 队列尾"
 *
 * 🔥 这是本文件最要紧的一个决定，而且它是被"丢包"逼出来的：
 * 用队列的话，每个数据块进来都得判断"该排在哪"—— 迟到一块就会把后面所有块的位置
 * 整体挪一格，于是"迟到 20 ms"变成"从此永久错位 20 ms"，音画对齐一次就毁了。
 * 用绝对帧号之后，**每一块的播放位置在它自己的帧头里就写死了**，
 * 迟到、丢包、乱序都只影响它自己那一块：该补静音就补静音、该丢头部就丢头部。
 *
 * 槽位 = `帧号 % size`。`size` 取 2 的幂（131072 帧 ≈ 2.7 秒 @48 kHz）。
 */
class RemoteAudioRingProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super()

    const opts = (options && options.processorOptions) || {}

    /** 音频采样率（Hz）。**必须是音频流自己的采样率**，不是设备输出采样率。 */
    this.sampleRate_ = opts.sampleRate || 48000

    /** 声道数（1 或 2）。 */
    this.channels_ = opts.channels || 2

    /** 环形缓冲容量（帧）。2 的幂，取模才便宜。 */
    this.size_ = 1 << 17

    this.ring_ = []
    for (let c = 0; c < this.channels_; c += 1) this.ring_.push(new Float32Array(this.size_))

    /**
     * 下一个要写的绝对帧号；`-1` = 还没收到任何数据。
     *
     * 🔥 刻意用 `-1` 而不是 `0`：0 是一个合法的帧号（会话刚开始），
     * 用 0 当哨兵的话，"还没数据"和"数据从第 0 帧开始"就分不开了 ——
     * 而这两种状态要做的事完全相反（一个输出静音、一个开始出声）。
     */
    this.writeCursor_ = -1

    /** 当前正在输出的绝对帧号。第一次 `process` 时对齐到"现在"。 */
    this.readCursor_ = 0
    this.readStarted_ = false

    this.underruns_ = 0
    this.gapFrames_ = 0
    this.staleFrames_ = 0
    this.sinceReport_ = 0

    this.port.onmessage = (event) => this.onMessage_(event.data)
  }

  onMessage_(msg) {
    if (!msg || msg.type !== 'push') return

    const at = msg.frame | 0
    const data = msg.channels

    if (!data || data.length === 0 || data[0].length === 0) return

    const n = data[0].length

    if (this.writeCursor_ < 0) {
      // 第一块：写游标跟着它走；读游标留到第一次 process 时按"现在"对齐
      // （此刻还不知道音频线程跑到哪儿了）。
      this.writeCursor_ = at
    }

    if (at > this.writeCursor_) {
      // 🔥 中间缺了一段（丢包 / 迟到太久被跳过）⇒ **必须把那段槽位清零**。
      //    只把写游标往前挪是不够的：环形缓冲里那些槽位还留着**上一圈**的旧采样，
      //    不清的话会播放出一段"上上秒的声音"—— 表现为冷不丁冒出一小截来路不明的话，
      //    而任何日志里都不会有痕迹。
      this.zeroRange_(this.writeCursor_, at)
      this.gapFrames_ += at - this.writeCursor_
      this.writeCursor_ = at
    } else if (at < this.writeCursor_) {
      // 来晚了：已经写过的部分不能覆盖，丢弃重叠的头部后写剩下的。
      const overlap = this.writeCursor_ - at

      if (overlap >= n) {
        this.staleFrames_ += n
        return
      }

      this.staleFrames_ += overlap
      this.writeRows_(at + overlap, data, overlap, n - overlap)
      return
    }

    this.writeRows_(at, data, 0, n)
  }

  /** 把 `[from, to)` 这段槽位清零。 */
  zeroRange_(from, to) {
    const size = this.size_

    for (let c = 0; c < this.channels_; c += 1) {
      const dst = this.ring_[c]

      for (let f = from; f < to; f += 1) dst[f % size] = 0
    }
  }

  /** 把每声道的 `[offset, offset+count)` 写进 `[start, start+count)`。 */
  writeRows_(start, data, offset, count) {
    const size = this.size_

    for (let c = 0; c < this.channels_; c += 1) {
      const src = data[c] || data[0]
      const dst = this.ring_[c]

      for (let i = 0; i < count; i += 1) dst[(start + i) % size] = src[offset + i]
    }

    this.writeCursor_ = start + count
  }

  process(_inputs, outputs) {
    const out = outputs[0]

    if (!out || out.length === 0) return true

    const block = out[0].length

    if (!this.readStarted_) {
      // 🔥 第一次进来时把读游标对齐到"现在"：`currentTime` 在 `process` 里
      //    对应的正是**本块起点**。此后每块**固定前进 128 帧**（渲染量子是固定的），
      //    于是这个游标是**采样精确且单调**的，不会被 `currentTime` 的量化噪声带动。
      this.readCursor_ = Math.round(currentTime * this.sampleRate_)
      this.readStarted_ = true
    } else {
      // 兜底重同步：只有在"差得离谱"时才动它（设备重启 / 长暂停）。
      // 阈值给得很大是刻意的 —— 动不动就重同步，等于把上面那个"采样精确"扔掉。
      const nowFrame = Math.round(currentTime * this.sampleRate_)

      if (Math.abs(nowFrame - this.readCursor_) > this.sampleRate_ / 10) {
        this.readCursor_ = nowFrame
      }
    }

    const size = this.size_
    const hasData = this.writeCursor_ > 0
    const oldest = hasData ? this.writeCursor_ - size : 0

    for (let i = 0; i < block; i += 1) {
      const frame = this.readCursor_ + i
      const available = hasData && frame < this.writeCursor_ && frame >= oldest

      if (available) {
        const idx = frame % size

        for (let c = 0; c < out.length; c += 1) {
          out[c][i] = this.ring_[c < this.channels_ ? c : 0][idx]
        }
      } else {
        for (let c = 0; c < out.length; c += 1) out[c][i] = 0

        // ⚠️ 每块只计一次：按帧计会让这个数变成"静音了多少帧"，
        //    而它要回答的是"断了几次"，两者差 128 倍，读日志的人会以为坏得惊天动地。
        if (i === 0 && hasData) this.underruns_ += 1
      }
    }

    this.readCursor_ += block

    // 大约每 27 ms 汇报一次（10 个块）。再密没有意义 ——
    // 主线程那边只用它来"显示正在响的是哪一段"，不是拿来驱动播放。
    this.sinceReport_ += 1

    if (this.sinceReport_ >= 10) {
      this.sinceReport_ = 0
      this.port.postMessage({
        type: 'progress',
        frame: this.readCursor_,
        underruns: this.underruns_,
        gaps: this.gapFrames_,
        stale: this.staleFrames_,
      })
    }

    // 永远返回 true：节点一直活着。返回 false 会让它被回收掉，
    // 而下一次推流开始时不会有人再建一个 —— 表现是"第一次能听、切走再看就没声音了"。
    return true
  }
}

registerProcessor('remote-audio-ring', RemoteAudioRingProcessor)
