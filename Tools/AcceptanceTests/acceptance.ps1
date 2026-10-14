param([switch]$SkipMouse)
# Shimeji Windows 빌드 수용 테스트 하네스.
# 이 스크립트가 띄운 테스트 창(ShimejiTestWindow)만 이동/최소화하고, 마우스 조작은 캐릭터/테스트 창 위에서만 한다.
$ErrorActionPreference = 'Stop'
$SP = Split-Path $MyInvocation.MyCommand.Path
$B = "D:\UnityProjects\Shimeji\Builds\Win64"
. "$SP\win.ps1"
. "$SP\enumwin.ps1" -ProcId 0 | Out-Null

$results = New-Object System.Collections.Generic.List[object]
function Report([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail)
}
function Status {
    for ($i = 0; $i -lt 20; $i++) {
        try { return [IO.File]::ReadAllText("$B\Logs\status.json") | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 50 }
    }
    return $null
}
function Cmd([string[]]$lines) {
    while (Test-Path "$B\Logs\commands.txt") { Start-Sleep -Milliseconds 50 }
    [IO.File]::WriteAllLines("$B\Logs\commands.txt", $lines)
    $t = 0; while ((Test-Path "$B\Logs\commands.txt") -and $t -lt 40) { Start-Sleep -Milliseconds 50; $t++ }
    Start-Sleep -Milliseconds 300
}
function Actor([string]$name) {
    $st = Status
    if ($null -eq $st) { return $null }
    foreach ($x in @($st.actors)) { if ($x.key -eq $name) { return $x } }
    return $null
}
function WaitFor([scriptblock]$cond, [double]$timeout = 10) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeout) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 150 }
    return $false
}
# 조건을 만족하는 최신 캐릭터 상태를 돌려준다 (상태 파일 갱신 지연 대비)
function ActorWhere([string]$name, [scriptblock]$cond, [double]$timeout = 4) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $last = $null
    while ($sw.Elapsed.TotalSeconds -lt $timeout) {
        $last = Actor $name
        if ($last -and (& $cond $last)) { return $last }
        Start-Sleep -Milliseconds 120
    }
    return $last
}
function RootAt([int]$x, [int]$y) {
    $p = New-Object W+POINT; $p.X = $x; $p.Y = $y
    [W]::GetAncestor([W]::WindowFromPoint($p), 2).ToInt64()
}
function Click([int]$x, [int]$y, [string]$button = 'left') {
    [W]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 120
    $down = if ($button -eq 'left') { 2 } else { 8 }; $up = if ($button -eq 'left') { 4 } else { 0x10 }
    [W]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 110
    [W]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
}
function Ghost([string]$label) {
    Cmd @("pause on"); Start-Sleep 1.5
    Cmd @("ghostcheck")
    $null = WaitFor { (Status).lastResult -like 'ghosts=*' } 5
    $g = (Status).lastResult
    Cmd @("pause off")
    Write-Host "  [ghost] $label : $g"
}
function BodyCenter($a) { @([int]$a.x, [int]($a.y - $a.height * 0.45)) }

$origCursor = New-Object W+POINT; [W]::GetCursorPos([ref]$origCursor) | Out-Null
Get-Process Shimeji -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item "$B\Logs" -Recurse -ErrorAction SilentlyContinue
foreach ($d in "HotBlob", "BrokenModel") { if (Test-Path "$B\Characters\$d") { Remove-Item "$B\Characters\$d" -Recurse } }

$app = Start-Process "$B\Shimeji.exe" -ArgumentList '-shimeji-test' -PassThru
$formProc = $null
try {
    # ---------------- M1: 오버레이 ----------------
    $ready = WaitFor { $s = Status; $s -and $s.ready -and @($s.actors).Count -ge 2 } 30
    $s = Status
    Report "M1 start + 2 characters loaded" $ready ("actors=" + ((@($s.actors) | % { "$($_.key)/$($_.rig)" }) -join ','))
    Report "M1 overlay backend = layered" ($s.backend -eq 'layered') "backend=$($s.backend) overlay=$($s.overlay -join ',')"
    $ex = [uint32]$s.exStyle
    Report "M1 overlay styles (LAYERED|TOOLWINDOW|NOACTIVATE)" ((($ex -band 0x80000) -ne 0) -and (($ex -band 0x80) -ne 0) -and (($ex -band 0x08000000) -ne 0)) ("exStyle=0x{0:X8}" -f $ex)
    $wins = @([EW]::List($app.Id) | Where-Object { $_ -match 'vis=True' })
    $nonTool = @($wins | Where-Object { $_ -match 'ex=0x([0-9A-F]+)' -and (([Convert]::ToUInt32($Matches[1], 16) -band 0x80) -eq 0) })
    Report "M1 no taskbar/Alt-Tab window (all visible windows are TOOLWINDOW)" ($nonTool.Count -eq 0) ("visible=" + $wins.Count)
    & "$SP\shot.ps1" -Out "$SP\acc_overlay.png" -Scale 0.5 | Out-Null

    # ---------------- M2/M5: 창 위 착지, 창 이동 추종, 최소화 시 낙하 ----------------
    $formProc = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "$SP\testform.ps1", '-X', '800', '-Y', '600', '-W', '700', '-H', '400' -PassThru
    $null = WaitFor { (Get-Process -Id $formProc.Id).MainWindowHandle -ne 0 } 10
    $form = (Get-Process -Id $formProc.Id).MainWindowHandle
    Start-Sleep 1
    $r = New-Object W+RECT; [W]::GetWindowRect($form, [ref]$r) | Out-Null
    Cmd @("place SampleChibi 1150 300", "place SampleBlob 300 300")
    $a = ActorWhere SampleChibi { param($x) $x.state -eq 'Grounded' -and $x.owner -eq $form.ToInt64() } 6
    Report "M2/M5 lands on test window top" ($a.owner -eq $form.ToInt64()) "chibi=($([int]$a.x),$([int]$a.y)) surface=$($a.surface) formTop=$($r.T)"
    Cmd @("force SampleChibi Sit")
    $before = ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' }
    [W]::SetWindowPos($form, [IntPtr]::Zero, 1100, 700, 0, 0, 0x15) | Out-Null
    $after = ActorWhere SampleChibi { param($x) [math]::Abs($x.y - $before.y - 100) -lt 4 } 3
    $dx = $after.x - $before.x; $dy = $after.y - $before.y
    Report "M5 follows window move (+300,+100)" ([math]::Abs($dx - 300) -lt 4 -and [math]::Abs($dy - 100) -lt 4 -and $after.owner -eq $form.ToInt64()) ("delta=({0:0.#},{1:0.#}) state={2}" -f $dx, $dy, $after.state)
    [W]::ShowWindow($form, 6) | Out-Null   # SW_MINIMIZE
    $a = ActorWhere SampleChibi { param($x) $x.surface -eq 'Floor' } 5
    Report "M5 window minimized -> falls to floor" ($a.surface -eq 'Floor') "surface=$($a.surface) y=$([int]$a.y)"
    [W]::ShowWindow($form, 9) | Out-Null   # SW_RESTORE
    Start-Sleep 1
    [W]::GetWindowRect($form, [ref]$r) | Out-Null
    Cmd @("place SampleChibi $($r.L + 300) $($r.T - 200)")
    $null = ActorWhere SampleChibi { param($x) $x.state -eq 'Grounded' -and $x.owner -eq $form.ToInt64() } 6
    Cmd @("force SampleChibi Sit")

    Ghost 'after window tests'
    # 마우스 테스트 동안 블롭은 멀리 떨어뜨려 재운다 (겹치면 블롭이 집힐 수 있음)
    Cmd @("place SampleBlob 2350 300")
    $null = ActorWhere SampleBlob { param($x) $x.state -eq 'Grounded' } 5
    Cmd @("force SampleBlob Sleep")

    if (-not $SkipMouse) {
        # ---------------- M1: 클릭 통과 / 캐릭터 위에서만 입력 ----------------
        $emptyX = $r.L + 120; $emptyY = $r.T + 250
        [W]::SetCursorPos($emptyX, $emptyY) | Out-Null; Start-Sleep -Milliseconds 400
        $root = RootAt $emptyX $emptyY
        $s = Status
        Report "M1 click-through on empty area (WindowFromPoint = test window)" ($root -eq $form.ToInt64() -and $s.clickThrough) "clickThrough=$($s.clickThrough)"
        $a = ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' -and $x.owner -eq $form.ToInt64() }
        $c = BodyCenter $a
        [W]::SetCursorPos($c[0], $c[1]) | Out-Null; Start-Sleep -Milliseconds 400
        $s = Status
        $root = RootAt $c[0] $c[1]
        Report "M1 captures input over character" ((-not $s.clickThrough) -and $s.capturing -and $root -eq $s.hwnd) "clickThrough=$($s.clickThrough) root=$root overlay=$($s.hwnd)"

        # ---------------- M6: 클릭 / 더블클릭 / 우클릭 메뉴 ----------------
        Click $c[0] $c[1]
        $a = ActorWhere SampleChibi { param($x) $x.behavior -eq 'ClickReact' } 1.5
        Report "M6 click -> ClickReact" ($a.behavior -eq 'ClickReact') "behavior=$($a.behavior)"
        Start-Sleep -Milliseconds 600
        Cmd @("force SampleChibi Sit")
        $c = BodyCenter (ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' })
        Click $c[0] $c[1]; Start-Sleep -Milliseconds 80; Click $c[0] $c[1]
        $a = ActorWhere SampleChibi { param($x) $x.behavior -eq 'DoubleClickReact' } 1.5
        Report "M6 double click -> DoubleClickReact (dance)" ($a.behavior -eq 'DoubleClickReact') "behavior=$($a.behavior) anim=$($a.anim)"
        Start-Sleep -Milliseconds 500
        Cmd @("force SampleChibi Sit")
        $c = BodyCenter (ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' })
        Click $c[0] $c[1] 'right'
        $null = WaitFor { (Status).menuOpen } 1.5
        $s = Status
        & "$SP\shot.ps1" -Out "$SP\acc_menu.png" -X ($c[0] - 250) -Y ($c[1] - 250) -W 600 -H 650 -Scale 1 | Out-Null
        Report "M6 right click -> context menu opens" ([bool]$s.menuOpen) "menuOpen=$($s.menuOpen)"
        # 두 번째 항목 '앉기' (실제 메뉴 위치 기준. 항목 높이 30, 패딩 6)
        $mr = $s.menuRect
        $mx = [int]$mr[0] + 90; $my = [int]$mr[1] + 6 + 15 + 30 * 1
        # 안전장치: 메뉴 좌표가 유효하고 커서 근처일 때만 클릭 (다른 앱을 누르지 않도록)
        if (($mr[2] - $mr[0]) -gt 100 -and [math]::Abs($mx - $c[0]) -lt 400 -and [math]::Abs($my - $c[1]) -lt 500) { Click $mx $my }
        else { Write-Host "  [skip] invalid menuRect $($mr -join ',')" }
        $null = WaitFor { -not (Status).menuOpen } 2
        $a = ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' } 1.5
        $s = Status
        Report "M6 menu item 2 (Sit) -> Sit, menu closes" ((-not $s.menuOpen) -and $a.behavior -eq 'Sit' -and (-not $s.paused)) "menuOpen=$($s.menuOpen) behavior=$($a.behavior) paused=$($s.paused)"

        # ---------------- M6: 잡아서 다른 창 위에 놓기 ----------------
        Cmd @("place SampleChibi 500 900")
        $null = ActorWhere SampleChibi { param($x) $x.state -eq 'Grounded' -and $x.y -gt 1300 } 6
        Cmd @("force SampleChibi Sit")
        $a = ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' -and $x.y -gt 1300 }
        $c = BodyCenter $a
        $pre = "pre=($([int]$a.x),$([int]$a.y)) $($a.state)/$($a.behavior)"
        [W]::GetWindowRect($form, [ref]$r) | Out-Null
        $tx = $r.L + 350; $ty = $r.T - 150
        [W]::SetCursorPos($c[0], $c[1]) | Out-Null; Start-Sleep -Milliseconds 300
        [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        $held = $false
        for ($i = 1; $i -le 30; $i++) {
            $x = [int]($c[0] + ($tx - $c[0]) * $i / 30); $y = [int]($c[1] + ($ty - $c[1]) * $i / 30)
            [W]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 30
            if ($i -eq 25) { $held = ((Actor SampleChibi).state -eq 'Held') }
        }
        Start-Sleep -Milliseconds 300   # 멈춘 뒤 놓기 → 속도 0
        [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        $a = ActorWhere SampleChibi { param($x) $x.state -eq 'Grounded' -and $x.owner -eq $form.ToInt64() } 6
        Report "M6 drag (Held) and drop onto another window" ($held -and $a.owner -eq $form.ToInt64()) "heldDuringDrag=$held final=$($a.state)/$($a.surface) pressAt=($($c[0]),$($c[1])) $pre"

        # ---------------- M6: 던지기 → 포물선 → 벽/바닥 착지 ----------------
        Cmd @("force SampleChibi Sit")
        $a = ActorWhere SampleChibi { param($x) $x.behavior -eq 'Sit' }
        $c = BodyCenter $a
        [W]::SetCursorPos($c[0], $c[1]) | Out-Null; Start-Sleep -Milliseconds 300
        [W]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
        for ($i = 1; $i -le 8; $i++) { [W]::SetCursorPos($c[0] - $i * 4, $c[1] - $i * 6) | Out-Null; Start-Sleep -Milliseconds 25 }
        for ($i = 1; $i -le 6; $i++) { [W]::SetCursorPos($c[0] - 32 - $i * 45, $c[1] - 48 - $i * 25) | Out-Null; Start-Sleep -Milliseconds 16 }
        [W]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
        $thrown = $false; $minY = 99999
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 6) {
            $a = Actor SampleChibi
            if ($a.behavior -eq 'Thrown') { $thrown = $true }
            if ($a.y -lt $minY) { $minY = $a.y }
            if ($thrown -and ($a.state -eq 'Grounded' -or $a.state -eq 'WallAttached')) { break }
            Start-Sleep -Milliseconds 60
        }
        Report "M6 throw -> flies (Thrown) and lands on floor/window/wall" ($thrown -and ($a.state -eq 'Grounded' -or $a.state -eq 'WallAttached')) ("final=$($a.state)/$($a.surface) at ({0:0},{1:0}) apexY={2:0}" -f $a.x, $a.y, $minY)
        [W]::SetCursorPos($origCursor.X, $origCursor.Y) | Out-Null
        Ghost 'after mouse tests'
    }

    # ---------------- M3: 핫로딩 / 잘못된 파일 ----------------
    Copy-Item "$B\Characters\SampleBlob" "$B\Characters\HotBlob" -Recurse
    $added = WaitFor { Actor HotBlob } 8
    Report "M3 hot-load: folder added -> character appears" ([bool]$added) ""
    New-Item -ItemType Directory "$B\Characters\BrokenModel" | Out-Null
    [IO.File]::WriteAllBytes("$B\Characters\BrokenModel\model.glb", [byte[]](1..64))
    Start-Sleep 3
    $s = Status
    $log = Get-Content (Get-ChildItem "$B\Logs\shimeji_*.log" | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
    $errLogged = [bool]($log | Select-String "BrokenModel")
    Report "M3 broken file -> no crash, error logged, others keep running" ((-not $app.HasExited) -and $errLogged -and @($s.actors).Count -ge 3) ("alive=$(-not $app.HasExited) logged=$errLogged actors=" + @($s.actors).Count)
    Remove-Item "$B\Characters\HotBlob" -Recurse
    Remove-Item "$B\Characters\BrokenModel" -Recurse
    $removed = WaitFor { -not (Actor HotBlob) } 8
    Report "M3 hot-unload: folder removed -> character disappears" $removed ""

    Ghost 'after hot reload'
    # ---------------- M7: 단일 인스턴스 ----------------
    $second = Start-Process "$B\Shimeji.exe" -PassThru
    $exited = $second.WaitForExit(15000)
    Report "M7 single instance (second launch exits)" $exited "exited=$exited"
    if (-not $exited) { $second | Stop-Process -Force }

    # ---------------- 전체화면 앱 감지 ----------------
    $fs = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "$SP\testform.ps1", '-Fullscreen', '-Title', 'ShimejiFullscreenTest' -PassThru
    $hid = WaitFor { (Status).hiddenFullscreen } 8
    $fs | Stop-Process -Force
    $shown = WaitFor { -not (Status).hiddenFullscreen } 8
    Report "Fullscreen app -> overlay hides, then returns" ($hid -and $shown) "hidden=$hid restored=$shown"

    Ghost 'after fullscreen'
    # ---------------- 트레이 ----------------
    Report "M7 tray icon running" ([bool](Status).trayRunning) ""
    Cmd @("tray 2002")
    $scaled = WaitFor { [math]::Abs((Actor SampleBlob).scale - 0.75) -lt 0.01 } 3
    Cmd @("tray 2003")
    Report "M7 tray command (global scale 75% -> 100%)" $scaled ""

    Ghost 'after tray scale'
    # ---------------- M7: 성능 (명세 기준: 캐릭터 1개) ----------------
    Cmd @("debug off")
    New-Item -ItemType Directory "$B\_perf_disabled" -ErrorAction SilentlyContinue | Out-Null
    Move-Item "$B\Characters\SampleBlob" "$B\_perf_disabled\SampleBlob"
    $null = WaitFor { @((Status).actors).Count -eq 1 } 8
    Start-Sleep 3
    $p = Get-Process -Id $app.Id
    $t0 = $p.TotalProcessorTime; $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep 20
    $p.Refresh()
    $cpu = ($p.TotalProcessorTime - $t0).TotalMilliseconds / $sw.Elapsed.TotalMilliseconds / [Environment]::ProcessorCount * 100
    $ws = $p.WorkingSet64 / 1MB
    $s = Status
    Report "M7 performance, 1 character (CPU <= 3%, RAM <= 300MB)" ($cpu -le 3 -and $ws -le 300) ("cpu={0:0.00}% workingSet={1:0}MB private={5:0}MB fps={2:0.0} actors={3} uploadPx/s={4}" -f $cpu, $ws, $s.fps, @($s.actors).Count, $s.uploadPxPerSec, ($p.PrivateMemorySize64 / 1MB))
    Move-Item "$B\_perf_disabled\SampleBlob" "$B\Characters\SampleBlob"
    Remove-Item "$B\_perf_disabled"
    $null = WaitFor { @((Status).actors).Count -eq 2 } 8

    # ---------------- 잔상 검사: 렌더 결과와 실제 창 내용 비교 (모든 테스트 동작 이후) ----------------
    Cmd @("pause on")
    Start-Sleep 2
    Cmd @("ghostcheck")
    $null = WaitFor { (Status).lastResult -like 'ghosts=*' } 5
    $g = (Status).lastResult
    Cmd @("pause off")
    $gn = [int]($g -replace 'ghosts=', '')
    Report "Overlay has no stale pixels (ghost check)" ($gn -ge 0 -and $gn -le 20) $g

    & "$SP\shot.ps1" -Out "$SP\acc_final.png" -Scale 0.5 | Out-Null
    Cmd @("quit")
    $quit = $app.WaitForExit(10000)
    Report "M7 clean quit" $quit ""
}
finally {
    [W]::SetCursorPos($origCursor.X, $origCursor.Y) | Out-Null
    if ($formProc) { $formProc | Stop-Process -Force -ErrorAction SilentlyContinue }
    if (-not $app.HasExited) { $app | Stop-Process -Force }
    foreach ($d in "HotBlob", "BrokenModel") { if (Test-Path "$B\Characters\$d") { Remove-Item "$B\Characters\$d" -Recurse } }
    if (Test-Path "$B\_perf_disabled\SampleBlob") { Move-Item "$B\_perf_disabled\SampleBlob" "$B\Characters\SampleBlob" }
}
""
"==== SUMMARY: {0} passed, {1} failed ====" -f @($results | ? Result -eq PASS).Count, @($results | ? Result -eq FAIL).Count
