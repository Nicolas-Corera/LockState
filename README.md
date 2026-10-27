# LockState

Sistema de bloqueo de PC para Windows, al estilo cibercafe. Corre como servicio del sistema, bloquea el equipo y solo permite el acceso con la contrasena de administrador, controlando el horario de uso de la maquina.

> **Estado del proyecto: en desarrollo.** Lo central ya funciona (instalacion, servicio, bloqueo y contrasena), pero hay mejoras pendientes. Mira la seccion [Hoja de ruta](#hoja-de-ruta).

---

## Caracteristicas

- **Servicio de Windows** que arranca automaticamente con el sistema y corre con la cuenta SYSTEM.
- **Agente de sesion** que se lanza al iniciar sesion y se encarga del bloqueo en pantalla.
- **Atajo de teclado** para accionar el bloqueo (por defecto `Ctrl+Alt+L`).
- **Contrasena de administrador** definida durante la instalacion. Se guarda unicamente como hash PBKDF2 dentro de una configuracion cifrada, nunca en texto plano.
- **Control de horario de uso** del equipo.
- **Proteccion contra manipulacion**:
  - Los directorios de instalacion y de datos solo son accesibles para SYSTEM y Administradores (permisos NTFS).
  - Los usuarios estandar no pueden detener ni eliminar el servicio (DACL del servicio restringida).
  - Reinicio automatico del servicio ante fallos.
  - Una tarea programada "guardian" revisa cada minuto que el servicio siga activo y lo reinicia si alguien lo detuvo.
- **Instalacion y desinstalacion** con un solo script de PowerShell.

## Estructura del repositorio

```
.
├── src/             # Codigo fuente de la aplicacion
├── server/          # Componente servidor
├── publish/         # Salida de la compilacion (BloqueadorHorario.exe)
├── install.ps1      # Instala el servicio y configura la contrasena
├── uninstall.ps1    # Desinstala el servicio y limpia la configuracion
└── README.md
```

<!-- TODO: aclarar en una linea que hace cada carpeta, en especial server/ -->

## Requisitos

- Windows 10 u 11.
- PowerShell con permisos de **Administrador**.
- El ejecutable compilado en `publish\BloqueadorHorario.exe`.

## Instalacion

1. Compila el proyecto y verifica que exista `publish\BloqueadorHorario.exe`.
2. Abre PowerShell **como Administrador** en la carpeta del repositorio.
3. Ejecuta:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

4. El script te pide elegir y repetir la contrasena del programa (minimo 4 caracteres, sin comillas dobles).
5. Al terminar, el servicio queda instalado e iniciado. El agente se lanza en cada inicio de sesion.

Que hace `install.ps1`:

| Paso | Detalle |
|---|---|
| Validacion | Verifica permisos de administrador y que exista el `.exe` |
| Copia | Instala en `C:\Program Files\BloqueadorHorario` y usa `C:\ProgramData\BloqueadorHorario` para datos |
| Permisos | Restringe ambas carpetas a SYSTEM y Administradores |
| Contrasena | Ejecuta `--init-password` y guarda solo el hash PBKDF2 |
| Servicio | Crea el servicio `BloqueadorHorario` en inicio automatico, con reinicio ante fallos |
| Guardian | Crea la tarea programada `BloqueadorHorarioGuardian`, cada minuto |

## Uso

- Una vez instalado, el equipo se bloquea segun la configuracion de horario.
- El atajo por defecto para el bloqueo es `Ctrl+Alt+L`.
- Para desbloquear o cambiar la configuracion se necesita la contrasena de administrador.

## Desinstalacion

Desde PowerShell como Administrador:

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
```

## Hoja de ruta

Funciona hoy, pero estas son las mejoras previstas:

- [ ] Evitar pasar la contrasena como argumento de linea de comandos durante la instalacion (hoy podria ser visible brevemente en la lista de procesos) y permitir comillas en la contrasena.
- [ ] Panel de administracion para configurar horarios y tiempos de uso sin tocar archivos.
- [ ] Registro de eventos: intentos fallidos de contrasena, desbloqueos y horarios de uso.
- [ ] Soporte para multiples equipos administrados desde un puesto central.
- [ ] Instalador grafico (MSI o EXE) en lugar del script de PowerShell.
- [ ] Pruebas automatizadas para la logica de horarios y validacion de contrasena.
- [ ] Documentacion completa de configuracion y solucion de problemas.

## Desarrollo asistido por IA

Este proyecto fue desarrollado con ayuda de inteligencia artificial: una parte importante del codigo y de la documentacion fue generada o asistida por herramientas de IA, y luego revisada, probada y ajustada por el autor. Por ser un proyecto en desarrollo, puede contener errores o decisiones de diseno mejorables. Antes de usarlo en un entorno real, revisa el codigo y probalo en una maquina virtual.

## Advertencia

LockState esta pensado para equipos propios o administrados con autorizacion (cibercafes, aulas, locutorios, control parental). No lo instales en equipos ajenos sin permiso. Al ser un proyecto en desarrollo, probalo primero en una maquina virtual y conserva siempre un acceso de recuperacion antes de usarlo en produccion.

## Contribuir

Las sugerencias y reportes de errores son bienvenidos. Abri un issue describiendo el problema o la mejora que propones.

## Autor

**Nicolas Corera**
[GitHub](https://github.com/Nicolas-Corera)

## Licencia

Distribuido bajo la licencia MIT. Consulta el archivo [LICENCE.md](./LICENCE.md) para mas informacion.
