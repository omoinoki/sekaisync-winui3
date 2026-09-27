[CmdletBinding()]
param(
    # 只跑检查与构建，不动 git。
    [switch]$Check,
    # 产出可分发的发布目录（dotnet publish）。
    [switch]$Package,
    # 打附注 tag；配合 -Push 一并推送。
    [switch]$Tag,
    [switch]$Push,
    [string]$Remote
)

# SekaiSync Desktop 发布守门脚本。
#
#   powershell -File tools\publish.ps1 -Check            # 只体检 + Release 构建
#   powershell -File tools\publish.ps1 -Check -Package   # 再加一份可分发产物
#   powershell -File tools\publish.ps1 -Tag              # 体检通过后打 tag
#   powershell -File tools\publish.ps1 -Tag -Push -remote <url>
#
# 为什么要有这个文件：本仓 2026-09-20 出过一次跨仓 `git checkout -- Views/`
# 清掉 14 个未提交 XAML 的事故，2026-09-27 又出现过一次 `git add -A` 顺手把
# 一次性量测脚本收进版本库。两者都是「发布动作里没有守门」的后果。
# 这里把守门做成一条命令：版本号取自 csproj（不硬编码，避免主仓 publish.ps1
# 里那种 $Tag = "v0.3.0" 与工程实际版本长期不一致的情况），任何一步不过就退出非零。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'SekaiSync.Desktop.csproj'

function Fail([string]$msg) { Write-Host "FAIL  $msg" -ForegroundColor Red; exit 1 }
function Note([string]$msg) { Write-Host $msg }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail '未找到 git，请先安装 Git for Windows。' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail '未找到 dotnet CLI。' }

Push-Location $root
try {
    # 1. 版本号以工程文件为唯一出处。
    $version = (dotnet msbuild $proj -getProperty:Version -nologo) |
        ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Last 1
    if (-not $version) { Fail '读不到 <Version>，无法确定发布版本。' }
    # 局部量不能叫 $tag：PowerShell 变量名大小写不敏感，那会撞上 [switch]$Tag。
    $releaseTag = "v$version"
    Note "版本 $version → tag $releaseTag"

    # 2. 工作树必须干净：未提交的界面工作一旦被发布动作覆盖就不可恢复。
    $dirty = @(git status --porcelain)
    if ($dirty.Count -gt 0 -and -not $Package) {
        Note "工作树有 $($dirty.Count) 项未提交："
        $dirty | Select-Object -First 10 | ForEach-Object { Note "   $_" }
        Fail '发布前先提交干净。本仓有过未提交工作被 git 操作清掉的事故，不放行。'
    }

    # 3. 守门：这些路径绝不能进版本库。
    $forbidden = @(git diff --cached --name-only | Where-Object {
        $_ -match '^(screenshots|\.workbuddy|bin|obj)[\\/]' -or
        $_ -match '^tools/oneshot/' -or
        $_ -match '\.(log|dmp|tmp)$'
    })
    if ($forbidden.Count -gt 0) {
        Fail "以下文件不该入库，请检查 .gitignore:`n$($forbidden -join "`n")"
    }
    Note '守门通过：暂存区没有本机数据、产物或一次性脚本。'

    # 4. Release 构建。XAML 编译器出错时会连抛 WMC0001/0909/1111/9999，
    #    那是一串下游噪声；真正的错因在 CS 行，所以这里把 error 行原样打出来。
    Note '构建 Release x64 …'
    $build = dotnet build $proj -c Release -p:Platform=x64 --nologo 2>&1
    if ($LASTEXITCODE -ne 0) {
        $build | Where-Object { $_ -match ': error ' } | Select-Object -First 15 | ForEach-Object { Note $_ }
        Fail '构建失败。'
    }
    $warn = @($build | Where-Object { $_ -match ': warning ' })
    if ($warn.Count -gt 0) {
        $warn | Select-Object -First 10 | ForEach-Object { Note "   $_" }
        Fail "构建有 $($warn.Count) 条警告，按零警告标准不放行。"
    }
    Note '构建通过：0 错误 0 警告。'

    # 5. 可选：产出可分发目录。不带 RID——实测它会污染共享 obj/，
    #    之后引出一连串 WMC 假错（见 README「构建」一节）。
    if ($Package) {
        $out = Join-Path $root "dist\$releaseTag"
        Note "dotnet publish → $out"
        dotnet publish $proj -c Release -p:Platform=x64 -o $out --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail 'publish 失败。' }
        $exe = Join-Path $out 'SekaiSync.Desktop.exe'
        if (-not (Test-Path -LiteralPath $exe)) { Fail "publish 完成但找不到 $exe" }
        Note "产物就绪：$exe"
    }

    # 6. 可选：打 tag。
    if ($Tag) {
        if (git tag -l $releaseTag) { Fail "tag $releaseTag 已存在；先决定是覆盖还是升版本。" }
        git tag -a $releaseTag -m "SekaiSync Desktop $releaseTag"
        Note "已打 tag $releaseTag。"
        if ($Push) {
            if (-not $Remote) { Fail '-Push 需要 -Remote <仓库地址>。' }
            if (git remote get-url origin 2>$null) { git remote set-url origin $Remote }
            else { git remote add origin $Remote }
            git push -u origin HEAD --tags
            Note '已推送分支与 tag。'
        } else {
            Note '未推送：加 -Push -Remote <url> 才会触达远端。'
        }
    }

    Note '完成。'
} finally {
    Pop-Location
}
