# Desinstala todo. Ejecutar como Administrador.
$ErrorActionPreference = 'Continue'
schtasks.exe /Delete /TN 'BloqueadorHorarioGuardian' /F | Out-Null   # primero el guardián, si no revive el servicio
Stop-Service BloqueadorHorario -Force
Get-Process BloqueadorHorario -ErrorAction SilentlyContinue | Stop-Process -Force
sc.exe delete BloqueadorHorario | Out-Null
Start-Sleep 2
# Revertir restricciones (ACL Deny de archivos/carpetas + regla de firewall) antes de borrar el exe
$exe = "$env:ProgramFiles\BloqueadorHorario\BloqueadorHorario.exe"
if (Test-Path $exe) { Start-Process -Wait -FilePath $exe -ArgumentList '--unrestrict' }
Get-NetFirewallRule -DisplayName 'BloqueadorHorario-SinInternet' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue
Remove-Item "$env:ProgramFiles\BloqueadorHorario" -Recurse -Force
# Reactivar adaptadores de red por si quedaron desactivados
if (Test-Path "$env:ProgramData\BloqueadorHorario\net.txt") { Get-Content "$env:ProgramData\BloqueadorHorario\net.txt" | ForEach-Object { Enable-NetAdapter -Name $_ -Confirm:$false } }
Remove-Item "$env:ProgramData\BloqueadorHorario" -Recurse -Force
Write-Host 'Desinstalado.'
