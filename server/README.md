# Servidor de control remoto (Cloudflare Workers)

Relé gratis entre la PC (WebSocket saliente) y el celular (PWA). Sin abrir puertos en el router.
El horario local de la PC siempre manda: esto solo transporta comandos acotados (ver estado, sumar tiempo extra, apagar, cambiar horario).

## Qué contiene
- `src/worker.js` — Worker + Durable Object `PcRoom` (relé y directorio de códigos).
- `public/` — la PWA (app del celular): `index.html`, `sw.js`, `manifest.webmanifest`, `icon.svg`.
- `wrangler.toml` — configuración. Usa Durable Objects con SQLite (disponible en el **plan gratis**).

## Desplegar (una sola vez)
Necesitás una cuenta gratis de Cloudflare y Node.js instalado.

```bash
cd server
npm install -g wrangler        # o: npm i -D wrangler y usar npx wrangler
wrangler login                 # abre el navegador para autorizar
wrangler deploy
```

Al terminar te da una URL tipo `https://control-horario.TUCUENTA.workers.dev`.
Esa es la **URL del servidor** que vas a pegar en el panel de la PC (sección "Control remoto").

Para ver logs en vivo: `wrangler tail`.

## Cómo se usa
1. En la PC: panel → **Control remoto…** → pegás la URL → **Vincular celular**. Aparece un **QR** y un **código de 6 dígitos**.
2. En el celular: abrís la URL en el navegador (Chrome/Safari), "Agregar a pantalla de inicio" para instalarla como app.
3. En la app: ponés un nombre (y contraseña opcional, solo local) → **Vincular PC**: escaneás el QR o ingresás el código.
4. Listo: ves el estado en tiempo real y podés sumar tiempo extra, apagar o cambiar el horario.

## Seguridad
- Todo por HTTPS/WSS. La PC se autentica con su token; el celular con otra clave (`PhoneKey`).
- **Desvincular celular** (en el panel) regenera la `PhoneKey`: el celular viejo queda sin acceso.
- El tope de tiempo extra por comando remoto se aplica **en la PC** (`RemoteExtraCapMinutes`, 240 min por defecto).
- Si la PC está offline, el servidor responde "PC sin conexión" y **no encola** comandos (no hay apagados tardíos).
- Lo peor que puede hacer un atacante con la clave del celular es apagar, sumar extra acotado o cambiar el horario; nunca lee ni cambia la contraseña del programa.

## Notas
- El código de vinculación dura 5 minutos y es de un solo uso.
- El QR codifica `https://<servidor>/#id=<PcId>&k=<PhoneKey>`: abrirlo en el celu vincula directo.
- Plan gratis de Workers: suficiente para uso personal (una PC). Durable Objects SQLite entran en el free tier.
