# TAREAS — LockState

> Registro acumulativo. Cada tarea agrega su propia sección (no se borra lo anterior).
>
> **Regla de uso obligatoria (este proyecto, todos los chats):** cada tarea debe gastar
> como **máximo el 3–4 %** del límite de uso diario de 5 h, y el mínimo necesario. Nunca más del 4 %.
> **Git:** todo cambio se commitea y pushea; el autor siempre es el usuario (Nicolas-Corera);
> **nunca** aparece Claude como autor ni coautor en commits ni en ningún lado.

---

## Fase 2 · Etapa 1 — Sincronización celular → PC (en vivo + cola offline)
**Fecha:** 2026-10-17 · **Rama:** `fase2-sync` → `main`

### Contexto / problema
1. Los cambios hechos desde el celular no se reflejaban en el panel abierto de la PC hasta
   cerrar y reabrir la app.
2. Si la PC estaba apagada, el cambio del celular se perdía (el servidor respondía
   "PC sin conexión" y no guardaba nada).

### Qué se hizo
- **Refresco en vivo del panel (PC):** el servicio ahora emite `!CFG` por el named pipe cuando
  la config cambia (extra, gracia u horario desde el celular). El agente reenvía el evento
  (`Agent.CfgChanged`) y `PanelForm` se recarga sola. Si el usuario tiene ediciones sin guardar,
  no se pisan: aparece el aviso "Hay cambios nuevos desde el celular" + botón **Recargar**.
- **Cola offline en la nube:** si la PC está offline, los comandos de "último gana"
  (`extra`, `gracia`, `horario`) se guardan en el Durable Object (`q:<cmd>`, una entrada por tipo).
  `apagar` **nunca** se encola (seguridad: evita apagados tardíos). Al encender la PC y conectar
  el servicio (mensaje `hello`), el servidor reenvía la cola (`queued`); la PC aplica y confirma
  (`qack`) y recién ahí el servidor la borra.
- **Reglas de aplicación en la PC:** `extra` solo se aplica si se pidió el mismo día (sino se
  descarta); `horario`/`gracia` se descartan si hubo un cambio local más nuevo en la PC
  (`Cfg.LocalChangedTs`). Items de más de 30 días se descartan.
- **PWA:** botones de extra/horario/gracia habilitados también offline (se encolan); `apagar`
  sigue deshabilitado offline. Chip "⏳ Pendiente de aplicar: …" según `status.pendientes`.
  Cache del service worker subida a `ch-v3`.

### Archivos tocados
`src/Core.cs` (Cfg.LocalChangedTs), `src/Service.cs` (!CFG + RemoteLocalNewerThan),
`src/Agent.cs` (CfgChanged, !CFG), `src/Ui.cs` (PanelForm: dirty/Reload/aviso),
`src/Remote.cs` (ApplyQueued/qack), `server/src/worker.js` (cola + flushQueue + pendientes),
`server/public/index.html` (offline + chip pendientes), `server/public/sw.js` (cache v3).

### Cómo probar
- Compilar sin ejecutar: `dotnet build src -c Release`.
- VM: (1) panel abierto + cambio desde el celular → se actualiza solo; (2) apagar servicio/PC,
  cambiar horario desde el celular (toast "guardado en la nube"), encender → en
  `C:\ProgramData\BloqueadorHorario\log.txt` aparece `Remoto (cola): horario aplicado` y el
  horario nuevo queda cargado.
- Servidor: `wrangler deploy` (lo despliega el usuario).

### Pendiente
- Desplegar el Worker actualizado y probar el ciclo completo en VM + celular real.

---

## Fase 2 · Etapa 1 (continuación) — Correcciones tras pruebas en la VM
**Fecha:** 2026-10-17 · **Rama:** `main`

### Problemas reportados y qué se hizo
1. **Cola offline no se aplicaba** (el cartel "pendiente" quedaba para siempre): en `src/Remote.cs` el `JsonDocument` del mensaje `queued` se liberaba (`using`) antes de que la tarea en segundo plano leyera los items. Ahora `ApplyQueued(string json)` parsea el JSON dentro del propio método, aplica y envía `qack`.
2. **Gracia / horario no se reflejaban**: el panel de la PC se refresca por `!CFG` (requiere recompilar la app). En el celular, el editor de horario ahora pide el estado fresco a `/api/status` cada vez que se abre (antes usaba `lastDias` en caché).
3. **Pendientes**: `worker.js` → `pendientes()` devuelve el detalle de cada cambio (`cmd`, `min`, `seg`, `dias`). En la PWA el cartel pasó a ser un botón "Pendientes de aplicar (N) ›" que abre un modal scrolleable (ej.: *Horario → Lunes de 08:00 a 20:00*).
4. **Seguridad**: al llegar un `hello` con `phoneKey` distinta (celular revocado/revinculado) el servidor borra toda la cola `q:*`.
5. **Selector de horario (24 h)**: hubo varios intentos. Se cambió el diseño sin que se pidiera (dos desplegables hora/minuto) → se revirtió a `<input type="time">` → `lang="en-GB"` y `lang="es-AR"` no lograron 24 h en Android → campo de texto con máscara (descartado). **Solución final:** un `<select>` por campo (Inicio y Fin) con horas 24 h **cada 15 min** (00/15/30/45), igual que la PC, `width:100%` para que no desborde. Validación `fin > inicio` al guardar.

### Archivos tocados
`src/Remote.cs`, `server/src/worker.js`, `server/public/index.html`.

### Cómo desplegar / probar (la app se compila y prueba en la VM, usuario `testeos`)
- Servidor (Worker + PWA): `cd <ruta>\LockState\server` → `npx wrangler deploy`.
- App (VM): `cd C:\Users\testeos\Documents\LockState\src` → `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish` → doble clic a `publish\BloqueadorHorario.exe` y poner la contraseña existente (el `.exe` es el instalador; **no** se usa `install.ps1`).

### Reglas del proyecto (recordatorio)
- Uso máx. 4 % del límite de 5 h por tarea (el usuario pidió 2-3 % en estas correcciones). Mínima exploración.
- Commit + push de todo, sin preguntar; autor siempre el usuario, nunca Claude.
- Actualizar este archivo al terminar **cada** tarea.
- Dar siempre los comandos de deploy/compilación.

### Pendiente / conocido
- Hallazgos de seguridad del servidor ya existentes (no tocados por pedido): código de vinculación de 6 dígitos sin límite de intentos y `/api/regstore` expuesto vía `id=__dir__`; valores `Start/End` interpolados en HTML.

---

## Fase 2 · Etapa 2 — Pestaña "Restricciones" (bloqueo de archivos/carpetas + internet)
**Fecha:** 2026-10-17 · **Rama:** `main`

### Contexto / pedido
Pestaña nueva ("Restricciones") en el panel de la PC y en la PWA, para: (1) bloquear
archivos/ejecutables/carpetas (no se pueden abrir con doble clic ni de ninguna forma) y
(2) cortar/permitir internet. Rige **siempre** mientras la PC está en uso, salvo modo
mantenimiento o panel desbloqueado con contraseña. Desde el celular se agregan ítems
**navegando el disco de la PC** (explorador remoto), se activan/desactivan y se quitan.

### Qué se hizo
- **Modelo** (`src/Core.cs`): `Cfg.Bloqueos` (`Bloqueo {Id, Ruta, Tipo archivo|carpeta, Activo}`)
  y `Cfg.SinInternet`. Se cifra igual que el resto.
- **Aplicación** (`src/Restrict.cs`, nuevo; integrado en `src/Service.cs`):
  - Capa 1 — **ACL NTFS Deny** al grupo Usuarios (`icacls … /deny *S-1-5-32-545:(RX)` para
    archivos, `(OI)(CI)(RX)` para carpetas). Las rutas aplicadas se guardan en
    `bloqueos.dat` para revertir exacto. El doble clic falla a nivel kernel.
  - Capa 2 — **vigilante de procesos** en el tick (1 Hz): termina cualquier proceso cuya
    ruta caiga en un ítem activo (cubre USB/FAT sin ACL o exe copiado dentro de carpeta bloqueada).
  - **Internet**: regla de firewall saliente `BloqueadorHorario-SinInternet` con
    `-LocalUser 'D:(A;;CC;;;S-1-5-32-545)'` → corta la red solo al usuario estándar; el
    servicio (SYSTEM) conserva su WebSocket (el celular sigue controlando). No se usa `Disable-NetAdapter`.
  - **Reconciliación** idempotente (`Engine.ReconcileRestrict`): se aplica cuando la PC está
    en uso y se **levanta** en mantenimiento / panel desbloqueado; al volver se re-aplica.
  - **Salvaguardas** (`Restrict.EsProtegida`): nunca `C:\Windows`, la carpeta del programa,
    `ProgramData\BloqueadorHorario` ni la raíz de un disco (validado en el servicio, no solo en UI).
- **Panel PC** (`src/Ui.cs`): el panel pasó a **2 pestañas** (`TabControl` oscuro):
  *Horario* (lo de antes) y *Restricciones* (lista con checkbox Activo, Agregar archivo/exe…,
  Agregar carpeta…, Quitar, e interruptor "Bloquear internet"). Se guarda con el mismo botón
  Guardar; reutiliza el aviso/Recargar de `!CFG`.
- **Servidor + remoto** (`server/src/worker.js`, `src/Remote.cs`): comandos nuevos
  `bloq_set`, `bloq_add`, `bloq_del`, `internet` (último-gana; `bloq_set/bloq_del/internet`
  se **encolan** offline con clave por ítem `q:bloq_set:<id>` etc.; `bloq_add` solo online) y
  `ls` (explorador remoto, solo online, request/response por el WS, máx. 300 entradas, sin
  leer contenido; raíz = discos). `status`/`pendientes()` devuelven `bloqueos[]` y `sinInternet`.
  `qack` ahora borra por **clave exacta** (`keys`, vía `_k`).
- **PWA** (`server/public/index.html`, `sw.js`): control segmentado **Control / Restricciones**;
  vista con interruptor de internet, lista de ítems (switch activo + quitar) y **explorador**
  (breadcrumb, navegación de carpetas, botón Bloquear por ítem y "Bloquear esta carpeta").
  Offline: agregar deshabilitado; activar/quitar/internet se encolan (optimista) y aparecen en
  "Pendientes". Cache SW → `ch-v4`.
- **Desinstalador** (`uninstall.ps1`): rol nuevo `--unrestrict` revierte ACL + firewall antes de borrar.

### Archivos tocados
`src/Core.cs`, `src/Restrict.cs` (nuevo), `src/Service.cs`, `src/Remote.cs`, `src/Program.cs`,
`src/Ui.cs`, `server/src/worker.js`, `server/public/index.html`, `server/public/sw.js`, `uninstall.ps1`.

### Cómo desplegar / probar (VM, usuario `testeos`)
- Servidor: `cd <ruta>\LockState\server` → `npx wrangler deploy`.
- App: `cd C:\Users\testeos\Documents\LockState\src` → `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\publish` → doble clic a `publish\BloqueadorHorario.exe` (es el instalador; **no** `install.ps1`).
- Probar: bloquear un `.exe` y una carpeta → doble clic = "Acceso denegado"; con mantenimiento/contraseña se levanta y al salir se re-aplica; exe copiado a ruta bloqueada → el vigilante lo mata. Internet bloqueada: navegador sin red para el usuario, pero el celular sigue controlando. Celular: agregar por explorador, activar/desactivar, quitar; con la PC apagada el toggle queda "Pendiente" y se aplica al encender. Desinstalar: sin ACL Deny ni regla de firewall residuales.

### Regla de uso de esta tarea
- Tope **10 %** del límite de 5 h (lo pidió el usuario por ser una función grande).
