@echo off
setlocal
cd /d "%~dp0"

rem ============================================================
rem  中控服务 ControlServer -- 一键启动
rem
rem  双击本文件即可启动。
rem  【关闭本窗口不会停止服务】-- 服务在后台独立运行。
rem  停止服务：右键右下角托盘图标 -> 「退出服务」。
rem
rem  用法：run.cmd          启动在 8899 端口
rem        run.cmd 9000     启动在 9000 端口
rem ============================================================

rem 本机 SDK 位置。
rem 注意：系统 PATH 里那个 dotnet（C:\Program Files\dotnet）只有 runtime，
rem       既没有 SDK 也没有 Microsoft.AspNetCore.App，所以必须用这一份。
set "SDK_ROOT=C:/Users/IYLONG/.workbuddy/binaries/dotnet8"
set "DOTNET_EXE=%SDK_ROOT%\dotnet.exe"

set "PORT=8899"
if not "%~1"=="" set "PORT=%~1"

rem ── 服务进程的工作目录必须钉在 Server 项目目录（否则静态资源走内嵌，见下）──
rem ContentRoot 默认取「当前工作目录」，而 Program.cs 用 ContentRoot\wwwroot 找前端产物：
rem   从仓库根启动  -> %~dp0wwwroot 不存在        -> 回退内嵌资源
rem                    -> 改前端就得重编后端，且 exe 被占用时编译还会失败
rem   从项目目录启动 -> 命中 npm run build 的产物 -> 改完前端刷新浏览器即可
rem 所以下面用 start /D 指定工作目录；三个路径也一并改成绝对路径，不再依赖 cwd。
set "SERVER_DIR=%~dp0src\ControlServer.Server"
set "SERVER_PROJECT=%~dp0src\ControlServer.Server\ControlServer.Server.csproj"
set "SERVER_EXE=%~dp0src\ControlServer.Server\bin\Debug\net8.0\ControlServer.Server.exe"

title 中控服务 ControlServer

echo.
echo  ============================================================
echo    中控服务 ControlServer
echo  ============================================================

if not exist "%DOTNET_EXE%" goto :no_sdk

rem 让 apphost 也能找到运行时（直接跑 bin 下的 exe 时需要）
set "DOTNET_ROOT=%SDK_ROOT%"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "Server__Http__Urls=http://0.0.0.0:%PORT%"

echo    正在编译 ...
"%DOTNET_EXE%" build "%SERVER_PROJECT%" -c Debug --nologo -v q
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" goto :build_failed
if not exist "%SERVER_EXE%" goto :build_failed

echo    启动中 ...
rem 用 start 启动：不占用本窗口。
rem 服务进程启动时会【主动脱离控制台】，所以之后关掉本窗口不会把它带走。
start "" /D "%SERVER_DIR%" "%SERVER_EXE%"

rem 等端口真的进入监听再报成功（最多 40 秒）-- 不要凭空断言「已启动」。
set /a WAITED=0
:wait_ready
netstat -ano | findstr ":%PORT%" | findstr "LISTENING" >nul 2>&1
if not errorlevel 1 goto :ready
set /a WAITED+=1
if %WAITED% GEQ 40 goto :timeout
timeout /t 1 /nobreak >nul 2>&1
goto :wait_ready

:ready
echo.
echo  ============================================================
echo    服务已启动：http://localhost:%PORT%
echo    默认账号：admin / admin123
echo    日志目录：%ProgramData%\ControlServer\logs
echo  ------------------------------------------------------------
echo    【关闭本窗口不会停止服务】-- 服务已在后台独立运行。
echo    停止服务：右键右下角托盘图标，选「退出服务」。
echo  ============================================================
echo.
pause
exit /b 0

:timeout
echo.
echo  [!] 等了 40 秒仍未看到端口 %PORT% 进入监听。
echo      可能原因：端口被别的程序占用，或者启动时就报错了。
echo      查看日志：%ProgramData%\ControlServer\logs
echo      查看占用：netstat -ano ^| findstr ":%PORT%"
echo.
pause
exit /b 1

:build_failed
echo.
echo  [错误] 编译失败（退出码 %RC%）。请查看上方输出。
echo.
pause
exit /b 1

:no_sdk
echo.
echo  [错误] 未找到 .NET 8 SDK：
echo         %DOTNET_EXE%
echo.
echo  用记事本打开本脚本，把 SDK_ROOT 改成你的 SDK 安装目录即可。
echo.
pause
exit /b 1
