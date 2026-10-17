// Aplicación de restricciones (corre dentro del servicio, como SYSTEM; un usuario estándar no puede deshacerlo).
// Dos capas para los archivos/carpetas: (1) ACL NTFS Deny al grupo Usuarios -> el doble clic falla a nivel kernel;
// (2) vigilante de procesos en el tick -> cubre USB/FAT sin ACL o ejecutables copiados dentro de una carpeta bloqueada.
// Internet: una regla de firewall saliente que bloquea solo al grupo Usuarios (el servicio SYSTEM conserva su WebSocket).
namespace BloqueadorHorario;

static class Restrict
{
    const string UsersSid = "*S-1-5-32-545";        // grupo "Usuarios" (usuarios estándar)
    const string UsersSidBare = "S-1-5-32-545";
    const string FwRule = "BloqueadorHorario-SinInternet";

    // ---- Rutas que NUNCA se pueden bloquear (romperían Windows o el propio programa) ----
    public static bool EsProtegida(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return true;
        string p;
        try { p = Path.GetFullPath(ruta).TrimEnd('\\'); } catch { return true; }
        if (p.Length <= 2) return true;                               // raíz de disco ("C:")
        var pl = p.ToLowerInvariant();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant().TrimEnd('\\');
        string? inst = null;
        try { inst = Path.GetDirectoryName(Environment.ProcessPath)?.ToLowerInvariant().TrimEnd('\\'); } catch { }
        var data = Paths.Dir.ToLowerInvariant().TrimEnd('\\');
        bool Under(string b) => pl == b || pl.StartsWith(b + "\\");
        if (Under(win) || Under(data)) return true;
        if (inst != null && Under(inst)) return true;
        return false;
    }

    /// <summary>Si la ruta es un acceso directo (.lnk), devuelve su destino real (el .exe). Si no, la ruta tal cual.</summary>
    public static string Resolve(string ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta) || !ruta.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return ruta;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return ruta;
            dynamic sh = Activator.CreateInstance(t)!;
            dynamic sc = sh.CreateShortcut(ruta);
            string target = sc.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? ruta : target;
        }
        catch (Exception e) { Log.W("Resolve lnk: " + e.Message); return ruta; }
    }

    static void Icacls(string args) => Run("icacls.exe", args);
    static void Ps(string cmd) => Run("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + cmd + "\"");
    static void Run(string exe, string args)
    {
        // Sin redirigir la salida: si se redirige y no se lee, el proceso se cuelga al llenarse el buffer (~4 KB)
        // y el comando queda a medio aplicar. CreateNoWindow basta para que no se vea ninguna ventana.
        try { using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }); p?.WaitForExit(15000); }
        catch (Exception e) { Log.W("Restrict exec: " + e.Message); }
    }

    /// <summary>Quita toda restricción: revierte las ACL aplicadas (según el archivo de estado) y borra la regla de firewall.</summary>
    public static void RemoveAll()
    {
        try
        {
            if (File.Exists(Paths.RestrictState))
                foreach (var ruta in File.ReadAllLines(Paths.RestrictState))
                    if (!string.IsNullOrWhiteSpace(ruta)) Icacls($"\"{ruta}\" /remove:d {UsersSid} /Q /C");
        }
        catch (Exception e) { Log.W("Restrict RemoveAll ACL: " + e.Message); }
        try { File.Delete(Paths.RestrictState); } catch { }
        Ps($"Get-NetFirewallRule -DisplayName '{FwRule}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue");
    }

    /// <summary>Aplica el estado deseado: primero revierte lo anterior (idempotente) y luego vuelve a aplicar lo activo.</summary>
    public static void Apply(List<Bloqueo> bloqueos, bool sinInternet)
    {
        RemoveAll();
        var aplicadas = new List<string>();
        foreach (var b in bloqueos)
        {
            if (!b.Activo || EsProtegida(b.Ruta)) continue;
            string full;
            try { full = Path.GetFullPath(b.Ruta).TrimEnd('\\'); } catch { continue; }
            bool carpeta = b.Tipo == "carpeta";
            if (carpeta ? !Directory.Exists(full) : !File.Exists(full)) continue;   // ya no existe
            var perm = carpeta ? "(OI)(CI)(RX)" : "(RX)";
            Icacls($"\"{full}\" /deny \"{UsersSid}:{perm}\" /Q /C");
            aplicadas.Add(full);
        }
        try { if (aplicadas.Count > 0) File.WriteAllLines(Paths.RestrictState, aplicadas); else File.Delete(Paths.RestrictState); } catch { }

        if (sinInternet)
            Ps($"if(-not(Get-NetFirewallRule -DisplayName '{FwRule}' -ErrorAction SilentlyContinue)){{New-NetFirewallRule -DisplayName '{FwRule}' -Direction Outbound -Action Block -LocalUser 'D:(A;;CC;;;{UsersSidBare})' -ErrorAction SilentlyContinue}}");

        Log.W($"Restricciones aplicadas: {aplicadas.Count} ítem(s), internet {(sinInternet ? "bloqueado" : "libre")}");
    }

    /// <summary>Capa de respaldo: termina cualquier proceso cuya ruta caiga dentro de un ítem activo.</summary>
    public static void Watchdog(List<Bloqueo> bloqueos)
    {
        var files = new List<string>(); var dirs = new List<string>();
        foreach (var b in bloqueos)
        {
            if (!b.Activo || EsProtegida(b.Ruta)) continue;
            string f; try { f = Path.GetFullPath(b.Ruta).TrimEnd('\\').ToLowerInvariant(); } catch { continue; }
            if (b.Tipo == "carpeta") dirs.Add(f + "\\"); else files.Add(f);
        }
        if (files.Count == 0 && dirs.Count == 0) return;
        string self; try { self = (Environment.ProcessPath ?? "").ToLowerInvariant(); } catch { self = ""; }
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                var pl = path.ToLowerInvariant();
                if (pl == self) continue;
                if (files.Contains(pl) || dirs.Any(d => pl.StartsWith(d)))
                {
                    p.Kill();
                    Log.W("Restricción: proceso terminado -> " + path);
                }
            }
            catch { }   // procesos del sistema/protegidos: no accesibles, se ignoran
            finally { p.Dispose(); }
        }
    }
}
