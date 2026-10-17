# Instala el servicio. Ejecutar como Administrador:  powershell -ExecutionPolicy Bypass -File .\install.ps1
# Requiere haber compilado antes (ver PROJECT_CONTEXT.md): publish\BloqueadorHorario.exe
param([string]$Exe = "$PSScriptRoot\publish\BloqueadorHorario.exe")
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Ejecutá como Administrador.' }
if (-not (Test-Path $Exe)) { throw "No existe $Exe. Compilá primero." }

$name = 'BloqueadorHorario'
$dir  = "$env:ProgramFiles\BloqueadorHorario"
$data = "$env:ProgramData\BloqueadorHorario"
$dst  = "$dir\BloqueadorHorario.exe"

# Contraseña inicial del programa
$s1 = Read-Host 'Elegí la contraseña del programa (no uses comillas ")' -AsSecureString
$s2 = Read-Host 'Repetila' -AsSecureString
$p1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s1))
$p2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s2))
if ($p1 -ne $p2 -or $p1.Length -lt 4 -or $p1.Contains('"')) { throw 'Contraseñas distintas, menores a 4 caracteres o con comillas.' }

# Si ya estaba instalado, detener primero
schtasks.exe /Delete /TN 'BloqueadorHorarioGuardian' /F 2>$null | Out-Null   # si no, revive el servicio
if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force -ErrorAction SilentlyContinue }
Get-Process BloqueadorHorario -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
New-Item -ItemType Directory -Force $dir, $data | Out-Null
Copy-Item $Exe $dst -Force

# Permisos NTFS: solo SYSTEM y Administradores (usuarios estándar no leen ni editan)
foreach ($p in $dir, $data) { icacls $p /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null }

# Contraseña inicial (se guarda solo como hash PBKDF2 dentro de la config cifrada)
Start-Process -Wait -FilePath $dst -ArgumentList '--init-password', "`"$p1`""

# Servicio: inicio automático, SYSTEM, reinicio automático ante fallos, DACL sin permiso de parar/borrar para usuarios
if (-not (Get-Service $name -ErrorAction SilentlyContinue)) {
    New-Service -Name $name -BinaryPathName "`"$dst`" --service" -DisplayName 'Servicio de Control de Horario' -StartupType Automatic | Out-Null
}
sc.exe description $name "Control de horario de uso del equipo" | Out-Null
sc.exe failure $name reset= 86400 actions= restart/1000/restart/1000/restart/1000 | Out-Null
sc.exe failureflag $name 1 | Out-Null
sc.exe sdset $name 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null

# Guardián: tarea SYSTEM cada minuto que reinicia el servicio si alguien lo detuvo
schtasks.exe /Create /TN 'BloqueadorHorarioGuardian' /SC MINUTE /MO 1 /RU SYSTEM /TR 'sc.exe start BloqueadorHorario' /F | Out-Null

Start-Service $name
Write-Host 'Instalado. El servicio lanza el agente al iniciar sesión. Atajo por defecto: Ctrl+Alt+L.'
