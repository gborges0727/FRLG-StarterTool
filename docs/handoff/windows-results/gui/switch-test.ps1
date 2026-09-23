# Step 5 device-switch check: Variable Offset countdown at frame 600 (beeps at 8.0-10.0 s), default output switched
# from the Yeti to the Realtek digital output at +8.7 s, switched back at +10.8 s. Both endpoints are recorded.
. C:\Users\User\source\frlg-beep-results\gui\gui.ps1
function QpcNow { $q = 0L; [G2]::QueryPerformanceCounter([ref]$q) | Out-Null; $q }
function WaitQpc([long]$t) { while ($t - (QpcNow) -gt 300000) { Start-Sleep -Milliseconds 10 }; while ((QpcNow) -lt $t) { } }

$sw = 'C:\Users\User\source\frlg-beep-results\AudioSwitch\out\AudioSwitch.exe'
$yeti = '{0.0.0.00000000}.{eea469c9-ce80-4220-a5fd-24ca98d7f4d9}'
$rtk = '{0.0.0.00000000}.{14aeb1ad-c4b5-4b6d-9186-966eec9e289a}'
$p = Get-Process -Id ([int](Get-Content C:\Users\User\source\frlg-beep-results\gui\pid.txt))

$dirA = New-Rec 'switch-yeti'
$dirB = 'C:\Users\User\source\frlg-beep-results\gui\switch-realtek'
New-Item -ItemType Directory -Force $dirB | Out-Null
foreach ($name in 'recording', 'stop', 'onsets.csv', 'envelope.csv') {
    $old = Join-Path $dirB $name
    if (Test-Path $old) { Remove-Item $old }
}
Start-Process -FilePath 'C:\Users\User\source\flowtimer\BeepProbe\out\x86\BeepProbe.exe' -ArgumentList '--mode', 'record', '--selftest-dir', $dirB, '--max-s', '900', '--abs-thr', '0.02', '--device-id', $rtk -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dirB 'record.log') | Out-Null
for ($i = 0; $i -lt 50 -and -not (Test-Path (Join-Path $dirB 'recording')); $i++) { Start-Sleep -Milliseconds 100 }
Start-Sleep -Milliseconds 700

$ts = 0L
try {
    [G2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 400
    $t0 = [G2]::ClickT(107, 211)
    Start-Sleep -Milliseconds 700
    Type-Text '600'
    [G2]::Key(0x0D) | Out-Null
    WaitQpc ($t0 + 87000000)
    $ts = QpcNow
    & $sw set $rtk
    WaitQpc ($t0 + 108000000)
}
finally {
    & $sw set $yeti
    $tr = QpcNow
}

$onA = Stop-Rec $dirA
New-Item -ItemType File (Join-Path $dirB 'stop') -Force | Out-Null
for ($i = 0; $i -lt 100 -and -not (Test-Path (Join-Path $dirB 'envelope.csv')); $i++) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 300
$onB = Import-Csv (Join-Path $dirB 'onsets.csv')
[G2]::Click(203, 211)

$ms0 = $t0 / 10000.0
"t0,$ms0`nswitch,$($ts / 10000.0)`nrestore,$($tr / 10000.0)" | Set-Content (Join-Path $dirA 'actions.csv')
"switched to Realtek at +{0:F1} ms, restored Yeti at +{1:F1} ms" -f (($ts - $t0) / 10000.0), (($tr - $t0) / 10000.0)
"Yeti loopback:"
foreach ($o in $onA) { "  {0}: +{1,9:F3} ms, peak {2}" -f $o.n, ([double]$o.fit_ms - $ms0), $o.peak }
"Realtek loopback:"
foreach ($o in $onB) { "  {0}: +{1,9:F3} ms, peak {2}" -f $o.n, ([double]$o.fit_ms - $ms0), $o.peak }
& $sw list
Get-Content (Get-ChildItem "$env:APPDATA\frlg-startertool\runs" | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
