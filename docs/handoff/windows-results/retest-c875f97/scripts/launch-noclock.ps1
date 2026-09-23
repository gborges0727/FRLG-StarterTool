# Step 4 of the retest: start the app with FRLG_DISABLE_AUDIOCLOCK=1 so its WASAPI output skips IAudioClock.
. C:\Users\User\source\frlg-beep-results\gui\gui.ps1
$env:FRLG_DISABLE_AUDIOCLOCK = '1'
$p = Start-Tool
$p.Id | Set-Content C:\Users\User\source\frlg-beep-results\gui\pid.txt
"pid $($p.Id)"
Save-Window $p.MainWindowHandle C:\Users\User\source\frlg-beep-results\retest\step4-main.png
