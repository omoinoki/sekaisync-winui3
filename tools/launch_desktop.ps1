# 启动 SekaiSync Desktop 前端用于人工核实。
#
# 为什么需要脚本而不是直接 Start-Process：
#   - WebView2 的 GPU 进程在本机受限环境里会反复退出
#     （日志表现为 GpuProcessExited / UtilityProcessExited 刷屏，
#      随后 BrowserProcessExited 拖垮整个窗口），加 --disable-gpu 走软件渲染可绕开。
#   - 通过嵌套的 powershell -File 启动，让进程脱离调用方会话的进程组，
#     否则外层会话结束时子进程会被一并清理。
#
# 用法（从任意 shell）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\launch_desktop.ps1
#
# 加 -NoGpuWorkaround 可测一下不加参数是否也能起来。

param(
    [switch]$NoGpuWorkaround
)

# 仓库根从脚本自身位置推导，不写死任何机器路径。
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\x64\Debug\net8.0-windows10.0.19041.0\SekaiSync.Desktop.exe'

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Output "EXE NOT FOUND: $exe"
    Write-Output '构建：dotnet build SekaiSync.Desktop.csproj -c Debug -p:Platform=x64'
    exit 1
}

# 陈旧产物自检：源文件比 exe 新，说明这次实跑验证的不是当前代码。
# 必须用 -Path 而不是 -LiteralPath：-Include 只在 -Path 上加 -Recurse 时才生效，
# 换成 -LiteralPath 会被静默忽略，把目录和 .gitignore 也算成「更新的源文件」。
$exeTime = (Get-Item -LiteralPath $exe).LastWriteTime
$stale = Get-ChildItem -Path $repo -Recurse -File -Include *.xaml,*.cs -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(obj|bin|tools|\.workbuddy|reference)\\' -and $_.LastWriteTime -gt $exeTime }
if ($stale) {
    Write-Output ("STALE BUILD: exe=" + $exeTime.ToString('HH:mm:ss') + "，但有 " + $stale.Count + " 个源文件更新，例如：")
    $stale | Select-Object -First 5 | ForEach-Object { Write-Output ("  " + $_.Name + " " + $_.LastWriteTime.ToString('HH:mm:ss')) }
    Write-Output "先重新构建，否则这次实跑验证的不是当前代码。"
    exit 2
}

# 带 -p:RuntimeIdentifier 的构建会把产物写到 …\net8.0-…\win-x64\ 子目录，
# 并且污染共享 obj/（实测引发一连串 WMC0909/WMC0001/WMC1111/WMC9999 假错）。
# 这里只是提醒那份副本的存在，避免有人误当成本脚本要跑的 exe。
$ridDir = Join-Path $repo 'bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64'
if (Test-Path -LiteralPath $ridDir) {
    Write-Output "NOTE: 存在 RID 构建副本 $ridDir —— 本脚本跑的不是它；建议清掉该子目录并重新无 RID 构建。"
}


$already = Get-Process -Name "SekaiSync.Desktop" -ErrorAction SilentlyContinue
if ($already) {
    Write-Output ("ALREADY RUNNING pid=" + ($already.Id -join ','))
    exit 0
}

if (-not $NoGpuWorkaround) {
    $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--disable-gpu --disable-gpu-compositing"
}

Start-Process -FilePath $exe
Start-Sleep -Seconds 15

# 进程可能在窗口创建后仍被 WebView2 崩溃带走，所以等一会儿再判断一次。
$p = Get-Process -Name "SekaiSync.Desktop" -ErrorAction SilentlyContinue
if (-not $p) {
    Start-Sleep -Seconds 8
    $p = Get-Process -Name "SekaiSync.Desktop" -ErrorAction SilentlyContinue
}

if ($p) {
    $title = ($p | Where-Object { $_.MainWindowTitle } | Select-Object -First 1).MainWindowTitle
    Write-Output ("RUNNING pid=" + ($p.Id -join ',') + " title=" + $title)
} else {
    Write-Output "NOT RUNNING"
}
