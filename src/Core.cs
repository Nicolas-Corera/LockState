// Núcleo compartido: rutas, log, configuración cifrada, reloj confiable, scheduler y capa de comandos.
global using System.Diagnostics;
global using System.IO.Pipes;
global using System.Runtime.InteropServices;
global using System.Security.AccessControl;
global using System.Security.Cryptography;
global using System.Security.Principal;
global using System.Text;
global using System.Text.Json;
using System.Net;
using System.Net.Sockets;
namespace BloqueadorHorario;

static class Paths
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BloqueadorHorario");
    public static string Config => Path.Combine(Dir, "config.dat");
    public static string Log => Path.Combine(Dir, "log.txt");
    public static string NetFlag => Path.Combine(Dir, "net.txt");
}

/// <summary>Log en ProgramData (el instalador deja el directorio accesible solo a SYSTEM/Administradores).</summary>
static class Log
{
    static readonly object L = new();
    public static void W(string m)
    {
        try
        {
            lock (L)
            {
                Directory.CreateDirectory(Paths.Dir);
                var f = Paths.Log;
                if (File.Exists(f) && new FileInfo(f).Length > 1_000_000) File.Move(f, f + ".old", true);
                using var fs = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var b = Encoding.UTF8.GetBytes($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}{Environment.NewLine}");
                fs.Write(b, 0, b.Length);
            }
        }
        catch { }
    }
}

class DayCfg { public bool Restricted { get; set; } public string? Start { get; set; } public string? End { get; set; } }

class Cfg
{
    public DayCfg[] Days { get; set; } = Enumerable.Range(0, 7).Select(_ => new DayCfg()).ToArray(); // 0 = lunes
    public int ExtraMinutes { get; set; }
    public string? ExtraDate { get; set; }            // yyyy-MM-dd; el extra solo vale ese día
    public int GraceSeconds { get; set; } = 30;
    public string Shortcut { get; set; } = "Ctrl+Alt+L";
    public int MaintMinutes { get; set; } = 30;       // 0 = hasta que se desactive
    public DateTime? MaintUntil { get; set; }
    public bool MaintOn { get; set; }                 // solo para el panel
    public int ModalTimeoutSeconds { get; set; } = 20;
    public string? PwdHash { get; set; }              // PBKDF2; nunca sale del servicio
    // ---- Control remoto (opcional) ----
    public bool RemoteEnabled { get; set; }
    public string? RemoteUrl { get; set; }            // base del servidor, ej: https://xxx.workers.dev
    public string? PcId { get; set; }                 // id de esta PC (público)
    public string? PcToken { get; set; }              // auth de la PC contra el servidor
    public string? PhoneKey { get; set; }             // auth del celular; cambiarlo revoca el celu
    public int RemoteExtraCapMinutes { get; set; } = 240; // tope de extra por comando remoto
}

/// <summary>Config cifrada con DPAPI (máquina). Contraseña solo como hash PBKDF2-SHA256.</summary>
static class Store
{
    static readonly object L = new();
    public static Cfg Load()
    {
        lock (L)
        {
            try
            {
                if (!File.Exists(Paths.Config)) return new Cfg();
                var raw = ProtectedData.Unprotect(File.ReadAllBytes(Paths.Config), null, DataProtectionScope.LocalMachine);
                return JsonSerializer.Deserialize<Cfg>(raw) ?? new Cfg();
            }
            catch (Exception e) { Log.W("Config ilegible: " + e.Message); return new Cfg(); }
        }
    }
    public static void Save(Cfg c)
    {
        lock (L)
        {
            Directory.CreateDirectory(Paths.Dir);
            var enc = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(c), null, DataProtectionScope.LocalMachine);
            var tmp = Paths.Config + ".tmp";
            File.WriteAllBytes(tmp, enc);
            File.Move(tmp, Paths.Config, true);
        }
    }
    public static string Hash(string pwd)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var h = Rfc2898DeriveBytes.Pbkdf2(pwd, salt, 200_000, HashAlgorithmName.SHA256, 32);
        return $"200000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(h)}";
    }
    public static bool Verify(Cfg c, string pwd)
    {
        try
        {
            var p = (c.PwdHash ?? "").Split('.');
            if (p.Length != 3) return false;
            var h = Rfc2898DeriveBytes.Pbkdf2(pwd, Convert.FromBase64String(p[1]), int.Parse(p[0]), HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(h, Convert.FromBase64String(p[2]));
        }
        catch { return false; }
    }
}

/// <summary>Hora de internet (SNTP). Devuelve null si no hay red.</summary>
static class Ntp
{
    public static DateTime? Get()
    {
        try
        {
            using var u = new UdpClient();
            u.Client.ReceiveTimeout = 3000;
            u.Connect("pool.ntp.org", 123);
            var b = new byte[48]; b[0] = 0x1B;
            u.Send(b, 48);
            IPEndPoint? ep = null;
            var r = u.Receive(ref ep);
            if (r.Length < 48) return null;
            ulong s = ((ulong)r[40] << 24) | ((ulong)r[41] << 16) | ((ulong)r[42] << 8) | r[43];
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s);
        }
        catch { return null; }
    }
}

/// <summary>
/// Reloj anti-manipulación: hora base (NTP o reloj del sistema al iniciar) + cronómetro monótono.
/// Si el reloj de Windows se atrasa más de 90 s respecto al confiable, se ignora y se intenta resincronizar por NTP.
/// Si se adelanta (suspensión o cambio) se acepta: adelantar solo puede bloquear antes, nunca dar más tiempo.
/// </summary>
static class Clock
{
    static readonly object L = new();
    static DateTime baseUtc; static readonly Stopwatch sw = new();
    static DateTime nextNtp; static volatile bool busy;
    public static void Init()
    {
        var n = Ntp.Get();
        baseUtc = n ?? DateTime.UtcNow; sw.Restart();
        nextNtp = DateTime.UtcNow.AddMinutes(30);
        Log.W(n != null ? "Reloj sincronizado con NTP" : "Reloj iniciado con hora local (sin NTP)");
    }
    public static DateTime UtcNow { get { lock (L) return baseUtc + sw.Elapsed; } }
    public static DateTime Now => UtcNow.ToLocalTime();
    public static void Check()
    {
        var diff = DateTime.UtcNow - UtcNow;
        if (diff > TimeSpan.FromSeconds(90)) { lock (L) { baseUtc = DateTime.UtcNow; sw.Restart(); } Log.W("Reloj adelantado/suspensión: se acepta hora del sistema"); return; }
        bool tamper = diff < TimeSpan.FromSeconds(-90);
        if (busy || (!tamper && DateTime.UtcNow < nextNtp)) return;
        busy = true;
        Task.Run(() =>
        {
            try
            {
                var n = Ntp.Get();
                if (n != null) { lock (L) { baseUtc = n.Value; sw.Restart(); } if (tamper) Log.W("Reloj atrasado detectado: corregido con NTP"); }
                else if (tamper) Log.W("Reloj atrasado detectado: se mantiene hora confiable interna");
                nextNtp = DateTime.UtcNow.AddMinutes(tamper ? 1 : 30);
            }
            finally { busy = false; }
        });
    }
}

static class Scheduler
{
    public static bool TryT(string? s, out TimeSpan t) { t = default; return s != null && TimeSpan.TryParseExact(s, @"hh\:mm", null, out t); }
    /// <summary>true = la PC puede usarse. Día sin tildar o sin configurar = sin restricción.</summary>
    public static bool IsAllowed(Cfg c, DateTime now)
    {
        var d = c.Days[((int)now.DayOfWeek + 6) % 7];
        if (!d.Restricted || !TryT(d.Start, out var s) || !TryT(d.End, out var e)) return true;
        if (c.ExtraDate == now.ToString("yyyy-MM-dd")) e += TimeSpan.FromMinutes(c.ExtraMinutes);
        var t = now.TimeOfDay;
        return t >= s && t < e;
    }
    /// <summary>"Ctrl+Alt+L" -> mods (1 ctrl, 2 alt, 4 shift, 8 win) + código de tecla virtual.</summary>
    public static bool ParseShortcut(string s, out int mods, out int vk)
    {
        mods = 0; vk = 0;
        foreach (var raw in s.Split('+'))
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl": mods |= 1; break;
                case "alt": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (Enum.TryParse<Keys>(p, true, out var k)) vk = (int)k; else return false;
                    break;
            }
        }
        return vk != 0 && mods != 0;
    }
}

// ---- Capa de comandos: la usan el panel local (vía pipe) y en el futuro el módulo remoto ----
interface ICommands
{
    void ApagarAhora();
    void AgregarTiempoExtra(int minutos);   // fija el extra de HOY (no toca el horario semanal)
    object ObtenerEstado();
    void CambiarHorario(Cfg nuevo);
}

/// <summary>Punto de extensión (sin implementación). Ver PROJECT_CONTEXT.md, sección control remoto.</summary>
interface IRemoteChannel
{
    Task StartAsync(ICommands commands, CancellationToken ct);
    Task StopAsync();
}
