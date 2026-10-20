# 升级 / 回滚工具

`docs/M6.4-升级回滚.md` 是方案与实测数据，这里是"怎么用"。

| 文件 | 干什么 | 在哪跑 |
|---|---|---|
| `rehearse-upgrade.py` | **升级/回滚演练**：拿现场库副本往回倒一版，走完整升级再走完整回滚，逐项比对 | 任意（需要 python3） |
| `rehearse-scripts.ps1` | **脚本本身演练**：真跑 `upgrade.ps1` / `rollback.ps1`（进程模式，不需要管理员、不需要先装服务） | Windows（不用提权） |
| `AppControl.ps1` | 上面两个脚本共用的"停/起一个实例"（服务模式 / 进程模式） | 被点进来，不单独跑 |
| `upgrade.ps1` / `upgrade.sh` | 正式升级：停服 → 强制备份 → 装新版 → 验收 → 失败自动回滚 | Windows（管理员）/ Linux（root） |
| `rollback.ps1` / `rollback.sh` | 回滚：停服 → 恢复升级前备份 → 装旧版 → 验收 | 同上 |

---

## 两份演练各管一半，别只做一份

```bash
python deploy/upgrade/rehearse-upgrade.py "C:\ProgramData\ControlServer"
```

验的是**数据链路**：迁移会不会跑、升级会不会丢数据、回滚能不能把数据退回原样、
不可用窗口多长。它**不跑那两个 .ps1**。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\upgrade\rehearse-scripts.ps1
```

验的是**脚本逻辑**：停服 → 基线 → 备份 → 起服 → 验收这条链能不能走通；
验收失败时自动回滚会不会真的触发、数据会不会真的退回、退出码说的是不是实话。
沙盒是空目录起家的，几秒钟就好，不碰现场数据。

⚠️ 它用 `-ProcessMode` 把"实例"换成控制台进程，所以**不需要管理员，也不需要先装服务**。
这是唯一能在开发机上把升级脚本真跑一遍的方式 —— 不然这两个脚本只有真升级那天才会第一次执行。

⚠️ 两份演练都**不覆盖 Inno 安装包那一步**（`/VERYSILENT` 装到 Program Files、注册服务）。
那一步只能在提权真机上验。

---

## `-ProcessMode` 不是测试开关

它是第二种真实部署形态（`dist/win-x64` 那种单文件 exe，直接跑、不注册服务）。
停/起走 PID 文件 + 进程，端口默认**挑一个当时空闲的**。

🔴 端口不能默认 8899：开发机上常常真有一个装在 8899 的实例，
那样"起服成功"其实是起在了别人的进程上，验收全绿而什么都没验。

---

## 退出码（`upgrade.ps1`）

| 码 | 含义 | 自动化该怎么处理 |
|---|---|---|
| 0 | 升级成功 | 继续 |
| 1 | 没开始就中止（没产生可用备份，未回滚） | 看原因，旧版本仍在原位 |
| 2 | 升级失败，且自动回滚也没成功 | **要人**。备份还在，可手工回滚 |
| 3 | 升级**失败**，但已自动回滚成功 | 机器可用但是旧版 —— **不要当成成功** |

🔴 3 这个码是必须的：回滚成功时 `rollback.ps1` 返回 0，如果 `upgrade.ps1` 直接
把它透传出去，那么"升级失败、已退回旧版"对外就表现为"退出码 0" ——
CI / 运维脚本看到 0 会认为升好了，而实际上一个字节都没升。这是最典型的假绿。

---

## 先演练，再升级

两份都跑。`rehearse-upgrade.py` 的源目录**只读**，产物在 `.tools/upgrade-rehearsal/`；
`rehearse-scripts.ps1` 的沙盒在 `.tools/upgrade-scripts/`。都是通过则自动清理、不通过保留供排查。

---

## 正式升级

```powershell
# 管理员身份运行
powershell -NoProfile -ExecutionPolicy Bypass -File deploy\upgrade\upgrade.ps1 `
    -Installer \\share\ControlServer-Setup-0.2.0.exe `
    -PreviousInstaller C:\ControlServer-Backups\ControlServer-Setup-0.1.0.exe
```

```bash
# Linux，root
sudo ./upgrade.sh --installer /tmp/ControlServer-0.2.0.tar.gz \
                  --previous-installer /root/ControlServer-0.1.0.tar.gz
```

**`-PreviousInstaller` 一定要给。** 不给的话，验收失败时自动回滚只能恢复数据、换不回程序 ——
库退回旧结构之后，下次用当前版本启动又被迁移回去，等于白回滚。脚本会明确喊出来，但那已经晚了。

---

## 四个会骗人的地方（都踩过）

1. **`--restore` 不检查服务是否在跑**。它只要求 `--force` 这个覆盖确认。服务跑着时恢复，
   内存里的旧数据会在恢复之后写回去 ⇒"恢复成功但数据是旧的"。脚本第一步就是停服并确认进程真的退出。

2. **"备份成功"不等于"能回滚"**。备份是全量的：现场 1 GB 文件仓库就要 1 GB 空间。
   磁盘余量不够时，备份可能写到一半失败 —— 所以 `upgrade` 先算余量（数据目录 × 1.2），不够就直接不开始。

3. **`--dbinfo` 靠 `Server__Storage__DataDir` 定位数据目录**。忘了设，它读的是**默认目录**，
   而默认目录里往往也有一份"看起来完全正常"的数据 —— 于是你会对着错的那份做比对。
   两个脚本都会显式传这个环境变量。

4. **收紧 ACL 之后，未提升的管理员读不了数据目录**。这不是 bug：UAC 下未提升窗口里的
   Administrators 组是 deny-only。所以从 M6.4 起，`--backup` / `--restore` / `--dbinfo`
   与 `deploy/backup/*` 都**必须提权运行**。

---

## 回滚的代价

备份只包含升级前那一刻的状态。**升级之后、回滚之前产生的数据全都会没有** ——
配置改动、审计、遥测、上传的文件。

所以：升级后如果现场改过东西，回滚前先单独导一份配置。`rollback` 脚本的结论段会把这句再喊一遍。
