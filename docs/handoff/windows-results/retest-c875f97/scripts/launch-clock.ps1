# Step 5 of the retest: close the fallback instance and start the app without FRLG_DISABLE_AUDIOCLOCK.
. C:\Users\User\source\frlg-beep-results\gui\gui.ps1
$old = Get-Process -Id ([int](Get-Content C:\Users\User\source\frlg-beep-results\gui\pid.txt)) -ErrorAction SilentlyContinue
if ($old) { $old.CloseMainWindow() | Out-Null; if (-not $old.WaitForExit(10000)) { throw 'the fallback instance did not exit' } }
if (Test-Path Env:FRLG_DISABLE_AUDIOCLOCK) { Remove-Item Env:FRLG_DISABLE_AUDIOCLOCK }
"FRLG_DISABLE_AUDIOCLOCK set: $(Test-Path Env:FRLG_DISABLE_AUDIOCLOCK)"
$p = Start-Tool
$p.Id | Set-Content C:\Users\User\source\frlg-beep-results\gui\pid.txt
"pid $($p.Id)"
