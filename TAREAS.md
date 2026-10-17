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
