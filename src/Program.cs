using System.ServiceProcess;
namespace BloqueadorHorario;

static class Program
{
    // Un solo exe, tres roles: --service (SYSTEM), --agent (sesión del usuario), --init-password <pwd> (instalador).
    // Sin argumentos no hace nada ni muestra nada.
    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length == 0) { Setup.Run(); return 0; }   // doble clic: instalador integrado
        switch (a[0])
        {
            case "--service": ServiceBase.Run(new SvcHost()); return 0;
            case "--agent": Agent.Run(); return 0;
            case "--testui": // prueba segura: muestra solo el modal como usuario normal (sin servicio, hooks, bloqueo ni apagado)
                ApplicationConfiguration.Initialize();
                using (var f = new UnlockForm(60, _ => false)) f.ShowDialog();
                return 0;
            case "--init-password" when a.Length > 1:
                var c = Store.Load(); c.PwdHash = Store.Hash(a[1]); Store.Save(c); return 0;
        }
        return 0;
    }
}
