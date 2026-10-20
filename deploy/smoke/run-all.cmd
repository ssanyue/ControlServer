@echo off
rem ============================================================================
rem  冒烟总入口（Windows）—— 它只做一件事：<b>找到能用的 python</b>。
rem
rem  为什么需要它：deploy/smoke/*.py 只用标准库，但"标准库"在不同 python 上
rem  并不一样 —— 精简版（Microsoft Store / embeddable 包）经常没有 sqlite3，
rem  而 pairing-smoke 会直接写 SQLite。于是"换了个 python 就红"这件事
rem  会被记成"冒烟坏了"，其实只是解释器不对。
rem
rem  本脚本按优先级找：
rem    1) SMOKE_PYTHON 环境变量（显式指定，最优先）
rem    2) 项目内 .venv                 （部署机/CI 的常规位置）
rem    3) 开发机上的托管解释器         （WorkBuddy 环境，一般机器上没有）
rem    4) PATH 上的 python
rem  找到后先验证"3.8+ 且能 import sqlite3"，再交给 run-all.py。
rem
rem  输出全 ASCII：GBK 控制台上中文会变问号，反而看不清是哪一步失败。
rem ============================================================================
setlocal

set "SCRIPT=%~dp0run-all.py"

if not exist "%SCRIPT%" (
  echo [smoke] missing %SCRIPT%
  exit /b 2
)

set "PY="

if defined SMOKE_PYTHON set "PY=%SMOKE_PYTHON%"

if not defined PY if exist "%~dp0..\..\.venv\Scripts\python.exe" set "PY=%~dp0..\..\.venv\Scripts\python.exe"

if not defined PY if exist "%USERPROFILE%\.workbuddy\binaries\python\envs\default\Scripts\python.exe" set "PY=%USERPROFILE%\.workbuddy\binaries\python\envs\default\Scripts\python.exe"

if not defined PY for %%P in (python.exe) do if not "%%~$PATH:P"=="" set "PY=%%~$PATH:P"

if not defined PY (
  echo [smoke] python not found.
  echo [smoke] install python 3.8+ or set SMOKE_PYTHON to its exe path, then retry.
  exit /b 2
)

rem 验证：版本 + sqlite3。两者缺一都会让某条冒烟以"看起来像产品缺陷"的方式红。
"%PY%" -c "import sys,sqlite3;sys.exit(0 if sys.version_info>=(3,8) else 1)" >nul 2>&1
if errorlevel 1 (
  echo [smoke] unusable python: "%PY%"
  echo [smoke] need python 3.8+ WITH the sqlite3 module ^(slim builds often drop it^).
  echo [smoke] hint: set SMOKE_PYTHON=C:\path\to\python.exe and retry.
  exit /b 2
)

echo [smoke] python=%PY%

"%PY%" "%SCRIPT%" %*
exit /b %ERRORLEVEL%
