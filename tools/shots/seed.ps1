# Writes a state folder with sample data (todos, notes, a calendar, shelf files) so screenshots never show personal content.
param([string]$Pinned = 'music,focus,todo,notes,info,calendar', [string]$Theme = 'dark')
$data = Join-Path $env:TEMP "perch-shots"; New-Item -ItemType Directory -Force $data | Out-Null
$shelf = "$data\shelf"; New-Item -ItemType Directory -Force $shelf | Out-Null
foreach ($n in '季度报告.pdf', '设计稿.png', '会议纪要.docx', '预算表.xlsx', '演示文稿.pptx') { if (-not (Test-Path "$shelf\$n")) { Set-Content "$shelf\$n" 'sample' } }
$now = Get-Date
function Ics($d) { $d.ToString('yyyyMMddTHHmmss') }
$ev = @(
  @('设计评审', $now.AddMinutes(-20), $now.AddMinutes(40), '会议室 A'),
  @('与产品对齐需求', $now.AddHours(2), $now.AddHours(3), '线上会议'),
  @('周报同步', $now.Date.AddDays(1).AddHours(10), $now.Date.AddDays(1).AddHours(10.5), ''),
  @('迭代计划会', $now.Date.AddDays(2).AddHours(14), $now.Date.AddDays(2).AddHours(15), '会议室 B'),
  @('客户演示', $now.Date.AddDays(4).AddHours(9.5), $now.Date.AddDays(4).AddHours(10.5), '3 楼')
)
$ics = "BEGIN:VCALENDAR`r`nVERSION:2.0`r`n"
$i = 0
foreach ($e in $ev) { $i++; $ics += "BEGIN:VEVENT`r`nUID:sample$i@perch`r`nDTSTART:$(Ics $e[1])`r`nDTEND:$(Ics $e[2])`r`nSUMMARY:$($e[0])`r`nLOCATION:$($e[3])`r`nEND:VEVENT`r`n" }
$ics += "END:VCALENDAR`r`n"
Set-Content "$data\sample.ics" $ics -Encoding utf8
$todos = @(
  @{ Text = '整理下周的产品评审材料'; Done = $false; Due = $now.Date.AddHours(18) },
  @{ Text = '给设计组回复配色方案'; Done = $false; Due = $now.Date.AddDays(1).AddHours(12) },
  @{ Text = '预约体检'; Done = $false; Due = $null },
  @{ Text = '写完季度总结'; Done = $true; Due = $null },
  @{ Text = '买咖啡豆'; Done = $true; Due = $null }
) | ForEach-Object { @{ Text = $_.Text; Done = $_.Done; Due = $_.Due; Created = $now.AddDays(-1); Completed = $(if ($_.Done) { $now } else { $null }) } }
$notes = @(
  @{ Title = '周会要点'; Text = "1. 新版本下周四发布`n2. 设计稿本周五前定稿`n3. 修复登录页的兼容问题"; Highlight = $true; Updated = $now.AddMinutes(-12) },
  @{ Title = '读书笔记'; Text = '《深度工作》：把注意力当作稀缺资源来管理。'; Highlight = $false; Updated = $now.AddHours(-5) },
  @{ Title = '购物清单'; Text = '牛奶、鸡蛋、咖啡豆、洗衣液'; Highlight = $false; Updated = $now.AddDays(-1) }
)
$state = @{
  Settings = @{
    Appearance = 'black'; Theme = $Theme; Language = 'zh-CN'; CityName = '杭州'; Latitude = 30.2741; Longitude = 120.1551
    CalendarWindows = $false; CalendarUrls = "$data\sample.ics"; MusicInCompact = $false; HotkeysEnabled = $true
    NavPinned = @($Pinned -split ',')
  }
  Todos = $todos; Notes = $notes
  Commands = @(@{ Name = '打开项目目录'; Command = 'explorer .' }, @{ Name = '查看 IP'; Command = 'ipconfig' })
  TrayFiles = @(Get-ChildItem $shelf | ForEach-Object { $_.FullName })
  Focus = @(@{ Date = $now.ToString('yyyy-MM-dd'); Sessions = 3; Minutes = 75 })
}
$state | ConvertTo-Json -Depth 8 | Set-Content "$data\state.json" -Encoding utf8
"seeded $data (pinned: $Pinned)"
