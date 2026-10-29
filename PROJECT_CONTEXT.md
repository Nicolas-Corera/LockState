# PROJECT_CONTEXT — Control de Horario (Bloqueador)

## Prompt de continuación (pegar a otro Claude junto con este archivo)
> Retomá el proyecto "Control de Horario" (C# .NET 8, Windows) descrito en PROJECT_CONTEXT.md. El código está en `src/`. Nunca ejecutes la app en la PC del usuario: se prueba en su VM; solo escribí/corregí archivos y dá instrucciones. Gastá el mínimo de tokens. Empezá por la sección "Próximos pasos". Idioma: español rioplatense.

## 1. Objetivo y requerimientos (resumen fiel del pedido)
App para Windows 10/11 que convierte una PC en "cibercafé con horario". Tres componentes: **servicio** (SYSTEM, autoinicio, recuperación automática), **agente de sesión** (hooks de teclado/mouse, modal, invisible) y **panel** (solo con contraseña). Solo existen 2 pantallas, ambas en español y tema oscuro:
- **Panel "Control de Horario"**: 7 filas Lunes–Domingo (checkbox + Inicio/Fin 24 h, deshabilitados si no está tildado; leyenda "Tildado = con restricción horaria. Sin tildar = libre todo el día."; sin checkbox en el encabezado), "Tiempo extra" (minutos + "Aplicar solo hoy"), "Opciones" (atajo + "Cambiar", ventana de gracia en segundos, "Cambiar contraseña", modo mantenimiento con interruptor y duración), botones Guardar (azul) / Cerrar.
- **Modal de desbloqueo**: pantalla completa, topmost, fondo oscuro semitransparente, tarjeta con candado, "Equipo bloqueado", "Ingresá la contraseña para desbloquear", campo con mostrar/ocultar, "Desbloquear", error rojo "Contraseña incorrecta. Intentos restantes: N" oculto hasta el primer fallo.
- Las imágenes de diseño traían valores de relleno (08:00, 15 min, 30 s, 60 min): **NO usarlos**; horarios arrancan en "--:--", todo vacío. Día sin configurar = sin restricción.
- Lógica: día tildado → solo usable entre inicio y fin (+ tiempo extra de hoy). Fuera de rango, o al encender/iniciar sesión fuera de rango → bloqueo de entrada (hooks WH_KEYBOARD_LL/WH_MOUSE_LL descartan todo salvo el atajo, Ctrl+Alt+L por defecto) + ventana de gracia (default 30 s); si nadie desbloquea: red deshabilitada, apagado forzado inmediato y silencioso (`shutdown /s /f /t 0`). Día sin tildar: no hace nada.
- Atajo: abre modal (también en uso normal). Modal abierto pausa el apagado. Contraseña correcta → quita bloqueo, cancela apagado, abre panel (cambiar horarios, extra, contraseña, modo mantenimiento, default 30 min o hasta reactivar). 3 intentos máx, luego se reanuda el bloqueo. Inactividad del modal → se cierra solo.
- Si el agente cae, el servicio lo relanza y mientras tanto igual apaga por horario.
- Seguridad: usuario normal no puede matar proceso, parar/desinstalar servicio ni editar config. Config cifrada, contraseña solo como hash PBKDF2. Anti-cambio de reloj (NTP). Log protegido.
- **Futuro (NO implementado)**: app de celular para ver estado/apagar/sumar tiempo extra/ver horarios. Arquitectura preparada (ver §2). El horario local manda siempre; remoto solo puede agregar tiempo extra o apagar.

## 2. Decisiones y porqué
- **Un solo exe** (`BloqueadorHorario.exe`) con roles por argumento (`--service`, `--agent`, `--init-password`). Más simple de publicar (single-file) e instalar.
- **WinForms** (.NET 8, self-contained single-file). Descartado WPF: más código para lo mismo.
- **Agente corre como SYSTEM en la sesión del usuario** (token del servicio duplicado + `TokenSessionId`, `CreateProcessAsUser`, `winsta0\default`). Así un usuario estándar no puede matarlo y puede mostrar UI/hooks. El servicio lo relanza cada 4 s si falta.
- **Named pipe `BloqueadorHorarioPipe`, ACL solo SYSTEM**: un usuario estándar no puede ni conectarse. El servicio verifica la contraseña (el hash nunca sale del servicio). Protocolo de líneas: `HELLO`, `AUTH <b64>`, `MODAL 1|0`, `PANEL 0`, `GETCFG`, `SETCFG <json>`, `EXTRA <min>`, `CHPASS <b64old>|<b64new>`, `SHUTDOWN`, `STATUS`; push del servicio: `!STATE <lock> <atajo> <timeoutModal>`.
- **Estado en el servicio** (tick 1 Hz): AUTH correcto = "hold" (bloqueo suspendido) hasta `PANEL 0` o desconexión; luego, si sigue fuera de horario y sin mantenimiento, reinicia bloqueo con gracia completa.
- **Config**: `%ProgramData%\BloqueadorHorario\config.dat` cifrado con DPAPI (LocalMachine) + ACL SYSTEM/Admins. Hash PBKDF2-SHA256 200k.
- **Reloj**: base NTP/sistema + Stopwatch monótono; reloj atrasado >90 s se ignora (se reintenta NTP); adelantado se acepta (solo puede bloquear antes).
- **Guardián**: DACL del servicio sin permisos para usuarios, `sc failure` (reinicio automático), tarea programada SYSTEM cada minuto (`sc start`), y el agente intenta `sc start` si el pipe no responde.
- **Red**: antes de apagar, `Disable-NetAdapter`; los nombres se guardan en `net.txt` y el servicio los reactiva al iniciar.
- **Control remoto (futuro)**: `ICommands` (ApagarAhora, AgregarTiempoExtra, ObtenerEstado, CambiarHorario) implementada por `Engine`; `IRemoteChannel` definida sin implementar. Plan: servicio paralelo/hilo que abre **conexión saliente** (WebSocket/MQTT sobre TLS) a un servidor intermedio, sin abrir puertos; la app del celular habla con ese servidor; los comandos remotos llaman a `ICommands` con autenticación propia y solo permiten extra/apagar. Si no hay internet el canal falla y el scheduler local sigue igual. Ojo: al bloquear se deshabilita la red, así que el canal remoto se corta justo en el apagado (aceptable); si se quisiera "sumar tiempo" remoto durante la gracia, habría que dejar la red habilitada hasta el final de la gracia (ya es así) y mandar el extra dentro de ese lapso.

## 3. Estado
- [x] Etapa 1: código local completo (servicio, agente, hooks, modal, panel, scheduler, config, reloj, log, instaladores).
- [x] Etapa 2: compilado y PROBADO OK en la VM. Bugs resueltos (ver nota al final de esta sección).
- [x] Etapa 3: control remoto implementado (servidor Cloudflare + PWA + cliente WebSocket en la PC). Falta probarlo en la VM/celular.
- [ ] Futuro: afinar estados de UI remota según uso real.

Bugs que se resolvieron en la VM (clave para no repetirlos):
- `SetUnhandledExceptionMode` debe llamarse ANTES de crear cualquier ventana (si no, crash del agente).
- `install.ps1`: chequeo admin con `WindowsBuiltInRole`, permisos por SID (`*S-1-5-18`, `*S-1-5-32-544`) por Windows en español, `New-Service` en vez de `sc create`, y borrar la tarea guardián + `Stop-Process` antes de copiar el exe.
- **El cliente del named pipe (agente) DEBE abrirse con `PipeOptions.Asynchronous`**: si no, leer (hilo Loop) y escribir (hilo Req) en el mismo handle sincrónico se bloquean mutuamente (deadlock). Era la causa de que el modal no verificara.
- El modal corre en su propio hilo STA y la verificación de contraseña va en segundo plano (nunca congela la pantalla completa).

## 4. Estructura
- `src/BloqueadorHorario.csproj` — proyecto (net8.0-windows, WinForms, single-file, win-x64).
- `src/Program.cs` — entrada y roles.
- `src/Core.cs` — rutas, Log, `Cfg`, `Store` (DPAPI+PBKDF2), `Ntp`, `Clock`, `Scheduler` (+parse de atajo), `ICommands`, `IRemoteChannel`.
- `src/Native.cs` — P/Invoke (sesiones WTS, `CreateProcessAsUser`, hooks, foreground).
- `src/Service.cs` — `SvcHost`, `Engine` (tick, bloqueo, apagado, red, pipe, comandos).
- `src/Agent.cs` — `Link` (cliente pipe), hooks de teclado/mouse, flujo atajo→modal→panel.
- `src/Ui.cs` — `UnlockForm`, `PanelForm`, `KeyCapture`, `PwdChangeForm`, helpers de estilo.
- `install.ps1` / `uninstall.ps1` — instalación (admin).

## 5. Compilar, instalar, probar (en la VM)
```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish
cd ..
powershell -ExecutionPolicy Bypass -File .\install.ps1     # como Administrador; pide la contraseña
```
Prueba: usar una cuenta estándar, tildar el día actual con un fin cercano (ej. dentro de 2 min), Guardar con Ctrl+Alt+L → esperar → bloqueo → gracia → apagado. Log: `C:\ProgramData\BloqueadorHorario\log.txt`. Desinstalar: `uninstall.ps1`.
Primera vez: iniciar sesión con la cuenta admin → Ctrl+Alt+L → contraseña → panel. Para tareas largas usar Modo mantenimiento (si se detiene el servicio a mano, la tarea guardián lo reinicia en ≤1 min).

## 6. Limitaciones conocidas (honestas)
- Sin probar: no se compiló ni ejecutó.
- Ctrl+Alt+Supr y Win+L los maneja Windows a nivel sistema (los hooks no los ven); modo seguro; arrancar desde USB; borrar disco. El apagado forzado es la protección real, el bloqueo de entrada es una capa extra. Recomendado: contraseña de BIOS, desactivar boot USB, BitLocker, y que el usuario use cuenta **estándar** (un administrador de Windows puede desactivar todo).
- Un administrador de Windows puede parar el servicio/borrar la tarea; la protección es contra usuarios estándar.
- Cambiar la zona horaria de Windows desplaza el horario (no se fija zona). Reloj: si se cambia la hora estando apagada la PC y sin red al arrancar, se toma la hora local hasta la primera sincronización NTP.
- El exe no está firmado: SmartScreen/antivirus pueden quejarse; el uso de hooks globales y `CreateProcessAsUser` puede dar falsos positivos.
- Si `config.dat` se corrompe/borra el sistema queda sin restricciones (fail-open) y sin contraseña → reinstalar.
- Atajo "Win" en combinaciones puede chocar con atajos del sistema.
- Pestaña "Tiempo extra": "Aplicar solo hoy" FIJA el extra de hoy (no suma al anterior).
- Timeout del modal (20 s) no tiene control en el panel; está en `Cfg.ModalTimeoutSeconds`.

## 6b. Control remoto (implementado)
Arquitectura: `Celular (PWA) --HTTPS--> Worker Cloudflare (Durable Object) <--WebSocket saliente-- Servicio PC`.
- PC: `src/Remote.cs` (cliente `ClientWebSocket`, implementa `IRemoteChannel`, reconexión con backoff, nunca afecta el tick de bloqueo). Lo arranca `Engine.Start()`. Comandos remotos permitidos: `extra` (con tope `RemoteExtraCapMinutes`), `apagar`, `horario` (solo los 7 días; NO toca atajo/gracia/contraseña). Estado se envía cada 10 s.
- Config (`Cfg`): `RemoteEnabled`, `RemoteUrl`, `PcId`, `PcToken`, `PhoneKey`, `RemoteExtraCapMinutes`. Credenciales se generan al activar; `PhoneKey` se regenera al "Desvincular".
- Panel (`src/Ui.cs` → `RemoteForm`): URL del servidor, "Vincular celular" (muestra QR con `QRCoder` + código de 6 dígitos), "Desvincular", "Desactivar remoto". Comandos por pipe: `RGET/RSET/RCODE/RUNLINK/ROFF`.
- Servidor: `server/` (Cloudflare Worker + DO `PcRoom`). Ver `server/README.md` para desplegar (`wrangler deploy`, plan gratis, DO SQLite).
- PWA: `server/public/index.html` (4 pantallas + lector QR con `jsQR`), `manifest.webmanifest`, `sw.js`, `icon.svg`. Login local sin servidor (nombre + contraseña opcional guardada solo en el celu); vínculo real = QR/código.
- Seguridad: tope de extra aplicado en la PC; el remoto nunca lee/cambia la contraseña del programa.
- **Sincronización en vivo (Fase 2 · Etapa 1):** el servicio emite `!CFG` por el pipe al cambiar la config desde el celular → el panel abierto se recarga solo (sin pisar ediciones sin guardar: aparece aviso + botón "Recargar"). Ver `TAREAS.md`.
- **Cola offline (Fase 2 · Etapa 1):** con la PC apagada, el DO guarda `extra`/`gracia`/`horario` (último gana, una entrada por tipo); `apagar` nunca se encola. Al conectar la PC (`hello`), el servidor reenvía (`queued`), la PC aplica y confirma (`qack`). `extra` solo si es del mismo día; `horario`/`gracia` se descartan si hubo cambio local más nuevo (`Cfg.LocalChangedTs`); items >30 días se descartan. La PWA muestra chip de pendientes y permite encolar offline.

## 7. Próximos pasos
1. Compilar en la VM y corregir errores de compilación si los hay.
2. Probar el ciclo completo (bloqueo, atajo, 3 intentos fallidos, timeout, mantenimiento, reinicio fuera de horario, cambio de reloj).
3. Verificar que el agente SYSTEM muestre bien modal/panel en el escritorio del usuario y que el foco funcione.
4. Ajustes de UI frente a las imágenes de referencia (opcional: bordes redondeados, interruptor tipo toggle).
5. Etapa remota: implementar `IRemoteChannel` (conexión saliente) sin tocar el scheduler.
