# Errores a corregir — Fase 2 · Etapa 2 (Restricciones)

> La pestaña **Restricciones** (bloqueo de archivos/carpetas + internet) se implementó pero
> **no funciona el efecto real**: es como si fuera solo visual. Abajo está cada problema con
> capturas de síntoma y la causa raíz más probable, para arreglarlo en otra sesión.
> **Probado en:** VM VirtualBox "Prueba bloqueador", cuenta **administrador** (`testeos`).

## Contexto de lo ya hecho (commiteado, NO verificado)
Ya se tocó: `src/Core.cs` (modelo `Bloqueo`/`SinInternet`), `src/Restrict.cs` (ACL icacls +
firewall + vigilante de procesos), `src/Service.cs` (timer `RestrictWork` fuera del lock),
`src/Remote.cs` + `server/src/worker.js` (comandos `bloq_set/add/del`, `internet`, `ls`, cola),
`src/Ui.cs` (pestañas + selector), PWA (`index.html`, `sw.js` ch-v5).
Correcciones ya aplicadas pero que **no resolvieron** el problema de fondo:
- Sacar `Restrict.Apply`/`Watchdog` del `lock(L)` a un timer propio.
- No redirigir la salida de `icacls`/PowerShell (evitar cuelgue por buffer lleno) + `icacls /Q /C`.
- Limpiar `restrictDirty` bajo lock antes del trabajo lento.
- Selector del panel apuntado a `C:\` / Este equipo.

**IMPORTANTE para diagnosticar:** nunca se llegó a ver `C:\ProgramData\BloqueadorHorario\log.txt`.
La línea clave es **`Restricciones aplicadas: N ítem(s), internet <bloqueado|libre>`**.
El primer paso de la próxima sesión debería ser **pedir/leer ese log** (dice si `RestrictWork`
corre, con cuántos ítems, y si entra a la rama de internet). Sin eso se adivina.

---

## 1. Selector de carpeta/archivo en la PC da error "Ubicación no disponible"
**Síntoma (capturas 2, 3 y 4):** al tocar "Agregar carpeta…" / "Agregar archivo…" aparece
`C:\WINDOWS\system32\config\systemprofile\Desktop no está disponible`. Aun apuntando el diálogo
a `C:\`, el error vuelve a saltar (la captura 4 muestra que igual se puede navegar a C:, pero el
cartel reaparece).

**Causa raíz:** el **agente/panel corre como SYSTEM** (por diseño, para que un usuario estándar
no lo pueda matar — ver `PROJECT_CONTEXT.md` §2). Los diálogos comunes del shell
(`FolderBrowserDialog`/`OpenFileDialog`) resuelven las carpetas especiales (Escritorio, etc.) del
**perfil de SYSTEM** → `...systemprofile\Desktop`, que no existe → error. `RootFolder`/
`InitialDirectory` no alcanzan: el shell igual sondea esas carpetas.

**Arreglo propuesto:** **no usar los diálogos del shell bajo SYSTEM.** Hacer un **selector propio**
(WinForms, tema oscuro) que liste el filesystem directamente con `DriveInfo.GetDrives()` +
`Directory.EnumerateDirectories/Files` (igual que `Engine.RemoteLs`, que sí anda). Es la misma
lógica que el explorador del celular; conviene compartirla. Alternativa descartada: correr el
panel como usuario (rompe el modelo de seguridad del proyecto).

---

## 2. "Bloquear internet" no desconecta internet
**Síntoma:** se activa (en PC y celular) pero la red sigue andando. No corta nada.

**Causas a revisar (en orden de probabilidad):**
1. **El Firewall de Windows puede estar apagado en la VM** → ninguna regla bloquea. Verificar
   `Get-NetFirewallProfile` (estado Enabled). Si está off, la regla no hace nada. Considerar
   detectar+avisar, o un método alternativo.
2. **Sintaxis de `-LocalUser` con SID de grupo** en `src/Restrict.cs`:
   `New-NetFirewallRule -Direction Outbound -Action Block -LocalUser 'D:(A;;CC;;;S-1-5-32-545)'`.
   Hay que confirmar que una regla saliente scopeada al **grupo Usuarios** por `-LocalUser`
   realmente filtra (puede no matchear por grupo). Probar: regla de bloqueo saliente **sin**
   `-LocalUser` + una regla **Allow** para el exe del servicio (`-Program <ruta exe>`), así corta
   a todos menos al propio servicio (que necesita el WebSocket). Validar con `Get-NetFirewallRule
   -DisplayName 'BloqueadorHorario-SinInternet'` y `Get-NetFirewallRule | Get-NetFirewallPortFilter`.
3. **¿`Restrict.Apply` llega a la rama de internet?** Confirmar en el log `internet bloqueado`.
   Si no aparece, `RestrictWork` no está corriendo o `enforce` da false (ver punto 3 abajo).

**Archivo:** `src/Restrict.cs` (método `Apply`, bloque del firewall) y `RemoveAll`.

---

## 3. Bloqueo de carpetas/archivos no bloquea (PC y celular)
**Síntoma:** se agregan ítems pero se puede abrir todo igual.

**Causas a revisar:**
1. **Cuenta administrador = bypass.** En admin, al abrir una carpeta con Deny, Windows muestra
   "No tenés permiso… **Continuar**" y al tocarlo **se auto-concede acceso**. La ACL `Deny` solo
   es efectiva en **cuenta estándar**. → **Probar en una cuenta estándar**, no admin.
2. **¿`Restrict.Apply` corre y la ACL queda?** Verificar con `icacls "<ruta>"` que aparezca
   `(DENY)` para el grupo Usuarios (`*S-1-5-32-545`). Si no está, Apply no se ejecutó o la lista
   `cfg.Bloqueos` está vacía (porque **agregar falló** por el problema 1 / explorador del celular).
3. **Vigilante de `.exe` (`Restrict.Watchdog`)** debería matar el proceso al abrirlo **incluso en
   admin** (no depende de ACL). Es el mejor test para saber si el motor corre: agregar un `.exe`,
   cerrar el panel, doble clic → debe cerrarse en ~2 s. Si **ni eso** funciona, el timer
   `RestrictWork` no está ejecutándose o `restrictApplied` nunca se pone true → revisar
   `src/Service.cs` (`RestrictWork`, condición `enforce = cfg.MaintUntil==null && !conns.Any(Hold)`;
   ojo si la conexión persistente del agente quedara con `Hold=true`).
4. Confirmar que los ítems realmente se **guardan** en la config: `SETCFG`→`CambiarHorario` copia
   `cfg.Bloqueos` filtrando `EsProtegida`; revisar que no se descarten de más.

**Archivos:** `src/Restrict.cs` (`Apply`/`Watchdog`), `src/Service.cs` (`RestrictWork`,
`CambiarHorario`).

---

## 4. PC apagada: los cambios del celular no quedan en la nube ni aparece el botón "Pendiente"
**Síntoma:** con la PC apagada, lo que se toca en el celular (internet, activar/quitar ítem) no
se guarda y **no aparece el cartel de pendientes**.

**Causas a revisar:**
1. **¿Se desplegó el Worker nuevo?** (`cd server && npx wrangler deploy`). Si no, la cola con las
   claves nuevas (`q:internet`, `q:bloq_set:<id>`, `q:bloq_del:<id>`) y el `qack` por `keys` no
   están en producción.
2. **PWA cacheada:** subir/forzar `sw.js` (está en `ch-v5`) y recargar duro en el celular.
3. En `server/src/worker.js` → `handleCmd` rama offline: confirmar que `internet`/`bloq_set`/
   `bloq_del` se encolen (sí está en el código) y que `isOnline()` dé **false** de verdad cuando
   la PC está apagada (si `this.pc` queda colgado, cae en la rama online y responde "no respondió").
4. En la PWA (`index.html`), `renderRestric` muestra el banner filtrando
   `['internet','bloq_set','bloq_del']` desde `lastPend` (que sale de `status.pendientes`).
   Verificar que `/api/status` devuelva `pendientes` con esos ítems estando offline.

**Archivos:** `server/src/worker.js` (`handleCmd` offline, `pendientes`, `isOnline`),
`server/public/index.html` (`loadRestric`/`renderRestric`), `server/public/sw.js`.

---

## 5. Higiene del repo (menor)
- Se subió por error `publish/BloqueadorHorario.exe` (~68 MB) al repo. Agregar `.gitignore`
  con `publish/` (y `src/bin/`, `src/obj/`, `server/.wrangler/`) y sacarlo del índice
  (`git rm -r --cached publish`).

---

## Plan sugerido para la próxima sesión (orden)
1. **Leer `log.txt`** y correr en la VM: `icacls "<ruta bloqueada>"`, `Get-NetFirewallProfile`,
   `Get-NetFirewallRule -DisplayName 'BloqueadorHorario-SinInternet'`. Con eso se sabe qué capa falla.
2. Arreglar **internet** (firewall) — probablemente firewall apagado o `-LocalUser` por grupo.
3. Arreglar **selector de la PC** con un picker propio (reutilizar lógica de `RemoteLs`).
4. Verificar **ACL + vigilante** en **cuenta estándar**.
5. Arreglar **cola offline** del celular (verificar deploy del Worker + `isOnline`).
6. `.gitignore` y sacar `publish/` del repo.

**Reglas del proyecto:** máx. 10 % de uso por tarea; commit+push de todo; autor siempre el
usuario, nunca Claude; la app se compila/prueba en la VM, nunca en la PC real del usuario.
