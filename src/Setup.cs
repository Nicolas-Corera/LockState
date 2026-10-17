// Instalador integrado: doble clic al .exe -> ventana para instalar / actualizar / desinstalar. Sin comandos ni scripts.
// Pide permisos de administrador solo (UAC) y hace lo mismo que hacía install.ps1.
using System.ServiceProcess;
namespace BloqueadorHorario;

static class Setup
{
    const string Svc = "BloqueadorHorario";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BloqueadorHorario");
    static string Dst => Path.Combine(Dir, "BloqueadorHorario.exe");
    public static bool PendingDelete;

    public static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool Installed() { try { using var sc = new ServiceController(Svc); _ = sc.Status; return true; } catch { return false; } }

    public static void Run()
    {
        if (!IsAdmin())
        {
            // Se vuelve a abrir pidiendo permisos de administrador (aviso de Windows).
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" }); } catch { }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
        if (PendingDelete)   // la carpeta del programa se borra cuando este proceso ya cerró
            try { Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{Dir}\"") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }

    static int Exec(string file, string args, int ms = 30000)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
        if (p == null) return -1;
        return p.WaitForExit(ms) ? p.ExitCode : -2;
    }

    /// <summary>Frena guardián, servicio y agentes (en ese orden, si no el servicio relanza al agente).</summary>
    static void StopAll()
    {
        Exec("schtasks.exe", "/Delete /TN BloqueadorHorarioGuardian /F");
        if (Installed())
        {
            Exec("sc.exe", "stop " + Svc, 20000);
            for (int i = 0; i < 30; i++)
            {
                try { using var sc = new ServiceController(Svc); if (sc.Status == ServiceControllerStatus.Stopped) break; } catch { break; }
                Thread.Sleep(500);
            }
        }
        foreach (var p in Process.GetProcessesByName("BloqueadorHorario"))
        {
            if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(4000); } catch { } }
            p.Dispose();
        }
    }

    public static void Install(string pwd, bool keepPwd)
    {
        StopAll();
        Directory.CreateDirectory(Dir); Directory.CreateDirectory(Paths.Dir);
        if (!string.Equals(Environment.ProcessPath, Dst, StringComparison.OrdinalIgnoreCase)) File.Copy(Environment.ProcessPath!, Dst, true);
        // Permisos: solo SYSTEM y Administradores (por SID, vale en cualquier idioma de Windows).
        foreach (var d in new[] { Dir, Paths.Dir }) Exec("icacls.exe", $"\"{d}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F");
        if (!keepPwd) { var c = Store.Load(); c.PwdHash = Store.Hash(pwd); Store.Save(c); }
        if (!Installed())
        {
            var r = Exec("sc.exe", $"create {Svc} binPath= \"\\\"{Dst}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"Servicio de Control de Horario\"");
            if (r != 0) throw new Exception("No se pudo crear el servicio (código " + r + ").");
        }
        Exec("sc.exe", $"description {Svc} \"Control de horario de uso del equipo\"");
        Exec("sc.exe", $"failure {Svc} reset= 86400 actions= restart/1000/restart/1000/restart/1000");
        Exec("sc.exe", $"failureflag {Svc} 1");
        Exec("sc.exe", $"sdset {Svc} \"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)\"");
        // Guardián: cada minuto reinicia el servicio si alguien lo detuvo.
        Exec("schtasks.exe", "/Create /TN BloqueadorHorarioGuardian /SC MINUTE /MO 1 /RU SYSTEM /TR \"sc.exe start BloqueadorHorario\" /F");
        Exec("sc.exe", "start " + Svc);
        Log.W("Instalado/actualizado con el instalador integrado");
    }

    public static void Uninstall()
    {
        StopAll();
        Exec("sc.exe", "delete " + Svc);
        try { if (File.Exists(Paths.NetFlag)) Exec("powershell.exe", "-NoProfile -Command \"Get-Content '" + Paths.NetFlag + "'|ForEach-Object{Enable-NetAdapter -Name $_ -Confirm:$false}\"", 25000); } catch { }
        try { Directory.Delete(Paths.Dir, true); } catch { }
        PendingDelete = true;
    }
}

class SetupForm : Form
{
    readonly bool fresh;
    readonly TextBox p1; readonly TextBox? p2;
    readonly Button b1; readonly Button? b2;
    readonly Label status;

    public SetupForm()
    {
        bool installed = Setup.Installed();
        fresh = !installed || Store.Load().PwdHash == null;
        Text = "Control de Horario"; ClientSize = new Size(460, 370); BackColor = Ui.Bg; ForeColor = Ui.Fg; Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;

        Ui.L(this, "Control de Horario", 0, 26, 460, 20, true).TextAlign = ContentAlignment.MiddleCenter;
        Ui.L(this, installed ? "Ya está instalado en este equipo." : "Instalación en este equipo", 0, 74, 460, 11, false, Ui.Mut).TextAlign = ContentAlignment.MiddleCenter;
        Ui.L(this, fresh ? "Elegí la contraseña del programa" : "Contraseña del programa", 40, 120);
        p1 = Ui.T(this, 40, 144, 380, true);
        if (fresh)
        {
            Ui.L(this, "Repetila", 40, 184); p2 = Ui.T(this, 40, 208, 380, true);
            b1 = Ui.B(this, "Instalar", 40, 262, 380, 44, true);
            b1.Click += (_, _) => Go(false);
        }
        else
        {
            b1 = Ui.B(this, "Actualizar", 40, 200, 180, 44, true);
            b2 = Ui.B(this, "Desinstalar", 240, 200, 180, 44);
            b1.Click += (_, _) => Go(false);
            b2.Click += (_, _) => Go(true);
        }
        status = Ui.L(this, "", 0, 322, 460, 10, false, Ui.Mut); status.TextAlign = ContentAlignment.MiddleCenter;
        AcceptButton = b1;
    }

    void Busy(bool on, string msg)
    {
        b1.Enabled = !on; if (b2 != null) b2.Enabled = !on; p1.Enabled = !on; if (p2 != null) p2.Enabled = !on;
        status.Text = msg;
    }

    void Go(bool uninstall)
    {
        var pwd = p1.Text;
        string? err = null;
        if (fresh) { if (pwd.Length < 4) err = "La contraseña debe tener al menos 4 caracteres."; else if (pwd != p2!.Text) err = "Las contraseñas no coinciden."; }
        else if (!Store.Verify(Store.Load(), pwd)) err = "Contraseña incorrecta.";
        if (err != null) { Dlg.Show(err); return; }

        Busy(true, "Trabajando… no cierres esta ventana.");
        Task.Run(() =>
        {
            try { if (uninstall) Setup.Uninstall(); else Setup.Install(pwd, !fresh); return (string?)null; }
            catch (Exception e) { return e.Message; }
        }).ContinueWith(t =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    Busy(false, "");
                    if (t.Result != null) { Dlg.Show("Error: " + t.Result); return; }
                    Dlg.Show(uninstall ? "Desinstalado." : "Listo. Ya está funcionando. Usá Ctrl+Alt+L para abrir el panel.");
                    Close();
                }));
            }
            catch { }
        });
    }
}
