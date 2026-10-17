// Servicio de Windows (SYSTEM): cerebro. Vigila el horario, ejecuta la secuencia de bloqueo/apagado,
// lanza el agente en la sesión del usuario y atiende comandos por named pipe (solo accesible para SYSTEM).
using System.ServiceProcess;
namespace BloqueadorHorario;

class SvcHost : ServiceBase
{
    Engine? e;
    public SvcHost() { ServiceName = "BloqueadorHorario"; CanStop = true; CanShutdown = true; }
    protected override void OnStart(string[] a) { e = new Engine(); e.Start(); }
    protected override void OnStop() { e?.Stop(); }
    protected override void OnShutdown() { e?.Stop(); }
}

class Conn
{
    public StreamWriter W = null!;
    public bool Authed, Hold, Modal;
    public DateTime ModalSince;
    readonly object wl = new();
    public void Send(string s) { lock (wl) { try { W.WriteLine(s); } catch { } } }
}

class Engine : ICommands
{
    Cfg cfg = new();
    readonly List<Conn> conns = new();
    readonly object L = new();
    bool lockActive; DateTime deadline;
    volatile bool restrictApplied;        // true = restricciones NTFS/firewall aplicadas ahora
    volatile bool restrictDirty = true;   // la config de restricciones cambió: reconciliar
    readonly object RL = new();           // serializa el trabajo pesado de restricciones (icacls/firewall)
    System.Threading.Timer? tick, restrictTimer;
    readonly DateTime started = DateTime.UtcNow;
    DateTime lastLaunch = DateTime.MinValue;

    public void Start()
    {
        Log.W("Servicio iniciado");
        Task.Run(RestoreNet);
        Clock.Init();
        cfg = Store.Load();
        new Thread(PipeLoop) { IsBackground = true }.Start();
        StartRemote();
        tick = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        // Restricciones en un hilo aparte: icacls/firewall pueden tardar segundos y NUNCA deben frenar el tick ni el pipe/WS.
        restrictTimer = new System.Threading.Timer(_ => RestrictWork(), null, 1500, 2000);
    }
    public void Stop() { tick?.Dispose(); restrictTimer?.Dispose(); try { remote?.Stop(); } catch { } Log.W("Servicio detenido"); }

    // ---------- Bucle principal (1 Hz) ----------
    void Tick()
    {
        lock (L)
        {
            try
            {
                Clock.Check();
                var now = Clock.Now;
                EnsureAgent();
                if (cfg.MaintUntil != null && now >= cfg.MaintUntil) { cfg.MaintUntil = null; Store.Save(cfg); Log.W("Modo mantenimiento terminado"); RPush(); }
                bool maint = cfg.MaintUntil != null;
                bool hold = conns.Any(c => c.Hold);
                if (Scheduler.IsAllowed(cfg, now) || maint || hold)
                {
                    if (lockActive) EndLock();
                    return;
                }
                if (!lockActive)
                {
                    // Espera a que haya sesión de usuario (o 3 min tras el arranque) para no apagar antes de poder desbloquear.
                    if (!SessionReady() && (DateTime.UtcNow - started).TotalSeconds < 180) return;
                    StartLock(now);
                    return;
                }
                // Mientras el modal está abierto el temporizador queda en pausa (con tope por si el agente se cuelga).
                if (conns.Any(c => c.Modal && (DateTime.UtcNow - c.ModalSince).TotalSeconds < cfg.ModalTimeoutSeconds + 15))
                { deadline = deadline.AddSeconds(1); return; }
                if (now >= deadline) DoShutdown();
            }
            catch (Exception e) { Log.W("Tick: " + e.Message); }
        }
    }

    /// <summary>
    /// Trabajo de restricciones, fuera del lock principal y del tick. Las restricciones rigen SIEMPRE que la PC
    /// está en uso, salvo mantenimiento o panel desbloqueado. Toma una foto bajo lock (rápido) y recién después
    /// corre icacls/firewall (lento) sin bloquear a nadie.
    /// </summary>
    void RestrictWork()
    {
        if (!Monitor.TryEnter(RL)) return;   // si una pasada anterior sigue corriendo, saltear
        try
        {
            bool enforce; bool sinInet; List<Bloqueo> snap; bool reconcile;
            lock (L)
            {
                enforce = cfg.MaintUntil == null && !conns.Any(c => c.Hold);
                sinInet = cfg.SinInternet;
                snap = cfg.Bloqueos.Select(b => new Bloqueo { Id = b.Id, Ruta = b.Ruta, Tipo = b.Tipo, Activo = b.Activo }).ToList();
                reconcile = restrictDirty || enforce != restrictApplied;
            }
            if (reconcile)
            {
                if (enforce) Restrict.Apply(snap, sinInet); else Restrict.RemoveAll();
                restrictApplied = enforce; restrictDirty = false;
            }
            if (enforce && restrictApplied) Restrict.Watchdog(snap);
        }
        catch (Exception e) { Log.W("Restrict: " + e.Message); }
        finally { Monitor.Exit(RL); }
    }

    void StartLock(DateTime now)
    {
        lockActive = true; deadline = now.AddSeconds(Math.Max(5, cfg.GraceSeconds));
        Log.W($"BLOQUEO iniciado; apagado a las {deadline:HH:mm:ss}");
        Broadcast(StateMsg()); RPush();
    }
    void EndLock() { lockActive = false; Log.W("Bloqueo terminado / cancelado"); Broadcast(StateMsg()); RPush(); }
    string StateMsg() => $"!STATE {(lockActive ? 1 : 0)} {cfg.Shortcut} {cfg.ModalTimeoutSeconds}";
    void Broadcast(string m) { foreach (var c in conns.ToArray()) c.Send(m); }

    static bool SessionReady() { var s = Native.WTSGetActiveConsoleSessionId(); return s != 0xFFFFFFFF && Native.HasUser(s); }

    /// <summary>Si no hay agente en la sesión activa, lo (re)lanza. Si el agente cae, el servicio igual apaga por horario.</summary>
    void EnsureAgent()
    {
        if ((DateTime.UtcNow - lastLaunch).TotalSeconds < 4) return;
        var sid = Native.WTSGetActiveConsoleSessionId();
        if (sid == 0xFFFFFFFF || !Native.HasUser(sid)) return;
        var exe = Environment.ProcessPath!;
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        bool running = procs.Any(p => { try { return p.SessionId == sid; } catch { return false; } });
        foreach (var p in procs) p.Dispose();
        if (running) return;
        lastLaunch = DateTime.UtcNow;
        Log.W("Lanzando agente en sesión " + sid + ": " + Native.LaunchInSession(sid, $"\"{exe}\" --agent"));
    }

    // ---------- Apagado ----------
    void DoShutdown()
    {
        Log.W("APAGADO por horario: deshabilitando red y apagando");
        DisableNet();
        try { Process.Start(new ProcessStartInfo("shutdown.exe", "/s /f /t 0") { CreateNoWindow = true, UseShellExecute = false }); }
        catch (Exception e) { Log.W("shutdown falló: " + e.Message); }
        deadline = Clock.Now.AddSeconds(30); // reintento si no apagó
    }
    static void RunPs(string cmd, int ms)
    {
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + cmd + "\"") { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(ms);
    }
    static void DisableNet()
    {
        try { RunPs("$a=Get-NetAdapter|Where-Object Status -ne 'Disabled'; $a.Name|Set-Content -Path '" + Paths.NetFlag + "'; $a|Disable-NetAdapter -Confirm:$false", 8000); } catch { }
    }
    /// <summary>Al iniciar, reactiva los adaptadores que el servicio desactivó antes de apagar.</summary>
    static void RestoreNet()
    {
        try { if (File.Exists(Paths.NetFlag)) RunPs("Get-Content '" + Paths.NetFlag + "'|ForEach-Object{Enable-NetAdapter -Name $_ -Confirm:$false}; Remove-Item '" + Paths.NetFlag + "'", 25000); } catch { }
    }

    // ---------- Comandos (ICommands) ----------
    public void ApagarAhora() { Log.W("Comando ApagarAhora"); DoShutdown(); }
    public void AgregarTiempoExtra(int min)
    {
        lock (L)
        {
            cfg.ExtraMinutes = Math.Clamp(min, 0, 1440); cfg.ExtraDate = Clock.Now.ToString("yyyy-MM-dd");
            Store.Save(cfg); Log.W($"Tiempo extra de hoy: {cfg.ExtraMinutes} min");
            Broadcast("!CFG");
        }
        RPush();
    }
    public object ObtenerEstado()
    {
        lock (L) return new { bloqueado = lockActive, permitido = Scheduler.IsAllowed(cfg, Clock.Now), ahora = Clock.Now, mantenimiento = cfg.MaintUntil != null };
    }
    public void CambiarHorario(Cfg n)
    {
        lock (L)
        {
            if (n.Days.Length != 7) throw new Exception("Horario inválido");
            if (!Scheduler.ParseShortcut(n.Shortcut, out _, out _)) throw new Exception("Atajo inválido");
            cfg.Days = n.Days; cfg.LocalChangedTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            cfg.GraceSeconds = Math.Clamp(n.GraceSeconds, 5, 600);
            cfg.Shortcut = n.Shortcut;
            cfg.MaintMinutes = Math.Clamp(n.MaintMinutes, 0, 1440);
            if (n.MaintOn && cfg.MaintUntil == null) cfg.MaintUntil = cfg.MaintMinutes == 0 ? DateTime.MaxValue : Clock.Now.AddMinutes(cfg.MaintMinutes);
            else if (!n.MaintOn) cfg.MaintUntil = null;
            // Restricciones editadas desde el panel de la PC
            cfg.Bloqueos = (n.Bloqueos ?? new()).Where(b => !Restrict.EsProtegida(b.Ruta)).ToList();
            foreach (var b in cfg.Bloqueos) if (string.IsNullOrEmpty(b.Id)) b.Id = Tok(6);
            cfg.SinInternet = n.SinInternet;
            restrictDirty = true;
            Store.Save(cfg);
            Log.W("Configuración guardada (mantenimiento: " + (cfg.MaintUntil != null) + ")");
            Broadcast(StateMsg()); RPush();
        }
    }

    // ---------- Control remoto ----------
    Remote? remote;
    static string Tok(int n) => Convert.ToHexString(RandomNumberGenerator.GetBytes(n)).ToLowerInvariant();

    void StartRemote()
    {
        try
        {
            remote?.Stop(); remote = null;
            if (cfg.RemoteEnabled && !string.IsNullOrEmpty(cfg.RemoteUrl) && !string.IsNullOrEmpty(cfg.PcId))
            {
                remote = new Remote(this, cfg.RemoteUrl!, cfg.PcId!, cfg.PcToken ?? "", () => cfg.PhoneKey ?? "");
                remote.Start();
                Log.W("Canal remoto iniciado: " + cfg.RemoteUrl);
            }
        }
        catch (Exception e) { Log.W("Remote start: " + e.Message); }
    }

    void RPush() { try { remote?.PushNow(); } catch { } }   // empuja el estado al celular al instante
    public int RemoteExtraCap => cfg.RemoteExtraCapMinutes;
    public bool RemoteActivo => cfg.RemoteEnabled;
    public string? RemoteUrlActual => cfg.RemoteUrl;
    public string? PhoneKeyActual => cfg.PhoneKey;
    public string? PcIdActual => cfg.PcId;

    /// <summary>Activa el remoto con la URL del servidor, generando credenciales si faltan.</summary>
    public (string url, string id, string pcToken, string phoneKey) ActivarRemoto(string url)
    {
        lock (L)
        {
            cfg.RemoteEnabled = true; cfg.RemoteUrl = url.TrimEnd('/');
            cfg.PcId ??= Tok(8); cfg.PcToken ??= Tok(24); cfg.PhoneKey ??= Tok(24);
            Store.Save(cfg);
        }
        StartRemote();
        return (cfg.RemoteUrl!, cfg.PcId!, cfg.PcToken!, cfg.PhoneKey!);
    }
    public void DesactivarRemoto()
    {
        lock (L) { cfg.RemoteEnabled = false; Store.Save(cfg); }
        try { remote?.Stop(); remote = null; } catch { }
        Log.W("Canal remoto desactivado");
    }
    /// <summary>Regenera la PhoneKey: el celular vinculado deja de tener acceso.</summary>
    public void RevocarCelular()
    {
        lock (L) { cfg.PhoneKey = Tok(24); Store.Save(cfg); }
        Log.W("Celular revocado");
        StartRemote();
    }
    /// <summary>Pide al servidor un código de 6 dígitos de un solo uso para vincular (vía el canal WS).</summary>
    public string? PedirCodigoVinculacion() => remote?.PedirCodigo();

    public object RemoteStatus()
    {
        lock (L)
        {
            var now = Clock.Now;
            var d = cfg.Days[((int)now.DayOfWeek + 6) % 7];
            bool restr = d.Restricted && Scheduler.TryT(d.Start, out _) && Scheduler.TryT(d.End, out _);
            int extraHoy = cfg.ExtraDate == now.ToString("yyyy-MM-dd") ? cfg.ExtraMinutes : 0;
            string? ini = null, fin = null;
            if (restr) { Scheduler.TryT(d.Start, out var s); Scheduler.TryT(d.End, out var e); ini = s.ToString(@"hh\:mm"); fin = (e + TimeSpan.FromMinutes(extraHoy)).ToString(@"hh\:mm"); }
            return new
            {
                bloqueado = lockActive,
                permitido = Scheduler.IsAllowed(cfg, now),
                mantenimiento = cfg.MaintUntil != null,
                ahora = now.ToString("yyyy-MM-dd HH:mm:ss"),
                restriHoy = restr,
                iniHoy = ini,
                finHoy = fin,
                extraHoy,
                gracia = cfg.GraceSeconds,
                dias = cfg.Days,
                bloqueos = cfg.Bloqueos,
                sinInternet = cfg.SinInternet
            };
        }
    }

    /// <summary>Cambia la ventana de gracia. Si ya hay un bloqueo en curso, la cuenta se reinicia con el valor nuevo.</summary>
    public void RemoteSetGrace(int seg)
    {
        lock (L)
        {
            cfg.GraceSeconds = Math.Clamp(seg, 5, 600); Store.Save(cfg);
            if (lockActive) deadline = Clock.Now.AddSeconds(cfg.GraceSeconds);
            Log.W($"Ventana de gracia: {cfg.GraceSeconds} s (desde el celular)");
            Broadcast("!CFG");
        }
        RPush();
    }

    /// <summary>Cambia solo los 7 días desde el celular (no toca atajo, gracia ni contraseña).</summary>
    public void RemoteSetDays(DayCfg[] dias)
    {
        if (dias.Length != 7) throw new Exception("Horario inválido");
        lock (L)
        {
            foreach (var d in dias)
                if (d.Restricted && (!Scheduler.TryT(d.Start, out var s) || !Scheduler.TryT(d.End, out var e) || e <= s))
                    throw new Exception("Horario de un día inválido");
            cfg.Days = dias; Store.Save(cfg);
            Log.W("Horario cambiado desde el celular");
            Broadcast(StateMsg()); Broadcast("!CFG"); RPush();
        }
    }

    /// <summary>Cola offline: ts = momento (ms UTC) en que el celular pidió el cambio.</summary>
    public bool RemoteLocalNewerThan(long ts) { lock (L) return cfg.LocalChangedTs > ts; }

    // ---------- Restricciones desde el celular ----------
    void RestrictChanged() { restrictDirty = true; Store.Save(cfg); Broadcast("!CFG"); RPush(); }

    public void RemoteBloqSet(string? id, bool activo)
    {
        lock (L)
        {
            var b = cfg.Bloqueos.FirstOrDefault(x => x.Id == id);
            if (b == null) throw new Exception("Ítem inexistente");
            b.Activo = activo; Log.W($"Remoto: ítem {(activo ? "activado" : "desactivado")} -> {b.Ruta}");
            RestrictChanged();
        }
    }
    public void RemoteBloqAdd(string? ruta, string? tipo)
    {
        if (string.IsNullOrWhiteSpace(ruta)) throw new Exception("Ruta vacía");
        if (Restrict.EsProtegida(ruta)) throw new Exception("Esa ruta no se puede bloquear");
        lock (L)
        {
            var full = Path.GetFullPath(ruta).TrimEnd('\\');
            if (cfg.Bloqueos.Any(x => string.Equals(x.Ruta, full, StringComparison.OrdinalIgnoreCase))) throw new Exception("Ya está en la lista");
            cfg.Bloqueos.Add(new Bloqueo { Id = Tok(6), Ruta = full, Tipo = tipo == "carpeta" ? "carpeta" : "archivo", Activo = true });
            Log.W("Remoto: ítem agregado -> " + full);
            RestrictChanged();
        }
    }
    public void RemoteBloqDel(string? id)
    {
        lock (L)
        {
            var b = cfg.Bloqueos.FirstOrDefault(x => x.Id == id);
            if (b != null) { cfg.Bloqueos.Remove(b); Log.W("Remoto: ítem quitado -> " + b.Ruta); RestrictChanged(); }
        }
    }
    public void RemoteInternet(bool sin)
    {
        lock (L) { cfg.SinInternet = sin; Log.W("Remoto: internet " + (sin ? "bloqueado" : "libre")); RestrictChanged(); }
    }

    /// <summary>Explorador remoto: lista subcarpetas y archivos de una ruta (o los discos si viene vacía). Solo lectura de nombres.</summary>
    public object RemoteLs(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta))
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new { nombre = d.Name, ruta = d.Name, tipo = "carpeta" });
            return new { ruta = "", arriba = (string?)null, items = drives.ToArray() };
        }
        var full = Path.GetFullPath(ruta).TrimEnd('\\') + "\\";
        var dirs = new List<object>(); var files = new List<object>();
        foreach (var d in Directory.EnumerateDirectories(full))
        {
            try { var di = new DirectoryInfo(d); if ((di.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0) dirs.Add(new { nombre = di.Name, ruta = di.FullName, tipo = "carpeta" }); } catch { }
            if (dirs.Count >= 300) break;
        }
        foreach (var f in Directory.EnumerateFiles(full))
        {
            try { var fi = new FileInfo(f); if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0) files.Add(new { nombre = fi.Name, ruta = fi.FullName, tipo = "archivo" }); } catch { }
            if (files.Count >= 300) break;
        }
        var parent = Path.GetDirectoryName(full.TrimEnd('\\'));
        return new { ruta = full.TrimEnd('\\'), arriba = string.IsNullOrEmpty(parent) ? "" : parent, items = dirs.Concat(files).ToArray() };
    }

    // ---------- Named pipe (solo SYSTEM: el agente corre como SYSTEM) ----------
    void PipeLoop()
    {
        var ps = new PipeSecurity();
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        while (true)
        {
            try
            {
                var p = NamedPipeServerStreamAcl.Create("BloqueadorHorarioPipe", PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, ps);
                p.WaitForConnection();
                new Thread(() => Handle(p)) { IsBackground = true }.Start();
            }
            catch (Exception e) { Log.W("Pipe: " + e.Message); Thread.Sleep(1000); }
        }
    }

    void Handle(NamedPipeServerStream p)
    {
        var c = new Conn { W = new StreamWriter(p) { AutoFlush = true } };
        var r = new StreamReader(p);
        try
        {
            lock (L) conns.Add(c);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                string? resp;
                try { resp = Dispatch(c, line); } catch (Exception e) { resp = "ERR " + e.Message; }
                if (resp != null) c.Send(resp);
            }
        }
        catch { }
        finally { lock (L) conns.Remove(c); p.Dispose(); }
    }

    static void Need(Conn c) { if (!c.Authed) throw new Exception("No autorizado"); }
    static string D64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

    string? Dispatch(Conn c, string line)
    {
        var i = line.IndexOf(' ');
        var cmd = i < 0 ? line : line[..i];
        var arg = i < 0 ? "" : line[(i + 1)..];
        // RCODE puede bloquear hasta 8 s esperando al servidor: fuera del lock para no frenar el tick.
        if (cmd == "RCODE") { Need(c); return PedirCodigoVinculacion() ?? "ERR sin conexión con el servidor"; }
        lock (L)
        {
            switch (cmd)
            {
                case "HELLO": c.Send(StateMsg()); return null;
                case "RGET": Need(c); return JsonSerializer.Serialize(new { enabled = RemoteActivo, url = RemoteUrlActual, id = PcIdActual, key = PhoneKeyActual });
                case "RSET": { Need(c); if (string.IsNullOrWhiteSpace(arg)) return "ERR URL vacía"; var cr = ActivarRemoto(arg); return JsonSerializer.Serialize(new { cr.url, cr.id, key = cr.phoneKey }); }
                case "RUNLINK": Need(c); RevocarCelular(); return "OK";
                case "ROFF": Need(c); DesactivarRemoto(); return "OK";
                case "MODAL":
                    c.Modal = arg == "1"; c.ModalSince = DateTime.UtcNow;
                    Log.W(c.Modal ? "Atajo usado: modal de desbloqueo abierto" : "Modal de desbloqueo cerrado");
                    return "OK";
                case "AUTH":
                    if (Store.Verify(cfg, D64(arg))) { c.Authed = true; c.Hold = true; Log.W("Contraseña correcta: bloqueo suspendido, panel habilitado"); return "OK"; }
                    Log.W("Contraseña INCORRECTA"); return "FAIL";
                case "PANEL":
                    if (arg == "0") { c.Authed = false; c.Hold = false; Log.W("Panel cerrado"); }
                    return "OK";
                case "GETCFG":
                    {
                        Need(c);
                        var d = JsonSerializer.Deserialize<Cfg>(JsonSerializer.Serialize(cfg))!;
                        d.PwdHash = null; d.MaintOn = cfg.MaintUntil != null;
                        if (d.ExtraDate != Clock.Now.ToString("yyyy-MM-dd")) d.ExtraMinutes = 0;
                        return JsonSerializer.Serialize(d);
                    }
                case "SETCFG": Need(c); CambiarHorario(JsonSerializer.Deserialize<Cfg>(arg)!); return "OK";
                case "EXTRA": Need(c); AgregarTiempoExtra(int.Parse(arg)); return "OK";
                case "CHPASS":
                    {
                        Need(c);
                        var p = arg.Split('|');
                        if (!Store.Verify(cfg, D64(p[0]))) return "ERR Contraseña actual incorrecta";
                        var np = D64(p[1]);
                        if (np.Length < 4) return "ERR Mínimo 4 caracteres";
                        cfg.PwdHash = Store.Hash(np); Store.Save(cfg); Log.W("Contraseña cambiada");
                        return "OK";
                    }
                case "SHUTDOWN": Need(c); ApagarAhora(); return "OK";
                case "STATUS": Need(c); return JsonSerializer.Serialize(ObtenerEstado());
            }
        }
        return "ERR comando desconocido";
    }
}
