// Agente de sesión (corre en la sesión del usuario, sin ventana ni bandeja): hooks de teclado/mouse,
// modal de desbloqueo y panel. Habla con el servicio por named pipe.
using System.Collections.Concurrent;
namespace BloqueadorHorario;

/// <summary>Conexión persistente al servicio. Líneas "!..." son eventos push; el resto, respuestas a Req().</summary>
static class Link
{
    static NamedPipeClientStream? pipe; static StreamWriter? w;
    static readonly BlockingCollection<string> resp = new();
    static readonly object RQ = new(), CL = new();
    public static event Action<string>? Push;

    public static void Start() => new Thread(Loop) { IsBackground = true }.Start();

    static void Loop()
    {
        int fails = 0;
        while (true)
        {
            try
            {
                // Asynchronous: permite leer (hilo Loop) y escribir (hilo Req) a la vez sin bloquearse mutuamente.
                var c = new NamedPipeClientStream(".", "BloqueadorHorarioPipe", PipeDirection.InOut, PipeOptions.Asynchronous);
                c.Connect(2000);
                var wr = new StreamWriter(c) { AutoFlush = true };
                var rd = new StreamReader(c);
                lock (CL) { pipe = c; w = wr; }
                Log.W("Agente: conectado al servicio");
                while (resp.TryTake(out _)) { }
                fails = 0;
                wr.WriteLine("HELLO");
                string? l;
                while ((l = rd.ReadLine()) != null)
                {
                    if (l.Length == 0) continue;
                    if (l[0] == '!') Push?.Invoke(l); else resp.Add(l);
                }
            }
            catch
            {
                // Guardián: si el servicio no responde, intentamos iniciarlo (somos SYSTEM).
                if (++fails == 5)
                    try { Process.Start(new ProcessStartInfo("sc.exe", "start BloqueadorHorario") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
            }
            lock (CL) { pipe = null; w = null; }
            Thread.Sleep(1000);
        }
    }

    public static string? Req(string line, int timeoutMs = 6000)
    {
        lock (RQ)
        {
            StreamWriter? wr; lock (CL) wr = w;
            var verb = line.Split(' ')[0];   // nunca registrar el argumento (puede traer la contraseña)
            if (wr == null) { Log.W("Link.Req " + verb + ": sin conexión"); return null; }
            try
            {
                while (resp.TryTake(out _)) { }
                wr.WriteLine(line);
                bool got = resp.TryTake(out var r, timeoutMs);
                Log.W("Link.Req " + verb + " -> " + (got ? r : "TIMEOUT"));
                return got ? r : null;
            }
            catch (Exception e) { Log.W("Link.Req " + verb + " ERROR: " + e.Message); return null; }
        }
    }
}

static class Agent
{
    static volatile bool locked, modalOpen, flowActive;
    static int scMods, scVk, timeoutSec = 20;
    static bool cD, aD, sD, wD;
    static Control host = null!;
    static readonly Native.HookProc kb = KbProc, ms = MsProc; // referencias estáticas: evitan que el GC las libere

    public static void Run()
    {
        using var mtx = new Mutex(true, "Local\\BloqueadorHorarioAgent", out bool created);
        if (!created) return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.W("Agente: error fatal: " + e.ExceptionObject);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.W("Agente: excepción en UI: " + e.Exception);
        ApplicationConfiguration.Initialize();
        host =new Control(); _ = host.Handle;
        Scheduler.ParseShortcut("Ctrl+Alt+L", out scMods, out scVk);
        var h = Native.GetModuleHandle(null);
        var hk = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, kb, h, 0);
        int e1 = Marshal.GetLastWin32Error();
        var hm = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, ms, h, 0);
        Log.W($"Agente iniciado: hook teclado={hk} mouse={hm} err={e1} modulo={h}");
        Link.Push += OnPush;
        Link.Start();
        Application.Run();
        Log.W("Agente: Application.Run terminó (el proceso se cierra)");
    }

    // "!STATE <lock 0|1> <atajo> <timeoutModal>"
    static void OnPush(string line)
    {
        var p = line.Split(' ');
        if (p.Length < 4 || p[0] != "!STATE") return;
        locked = p[1] == "1";
        if (Scheduler.ParseShortcut(p[2], out var m, out var vk)) { scMods = m; scVk = vk; }
        if (int.TryParse(p[3], out var t)) timeoutSec = t;
    }

    static bool ModsMatch() => cD == ((scMods & 1) != 0) && aD == ((scMods & 2) != 0) && sD == ((scMods & 4) != 0) && wD == ((scMods & 8) != 0);

    static IntPtr KbProc(int n, IntPtr w, IntPtr l)
    {
        if (n < 0) return Native.CallNextHookEx(IntPtr.Zero, n, w, l);
        int msg = (int)w, vk = Marshal.ReadInt32(l);
        bool down = msg == 0x100 || msg == 0x104;
        switch (vk)
        {
            case 0x11: case 0xA2: case 0xA3: cD = down; break;
            case 0x12: case 0xA4: case 0xA5: aD = down; break;
            case 0x10: case 0xA0: case 0xA1: sD = down; break;
            case 0x5B: case 0x5C: wD = down; break;
        }
        if (down && !flowActive && vk == scVk && ModsMatch())
        {
            Log.W("Agente: atajo detectado");
            flowActive = true;
            var t = new Thread(OpenFlow) { IsBackground = true };
            t.SetApartmentState(ApartmentState.STA); // el modal corre en su propio hilo, con su propio bucle de mensajes
            t.Start();
            return (IntPtr)1;
        }
        // Se descartan las pulsaciones; los "key up" pasan siempre para no dejar teclas "pegadas".
        if (locked && !modalOpen && down) return (IntPtr)1;
        return Native.CallNextHookEx(IntPtr.Zero, n, w, l);
    }

    static IntPtr MsProc(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0 && locked && !modalOpen) return (IntPtr)1;
        return Native.CallNextHookEx(IntPtr.Zero, n, w, l);
    }

    /// <summary>Atajo -> modal de contraseña -> (si es correcta) panel de configuración.</summary>
    static void OpenFlow()
    {
        modalOpen = true;        // flowActive ya lo puso KbProc antes de lanzar el hilo
        bool ok = false;
        Log.W("Agente: OpenFlow, mostrando modal");
        try
        {
            // El aviso al servicio (pausar el apagado) se manda aparte: la ventana NUNCA debe esperarlo.
            Notify("MODAL 1");
            using var f = new UnlockForm(timeoutSec, pwd => Link.Req("AUTH " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pwd))) == "OK");
            f.ShowDialog();
            ok = f.Success;
            Log.W("Agente: modal cerrado, ok=" + ok);
            if (ok) { using var pf = new PanelForm(); pf.ShowDialog(); }
        }
        catch (Exception e) { Log.W("Agente: error en flujo: " + e); }
        finally
        {
            Notify("MODAL 0");
            if (ok) Notify("PANEL 0");
            modalOpen = false; flowActive = false;   // el atajo SIEMPRE vuelve a funcionar
        }
    }

    /// <summary>Notifica al servicio sin bloquear la interfaz (fire-and-forget).</summary>
    static void Notify(string msg) => Task.Run(() => { try { Link.Req(msg); } catch { } });
}
