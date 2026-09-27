# 资讯页「缓存原文 / 原网页」切换 + 缩放器联动的 UIA 验证
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$ErrorActionPreference = 'Continue'
$exe = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\SekaiSync.Desktop.exe'))

function Get-Descendants($el) {
    return @($el.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition))
}
function Sz($v) {
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return -1 }
    return [int][Math]::Round($v)
}
function Fmt($c) {
    $r = $c.Current.BoundingRectangle
    return ("{0,-10} cls='{1}' name='{2}' {3}x{4}@({5},{6})" -f `
        $c.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), `
        $c.Current.ClassName, $c.Current.Name.Substring(0, [Math]::Min(60, $c.Current.Name.Length)), `
        (Sz $r.Width), (Sz $r.Height), (Sz $r.X), (Sz $r.Y))
}
function DumpState($all, $tag) {
    Write-Host "`n--- [$tag] ---"
    foreach ($c in ($all | Where-Object { $_.Current.ClassName -eq 'ToggleSwitch' -and $_.Current.Name -ne '全部时段' })) {
        $tp = $null
        if ($c.GetSupportedPatterns() -contains [System.Windows.Automation.TogglePattern]::Pattern) {
            $tp = $c.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        }
        Write-Host ("SWITCH   {0} enabled={1} state={2}" -f (Fmt $c), $c.Current.IsEnabled, $(if ($tp) { $tp.Current.ToggleState } else { '-' }))
    }
    foreach ($c in ($all | Where-Object { $_.Current.Name -match '原文已缓存|原文未缓存|仅原网页' })) {
        Write-Host ("BADGE    {0}" -f (Fmt $c))
    }
    $z = @($all | Where-Object { $_.Current.Name -match '^(页面缩放|A[-＋+]|↺|\d+%)' })
    if ($z.Count) { foreach ($c in $z) { Write-Host ("ZOOM     {0}" -f (Fmt $c)) } } else { Write-Host 'ZOOM     (hidden)' }
    $w = @($all | Where-Object { $_.Current.ClassName -match 'WebView2' })
    Write-Host ("WEBVIEW  {0}" -f $(if ($w.Count) { 'present ' + (Sz $w[0].Current.BoundingRectangle.Width) + 'x' + (Sz $w[0].Current.BoundingRectangle.Height) } else { '(hidden)' }))
}
function SelectByTitle($all, $title) {
    $sel = [System.Windows.Automation.SelectionItemPattern]::Pattern
    foreach ($li in ($all | Where-Object { $_.Current.ClassName -eq 'ListViewItem' })) {
        $names = @(Get-Descendants $li | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name })
        if ($names -contains $title) {
            if ($li.GetSupportedPatterns() -contains $sel) {
                $li.GetCurrentPattern($sel).Select()
                return $true
            }
        }
    }
    return $false
}

$proc = Start-Process -FilePath $exe -PassThru
Write-Host "launched pid=$($proc.Id)"
Start-Sleep -Seconds 12
try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $win = $null
    for ($i = 0; $i -lt 20; $i++) {
        $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
        if ($win) { break }
        Start-Sleep -Seconds 1
    }
    if (-not $win) { Write-Host 'WINDOW NOT FOUND'; return }
    Write-Host "window: $($win.Current.Name)"

    $all = Get-Descendants $win
    DumpState $all '1 默认：缓存优先（首条有缓存）'

    $sw = $all | Where-Object { $_.Current.ClassName -eq 'ToggleSwitch' -and $_.Current.Name -eq '缓存原文' } | Select-Object -First 1
    if ($sw) {
        Write-Host "`n>>> 切到原网页"
        $sw.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Seconds 8
        DumpState (Get-Descendants $win) '2 切到原网页'
        Write-Host "`n>>> 切回缓存原文"
        $s2 = (Get-Descendants $win) | Where-Object { $_.Current.ClassName -eq 'ToggleSwitch' -and $_.Current.Name -eq '原网页' } | Select-Object -First 1
        $s2.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        Start-Sleep -Seconds 5
        DumpState (Get-Descendants $win) '3 切回缓存原文'
    }

    # 未缓存条目：搜索 FAQ（外链，body_available=false）
    Add-Type -AssemblyName System.Windows.Forms
    $box = (Get-Descendants $win) | Where-Object { $_.Current.ClassName -eq 'TextBox' } | Select-Object -First 1
    if ($box) {
        $box.SetFocus()
        $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('FAQ')
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
        Start-Sleep -Seconds 4
    }
    $all3 = Get-Descendants $win
    if (SelectByTitle $all3 'FAQ') {
        Start-Sleep -Seconds 5
        DumpState (Get-Descendants $win) '4 未缓存条目（external 外链 FAQ）'
    } else { Write-Host "`n!!! FAQ item not found in list" }
}
finally {
    Start-Sleep -Seconds 1
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Write-Host "`ncleaned up."
}
