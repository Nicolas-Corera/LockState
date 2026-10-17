// Servidor intermedio: relé entre la PC (WebSocket saliente) y el celular (HTTP).
// No guarda nada sensible: último estado, tokens de esa PC y códigos de vinculación efímeros.
// El horario local de la PC siempre manda; esto solo transporta comandos acotados.

const json = (o, status = 200) =>
  new Response(JSON.stringify(o), { status, headers: { "content-type": "application/json" } });

export default {
  async fetch(req, env) {
    const url = new URL(req.url);
    const p = url.pathname;

    // Celular resolviendo un código manual -> {id, key}. Va al "directorio" (DO único).
    if (p === "/api/pair") {
      const dir = env.PCROOM.get(env.PCROOM.idFromName("__dir__"));
      return dir.fetch(new Request("https://do/api/pair", req));
    }
    // WebSocket de la PC, WebSocket del celular, y API del celular: van al DO de esa PC (por id).
    if (p === "/pc" || p === "/phone" || p.startsWith("/api/")) {
      const id = url.searchParams.get("id");
      if (!id) return json({ error: "falta id" }, 400);
      const room = env.PCROOM.get(env.PCROOM.idFromName(id));
      return room.fetch(req);
    }
    // Todo lo demás: la PWA (archivos estáticos).
    return env.ASSETS.fetch(req);
  },
};

export class PcRoom {
  constructor(state, env) {
    this.state = state;
    this.env = env;
    this.pc = null;            // WebSocket de la PC
    this.pcToken = null;       // auth de la PC
    this.phoneKey = null;      // auth del celular
    this.lastStatus = null;    // último estado recibido
    this.pcId = null;
    this.pending = new Map();  // reqid -> resolve
    this.seq = 0;
    this.phones = new Set();   // WebSockets de celulares (para push en vivo)
    this.lastSeen = 0;         // última señal de la PC (ms)
  }

  // La PC manda estado cada 10 s. Si pasan +25 s sin señal, está apagada/dormida/sin red,
  // aunque el cierre del WebSocket no haya llegado (apagado brusco, corte de red, suspensión).
  isOnline() { return !!this.pc && (Date.now() - this.lastSeen) < 25000; }

  async pendientes() {
    const q = await this.state.storage.list({ prefix: "q:" });
    // Devuelve el detalle de cada cambio en cola (para mostrarlo en el celular), no solo el tipo.
    return [...q.values()].map((v) => ({ cmd: v.cmd, min: v.min, seg: v.seg, dias: v.dias }));
  }

  async broadcast() {
    const pend = await this.pendientes();
    const st = this.lastStatus ? { ...this.lastStatus, pendientes: pend } : (pend.length ? { pendientes: pend } : null);
    const msg = JSON.stringify({ online: this.isOnline(), status: st });
    for (const ph of this.phones) { try { ph.send(msg); } catch { this.phones.delete(ph); } }
  }

  // Al conectarse la PC, le reenvía los comandos encolados (descarta los de más de 30 días).
  async flushQueue(server) {
    const q = await this.state.storage.list({ prefix: "q:" });
    const items = [];
    const cutoff = Date.now() - 30 * 24 * 3600 * 1000;
    for (const [k, v] of q) {
      if (v.ts && v.ts < cutoff) { await this.state.storage.delete(k); continue; }
      items.push(v);
    }
    if (items.length) { try { server.send(JSON.stringify({ t: "queued", items })); } catch {} }
  }

  // Se programa en cada señal de la PC: si venció sin señal nueva, avisa "desconectada".
  async alarm() {
    if (Date.now() - this.lastSeen >= 25000) { this.pc = null; this.broadcast(); }
  }

  async ensure() {
    if (this.phoneKey == null) this.phoneKey = await this.state.storage.get("phoneKey");
    if (this.pcToken == null) this.pcToken = await this.state.storage.get("pcToken");
    if (this.lastStatus == null) this.lastStatus = await this.state.storage.get("lastStatus");
  }

  authPhone(req) {
    const a = req.headers.get("Authorization") || "";
    return this.phoneKey && a === "Bearer " + this.phoneKey;
  }

  async fetch(req) {
    const url = new URL(req.url);
    this.pcId = url.searchParams.get("id") || this.pcId;
    const p = url.pathname;
    if (p === "/pc") return this.handlePc(req);
    await this.ensure();
    if (p === "/phone") return this.handlePhone(req);
    if (p === "/api/status") return this.handleStatus(req);
    if (p === "/api/cmd") return this.handleCmd(req);
    if (p === "/api/pair") return this.resolveCode(req);     // instancia __dir__
    if (p === "/api/regstore") return this.storeCode(req);   // instancia __dir__ (interno)
    return json({ error: "no encontrado" }, 404);
  }

  // ---------- Lado PC (WebSocket) ----------
  handlePc(req) {
    if (req.headers.get("Upgrade") !== "websocket") return new Response("se espera websocket", { status: 426 });
    const { 0: client, 1: server } = new WebSocketPair();
    server.accept();
    server.addEventListener("message", (ev) => this.onPcMsg(server, ev.data));
    const drop = () => { if (this.pc === server) { this.pc = null; this.broadcast(); } };
    server.addEventListener("close", drop);
    server.addEventListener("error", drop);
    return new Response(null, { status: 101, webSocket: client });
  }

  // WebSocket del celular: recibe el estado en vivo.
  handlePhone(req) {
    if (req.headers.get("Upgrade") !== "websocket") return new Response("se espera websocket", { status: 426 });
    const url = new URL(req.url);
    if (!this.phoneKey || url.searchParams.get("k") !== this.phoneKey) return new Response("no autorizado", { status: 401 });
    const { 0: client, 1: server } = new WebSocketPair();
    server.accept();
    this.phones.add(server);
    const drop = () => this.phones.delete(server);
    server.addEventListener("close", drop);
    server.addEventListener("error", drop);
    try { server.send(JSON.stringify({ online: this.isOnline(), status: this.lastStatus || null })); } catch {}
    return new Response(null, { status: 101, webSocket: client });
  }

  async onPcMsg(server, data) {
    let m;
    try { m = JSON.parse(data); } catch { return; }
    this.lastSeen = Date.now();
    try { this.state.storage.setAlarm(Date.now() + 26000); } catch {}
    await this.ensure();

    if (m.t === "hello") {
      if (this.pcToken && this.pcToken !== m.token) { try { server.close(1008, "token"); } catch {} return; }
      // Si la phoneKey cambió (el usuario revocó/revinculó el celular), la cola vieja es de un celular
      // ya revocado: se descarta entera para que no se apliquen sus comandos pendientes.
      const keyChanged = this.phoneKey != null && this.phoneKey !== m.phoneKey;
      if (keyChanged) {
        const q = await this.state.storage.list({ prefix: "q:" });
        for (const k of q.keys()) await this.state.storage.delete(k);
      }
      this.pc = server;
      this.pcToken = m.token;
      this.phoneKey = m.phoneKey;
      await this.state.storage.put("pcToken", m.token);
      await this.state.storage.put("phoneKey", m.phoneKey);
      try { server.send(JSON.stringify({ t: "ok" })); } catch {}
      await this.flushQueue(server);
      this.broadcast();
      return;
    }
    if (m.t === "qack") {
      // La PC confirmó qué comandos de la cola aplicó: se borran (los no confirmados se reintentan).
      for (const cmd of m.cmds || []) await this.state.storage.delete("q:" + cmd);
      this.broadcast();
      return;
    }
    if (m.t === "status") {
      this.lastStatus = m.data;
      await this.state.storage.put("lastStatus", m.data);
      this.broadcast();
      return;
    }
    if (m.t === "makecode") {
      if (!this.pcId || !this.phoneKey) return;
      const code = "" + Math.floor(100000 + Math.random() * 900000);
      const dir = this.env.PCROOM.get(this.env.PCROOM.idFromName("__dir__"));
      await dir.fetch(new Request("https://do/api/regstore", {
        method: "POST",
        body: JSON.stringify({ code, id: this.pcId, key: this.phoneKey, exp: Date.now() + 300000 }),
      }));
      try { server.send(JSON.stringify({ t: "code", code, ttl: 300 })); } catch {}
      return;
    }
    if (m.t === "res") {
      const w = this.pending.get(m.id);
      if (w) { this.pending.delete(m.id); w(m); }
      return;
    }
  }

  // ---------- Lado celular (HTTP) ----------
  async handleStatus(req) {
    if (!this.authPhone(req)) return json({ error: "no autorizado" }, 401);
    const pend = await this.pendientes();
    const st = this.lastStatus ? { ...this.lastStatus, pendientes: pend } : (pend.length ? { pendientes: pend } : null);
    return json({ online: this.isOnline(), status: st });
  }

  async handleCmd(req) {
    if (!this.authPhone(req)) return json({ error: "no autorizado" }, 401);
    const body = await req.json().catch(() => ({}));
    // Si la PC está offline: los cambios de "último gana" se guardan en la nube y se aplican al encender.
    // "apagar" NUNCA se encola (evita apagados tardíos inesperados).
    if (!this.isOnline()) {
      const cmd = body.cmd;
      if (cmd === "extra" || cmd === "gracia" || cmd === "horario") {
        await this.state.storage.put("q:" + cmd, {
          cmd, min: body.min, seg: body.seg, dias: body.dias, ts: Date.now(),
        });
        return json({ online: false, queued: true, msg: "Guardado en la nube; se aplicará al encender la PC" });
      }
      return json({ online: false, msg: "PC sin conexión" });
    }
    const id = "r" + (++this.seq);
    const res = await new Promise((resolve) => {
      const timer = setTimeout(() => { this.pending.delete(id); resolve({ ok: false, msg: "La PC no respondió" }); }, 8000);
      this.pending.set(id, (m) => { clearTimeout(timer); resolve(m); });
      try {
        this.pc.send(JSON.stringify({ t: "cmd", id, cmd: body.cmd, min: body.min, seg: body.seg, dias: body.dias }));
      } catch { clearTimeout(timer); this.pending.delete(id); resolve({ ok: false, msg: "PC sin conexión" }); }
    });
    return json({ online: true, ok: res.ok, msg: res.msg, status: res.data || this.lastStatus });
  }

  // ---------- Directorio de códigos (instancia __dir__) ----------
  async storeCode(req) {
    const { code, id, key, exp } = await req.json();
    await this.state.storage.put("code:" + code, { id, key, exp });
    return json({ ok: true });
  }

  async resolveCode(req) {
    const { code } = await req.json().catch(() => ({}));
    const rec = await this.state.storage.get("code:" + code);
    if (!rec || rec.exp < Date.now()) return json({ error: "Código inválido o vencido" }, 400);
    await this.state.storage.delete("code:" + code); // un solo uso
    return json({ id: rec.id, key: rec.key });
  }
}
