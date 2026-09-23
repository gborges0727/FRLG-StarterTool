# Diagnostic for the step 2 cross-check: does BeepProbe's onset threshold explain its wider DeviceClock spread?
# Three listeners on the same loopback: BeepProbe selftest mode at onset fraction 0.30 and 0.15, and record mode for peaks.
$bp = 'C:\Users\User\source\flowtimer\BeepProbe\out\x86\BeepProbe.exe'
$wav = 'C:\Users\User\source\FRLG-StarterTool\src\FRLG.StarterTool.App\Resources\beeps\ping1.wav'
$exe = 'C:\Users\User\source\FRLG-StarterTool\src\FRLG.StarterTool.App\bin\publish\win-x64\FRLGStarterTool.exe'
$dir = 'C:\Users\User\source\frlg-beep-results\retest\diag-threshold'
$rec = Join-Path $dir 'rec'
New-Item -ItemType Directory -Force $rec | Out-Null
Get-ChildItem $dir -File | Remove-Item
Get-ChildItem $rec -File | Remove-Item

Start-Process $bp -ArgumentList '--mode', 'selftest', '--selftest-dir', $dir, '--beep', $wav, '--volume', '70', '--max-s', '300', '--csv', (Join-Path $dir 'frac30') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dir 'frac30.log')
Start-Process $bp -ArgumentList '--mode', 'selftest', '--selftest-dir', $dir, '--beep', $wav, '--volume', '70', '--max-s', '300', '--onset-frac', '0.15', '--csv', (Join-Path $dir 'frac15') -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dir 'frac15.log')
Start-Process $bp -ArgumentList '--mode', 'record', '--selftest-dir', $rec, '--max-s', '300', '--abs-thr', '0.02' -WindowStyle Hidden -RedirectStandardOutput (Join-Path $rec 'record.log')
Start-Sleep -Seconds 4

$p = Start-Process -FilePath $exe -ArgumentList '--beep-selftest', '--mode', 'deviceclock', '--presses', '10', '--out', $dir -Wait -PassThru
"self-test exit code $($p.ExitCode)"
New-Item -ItemType File (Join-Path $rec 'stop') -Force | Out-Null
Start-Sleep -Seconds 6
foreach ($name in 'frac30', 'frac15') {
    "== $name"
    Get-Content (Join-Path $dir "$name.log") | Select-String -Pattern 'final beeps|all beeps|minus arrival|threshold'
}
"== peaks from record mode (all onsets)"
Import-Csv (Join-Path $rec 'onsets.csv') | Measure-Object -Property peak -Minimum -Maximum -Average | Select-Object Count, Minimum, Maximum, Average
