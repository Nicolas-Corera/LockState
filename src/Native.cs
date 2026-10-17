// P/Invoke: sesiones de Windows, lanzamiento del agente en la sesión del usuario y hooks de bajo nivel.
namespace BloqueadorHorario;

static class Native
{
    public delegate IntPtr HookProc(int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode)] static extern bool WTSQuery(IntPtr h, uint sid, int cls, out IntPtr buf, out int len);
    [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint acc, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr t, uint acc, IntPtr attr, int imp, int type, out IntPtr nt);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr t, int cls, ref uint v, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool CreateProcessAsUserW(IntPtr tok, string? app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? n);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte sc, uint fl, UIntPtr ex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    /// <summary>¿Hay un usuario con sesión iniciada en esa sesión?</summary>
    public static bool HasUser(uint sid)
    {
        if (!WTSQuery(IntPtr.Zero, sid, 5 /*WTSUserName*/, out var buf, out _)) return false;
        try { return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buf)); } finally { WTSFreeMemory(buf); }
    }

    /// <summary>
    /// Lanza el exe en la sesión indicada con el token de SYSTEM del servicio (duplicado y reasignado a esa sesión).
    /// Así el agente puede mostrar UI/hooks en el escritorio del usuario y un usuario estándar no puede matarlo.
    /// </summary>
    public static bool LaunchInSession(uint sid, string cmd)
    {
        IntPtr t = IntPtr.Zero, d = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0xF01FF, out t)) return false;
            if (!DuplicateTokenEx(t, 0xF01FF, IntPtr.Zero, 2, 1, out d)) return false;
            if (!SetTokenInformation(d, 12 /*TokenSessionId*/, ref sid, 4)) return false;
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessAsUserW(d, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out var pi)) return false;
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
            return true;
        }
        finally { if (t != IntPtr.Zero) CloseHandle(t); if (d != IntPtr.Zero) CloseHandle(d); }
    }

    /// <summary>Trae una ventana al frente (el truco de la tecla ALT evita el bloqueo de foco de Windows).</summary>
    public static void Front(IntPtr h)
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(h);
    }
}
