# 逐页采集内容区顶部控件的精确边界（DIP），用于跨页逐像素对齐验证。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'SekaiSync Desktop')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $win) { Write-Output 'WINDOW NOT FOUND'; exit }
$pages = @('资讯', '剧情', '台词', '用语', '实体', '数据源', '同步', '智能体接入')
foreach ($page in $pages) {
    $navCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $page)
    $nav = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $navCond)
    if ($nav) {
        try { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch { Write-Output "NAV FAIL: $page"; continue }
        Start-Sleep -Milliseconds 1500
    } else { Write-Output "NAV NOT FOUND: $page"; continue }
    Write-Output ("=== PAGE $page ===")
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $seen = @{}
    foreach ($e in $all) {
        $r = $e.Current.BoundingRectangle
        if ($r.Height -le 0 -or $r.Width -lt 20) { continue }
        if ($r.Y -lt 90 -or $r.Y -gt 380) { continue }
        $c = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($c -notin @('ComboBox', 'Edit', 'Button', 'CheckBox', 'ToggleButton', 'RadioButton')) { continue }
        $key = "$([math]::Round($r.X)),$([math]::Round($r.Y)),$([math]::Round($r.Width)),$([math]::Round($r.Height)),$c"
        if ($seen[$key]) { continue }
        $seen[$key] = 1
        Write-Output ("{0} [{1}] Y={2} H={3} X={4} W={5}" -f $c, $e.Current.Name, [math]::Round($r.Y), [math]::Round($r.Height), [math]::Round($r.X), [math]::Round($r.Width))
    }
}
