Necesito que desarrolles una aplicación para Windows 10/11 en C# (.NET 8) que convierta una PC en una computadora de cibercafé con horario controlado. Quiero código completo, compilable, con instrucciones para generar el .exe final (publicación single-file) y para instalarlo.

## ARQUITECTURA
Tres componentes:
1. **Servicio de Windows** (corre como SYSTEM, inicio automático, con recuperación automática si falla). Es el cerebro: vigila el horario, decide cuándo bloquear y ejecuta el apagado. Debe ser difícil de detener para un usuario estándar.
2. **Agente de sesión** (proceso que corre dentro de la sesión del usuario, lanzado por el servicio apenas se inicia sesión en Windows). Un servicio no puede mostrar ventanas ni capturar teclado en la sesión del usuario (aislamiento de sesión 0), así que este agente es el que instala los hooks de teclado y mouse y muestra el modal de contraseña. Se comunica con el servicio por named pipe.
3. **Panel de configuración** (WinForms o WPF), que se abre solo con contraseña.

## INTERFAZ GRÁFICA
La aplicación tiene interfaz gráfica, con exactamente dos pantallas:
1. **Panel de configuración:** ventana normal de Windows, tema oscuro, diseño limpio y simple, todos los textos en español. Título de la ventana: "Control de Horario". Contiene:
   - Sección "Horario semanal": 7 filas (Lunes a Domingo). Cada fila tiene un checkbox, el nombre del día y dos selectores de hora (Inicio y Fin, formato 24 h). Los selectores quedan deshabilitados (en gris) cuando el checkbox está sin tildar. Una leyenda corta explica: "Tildado = con restricción horaria. Sin tildar = libre todo el día". No agregar checkbox en la fila de encabezado de la tabla.
   - Sección "Tiempo extra": campo numérico en minutos y botón "Aplicar solo hoy".
   - Sección "Opciones": atajo de desbloqueo (con botón "Cambiar"), ventana de gracia en segundos, botón "Cambiar contraseña", y modo mantenimiento (interruptor y duración en minutos).
   - Botones "Guardar" (principal, azul) y "Cerrar" (secundario).
2. **Modal de desbloqueo:** pantalla completa, siempre encima (topmost), fondo oscuro semitransparente y una tarjeta centrada con: ícono de candado, título "Equipo bloqueado", subtítulo "Ingresá la contraseña para desbloquear", campo de contraseña (con opción de mostrar/ocultar), botón "Desbloquear" y, debajo, un mensaje de error en rojo con los intentos restantes ("Contraseña incorrecta. Intentos restantes: N"). El mensaje de error está oculto al abrir el modal y solo aparece después de un intento fallido. Nada más en pantalla.
- Adjunto dos imágenes de diseño (una por pantalla). Usalas como referencia de distribución, colores y estilo. IMPORTANTE: los valores que aparecen en la imagen del panel (horarios 08:00, 17:00, etc., 15 minutos de tiempo extra, 30 segundos de ventana de gracia, 60 minutos de mantenimiento) son solo relleno visual del generador de imágenes. NO deben usarse en el programa. Los campos de hora arrancan vacíos ("--:--") y no se cargan valores de ejemplo ni por defecto de horarios ni de tiempo extra.
- El servicio y el agente de sesión son invisibles para el usuario común: sin ventanas, sin ícono en la bandeja del sistema y sin accesos directos. Las únicas ventanas que existen son el panel y el modal, y solo se abren con el atajo y la contraseña.

## CONFIGURACIÓN DE HORARIOS
- Lista de los 7 días (lunes a domingo). Cada día tiene:
  - Un checkbox que indica si ese día TIENE RESTRICCIÓN HORARIA.
  - Un selector de hora de inicio y otro de hora de fin (formato 24 h).
- Significado del checkbox:
  - **Tildado**: ese día la PC solo se puede usar entre la hora de inicio y la de fin. Fuera de ese rango se bloquea.
  - **Sin tildar**: ese día NO hay restricción. La PC se puede usar las 24 horas y el sistema no la bloquea ni la apaga en ningún momento. Los selectores de hora se deshabilitan visualmente para ese día.
- Un campo aparte de "tiempo extra" (en minutos) que se suma al horario de fin del día actual. Solo aplica a días con restricción. Debe poder aplicarse de forma puntual (solo hoy) sin modificar la configuración semanal.
- IMPORTANTE: no cargar horarios de ejemplo ni valores por defecto en la interfaz. Todo arranca vacío hasta que yo lo configure. Mientras un día no esté configurado, se comporta como "sin restricción".
- La configuración se guarda en un archivo cifrado o protegido, con la contraseña almacenada solo como hash (PBKDF2 o bcrypt), nunca en texto plano.

## LÓGICA DE FUNCIONAMIENTO
- Día sin restricción (checkbox sin tildar): la PC funciona normalmente todo el día.
- Día con restricción: dentro del rango inicio-fin (más tiempo extra si hay), la PC se usa normalmente.
- Al llegar la hora de fin, o si la PC se enciende / se inicia sesión fuera del rango de un día con restricción, comienza la SECUENCIA DE BLOQUEO:
  1. Se activa el BLOQUEO DE ENTRADA (ver sección siguiente): teclado y mouse quedan anulados, salvo el atajo de desbloqueo.
  2. Se inicia una ventana de gracia configurable (por defecto 20-30 segundos) durante la cual el administrador puede desbloquear con el atajo.
  3. Si nadie desbloquea, al terminar la ventana: se deshabilita la red, se cierran todas las aplicaciones de forma forzada y se apaga la PC de inmediato, sin avisos, sin diálogos, sin cuenta regresiva (equivalente a `shutdown /s /f /t 0`).
- Sin ventanas de aviso para el usuario común en ningún momento. Todo silencioso. La única ventana que puede aparecer es el modal de contraseña, y solo cuando se usa el atajo.

## ENCENDIDO FUERA DE HORARIO
- Si la PC se enciende fuera de horario en un día con restricción, el servicio actúa apenas se inicia sesión en Windows (incluso con contraseña de cuenta Microsoft): activa el bloqueo de entrada y, pasada la ventana de gracia, apaga.
- Aunque la persona reinicie la PC para "resetear" el bloqueo, el ciclo se repite: enciende, inicia sesión, se bloquea, se apaga.
- En días sin restricción, el servicio no hace nada al encender.

## BLOQUEO DE ENTRADA Y ATAJO DE DESBLOQUEO
- Mientras el bloqueo está activo, el agente de sesión usa hooks de bajo nivel (WH_KEYBOARD_LL y WH_MOUSE_LL) que descartan TODOS los eventos de teclado y mouse, excepto la combinación del atajo (por defecto Ctrl+Alt+L, configurable). No depender solo de BlockInput, porque no es confiable y no permite dejar pasar un atajo.
- Al detectar el atajo durante el bloqueo:
  1. El agente muestra el modal de desbloqueo (ver sección INTERFAZ GRÁFICA) en primer plano, a pantalla completa y siempre encima (topmost), que pide la contraseña del programa. Solo en ese modal se habilitan teclado y mouse.
  2. Mientras el modal está abierto, el temporizador de apagado queda en pausa.
  3. Contraseña correcta: se cierra el modal, se quita el bloqueo de entrada, se cancela el apagado y se abre el panel de configuración. Desde ahí puedo cambiar horarios, tiempo extra y contraseña, y activar un modo mantenimiento que suspende el bloqueo por una duración configurable (por defecto 30 minutos) o hasta que yo lo reactive.
  4. Contraseña incorrecta: máximo 3 intentos. Si fallan los 3, se cierra el modal, se reanuda el bloqueo y sigue la cuenta hacia el apagado.
  5. Si el modal queda abierto sin actividad más de unos segundos configurables, se cierra solo y el bloqueo continúa.
- El atajo también debe funcionar durante el uso normal (sin bloqueo) para abrir el panel con contraseña.
- Si el agente de sesión se cae o se cierra, el servicio debe detectarlo y relanzarlo, y mientras tanto proceder con el apagado según el horario (el fallo del agente nunca debe dejar la PC sin protección).

## SEGURIDAD Y ANTI-MANIPULACIÓN
- El usuario normal no debe poder: cerrar el proceso desde el Administrador de tareas, detener o desinstalar el servicio, ni editar el archivo de configuración.
- Proponer medidas realistas: servicio como SYSTEM, permisos NTFS restringidos, reinicio automático del servicio, un segundo proceso "guardián" que relance al principal, y detección de cambios en la hora del sistema (que no se pueda engañar cambiando el reloj de Windows; considerar sincronizar con hora de internet cuando haya red).
- Explicarme honestamente las limitaciones que no se pueden cubrir por software (por ejemplo: Ctrl+Alt+Supr y Win+L, que Windows maneja a nivel de sistema, arranque en modo seguro, o booteo desde un USB) y qué recomendás hacer (contraseña de BIOS, desactivar arranque desde USB, BitLocker, cuenta de Windows sin permisos de administrador). Como el apagado forzado es la protección real, el bloqueo de entrada es una capa extra.

## REQUISITOS TÉCNICOS
- Código bien comentado y modular (servicio, agente de sesión, scheduler, módulo de bloqueo, panel, seguridad).
- Registro de eventos (log) en un archivo protegido: encendidos, bloqueos, uso del atajo, intentos de contraseña, modo mantenimiento.
- Instalador o script (.bat/PowerShell) que registre el servicio, configure el inicio automático, el lanzamiento del agente y los permisos.
- Estructura de carpetas del proyecto y comandos exactos de compilación.

## PREPARACIÓN PARA CONTROL REMOTO (NO IMPLEMENTAR AHORA)
En una versión futura voy a agregar una app de celular que se conecta a la PC para ver su estado y ejecutar acciones (apagar, sumar tiempo extra, ver horarios). NO desarrolles nada remoto en esta versión, pero diseñá la arquitectura para que sea fácil agregarlo después:
- Separá la lógica en una capa de "comandos" interna (por ejemplo: ApagarAhora, AgregarTiempoExtra, ObtenerEstado, CambiarHorario). El panel local usa esa capa, y en el futuro la usará también el módulo remoto, sin reescribir nada.
- Dejá definida una interfaz (por ejemplo IRemoteChannel) sin implementación, como punto de extensión.
- Principio obligatorio: el horario local manda siempre. Si no hay internet, o el canal remoto falla, el sistema debe seguir funcionando y bloqueando normalmente según el horario guardado en la PC. El control remoto solo puede agregar tiempo extra o apagar, nunca ser requisito para que el bloqueo funcione.
- En tu plan de arquitectura, proponé cómo conectarías el futuro módulo remoto (conexión saliente desde la PC hacia un servidor intermedio, sin abrir puertos en el router) y qué implicaría que el bloqueo corte la red.

## DOCUMENTACIÓN DE CONTEXTO (OBLIGATORIO EN CADA ETAPA)
Voy a trabajar por etapas y es posible que tenga que continuar con otra instancia de Claude sin acceso a esta conversación. Por eso, al terminar CADA etapa o tarea, creá o actualizá un archivo `PROJECT_CONTEXT.md` en la raíz del proyecto, que contenga:
1. Descripción del proyecto y objetivo, con todos los requerimientos que di en esta conversación (incluyendo los que quedan para versiones futuras).
2. Decisiones tomadas y el porqué (lenguaje, arquitectura, librerías, alternativas descartadas).
3. Estado actual: etapas completadas, etapa en curso y etapas pendientes (con checklist).
4. Estructura de carpetas y descripción de cada archivo importante.
5. Cómo compilar, instalar y probar lo hecho hasta ahora.
6. Problemas conocidos, limitaciones y cosas que no se pudieron cumplir.
7. Próximos pasos concretos, para que otra IA pueda retomar exactamente donde quedó.
Además, incluí al principio del archivo un breve "prompt de continuación" que yo pueda pegarle a otro Claude junto con el archivo para que retome el trabajo. Antes de dar por terminada una etapa, verificá que el archivo esté actualizado y avisame.

Antes de escribir código, mostrame un plan breve de la arquitectura y esperá mi confirmación. Si algo del requerimiento no se puede cumplir tal como lo pedí, decímelo y proponé la mejor alternativa.