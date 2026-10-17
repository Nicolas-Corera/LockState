# CLAUDE.md — LockState (reglas obligatorias de este proyecto)

> Léelo al empezar **cada** sesión. Estas reglas mandan sobre cualquier criterio propio.
> Idioma: **español rioplatense**, siempre.

## Uso (tokens)
- **Máximo 5 % por sesión** (idealmente **3–4 %**).
- Solo si la tarea es **realmente complicada**: hasta **10 %**, nunca más.
- Mínima exploración: leer solo lo necesario, no releer archivos ya vistos.

## Cómo trabajar
- **Resolver directo y al grano.** Nada de vueltas, ni listar opciones que no voy a tomar.
- **Explicar todo** lo que hago y por qué, pero conciso.
- **Ante un error: NO pedir logs a lo loco.** Primero **reviso el código** y deduzco la causa
  más probable; recién pido un dato puntual si es imprescindible para cerrar.
- **La palabra del usuario es santa.** Lo que dice el usuario es como él lo dice, no como yo
  pienso. No lo contradigo ni "reinterpreto"; si algo no cierra, pregunto corto, no asumo.
- **Seguir al pie de la letra** las correcciones de errores y **cada etapa/fase** tal como el
  usuario las define. No adelantarse, no cambiar el alcance, no "mejorar" sin que lo pida.

## Git
- **Todo cambio se commitea y se pushea**, sin preguntar.
- El **autor es siempre el usuario** (Nicolas-Corera). **Nunca** Claude como autor ni coautor,
  en ningún lado (ni `Co-Authored-By`, ni en descripciones de PR, ni en comentarios).

## Entorno de prueba
- La app **se compila y prueba en la VM** (usuario `testeos`), **nunca** en la PC real del usuario.
- Yo **no ejecuto ni instalo** en la PC del usuario: escribo/corrijo archivos y **doy los comandos**.
- **Siempre dar los comandos** de deploy/compilación al terminar (Worker: `npx wrangler deploy`;
  app: `dotnet publish …`; etc.).

## Documentación del proyecto
- **`TAREAS.md`**: registro acumulativo; agregar una sección por tarea al terminar (no borrar lo anterior).
- **`PROJECT_CONTEXT.md`**: arquitectura y decisiones. Leerlo para entender el sistema.
- **`errores a corregir.md`**: errores pendientes con causa raíz (si existe, atacarlo al pie de la letra).

## Qué es el sistema (resumen)
"Control de Horario / LockState": convierte una PC Windows en "cibercafé con horario".
C# .NET 8 (WinForms, single-file). Tres roles en un solo exe: **servicio** (SYSTEM, cerebro),
**agente** (sesión del usuario, hooks/modal/panel) y **init**. Config cifrada (DPAPI) + hash
PBKDF2. Control remoto por **Cloudflare Worker + PWA** (WebSocket saliente, sin abrir puertos);
el horario local **siempre manda**. Detalle completo en `PROJECT_CONTEXT.md`.
