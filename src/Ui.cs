// Las dos únicas pantallas: modal de desbloqueo y panel de configuración (tema oscuro, en español).
// No se cargan valores de ejemplo: horarios arrancan en "--:--" y el tiempo extra en 0.
using System.Drawing.Drawing2D;
namespace BloqueadorHorario;

static class Ui
{
    public static readonly Color Bg = Color.FromArgb(20, 22, 27), Card = Color.FromArgb(27, 30, 37), Fld = Color.FromArgb(34, 37, 45),
        Fg = Color.FromArgb(232, 234, 237), Mut = Color.FromArgb(160, 165, 175), Blue = Color.FromArgb(59, 125, 233),
        Line = Color.FromArgb(75, 80, 92), Red = Color.FromArgb(240, 86, 86);

    public static Label L(Control p, string t, int x, int y, int w = 0, float sz = 10, bool b = false, Color? c = null)
    {
        var l = new Label { Text = t, Left = x, Top = y, AutoSize = w == 0, ForeColor = c ?? Fg, BackColor = Color.Transparent, Font = new Font("Segoe UI", sz, b ? FontStyle.Bold : FontStyle.Regular) };
        if (w > 0) l.Width = w;
        p.Controls.Add(l); return l;
    }
    public static Button B(Control p, string t, int x, int y, int w, int h, bool primary = false)
    {
        var b = new Button { Text = t, Left = x, Top = y, Width = w, Height = h, FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Card, ForeColor = Fg, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? Blue : Line;
        p.Controls.Add(b); return b;
    }
    public static Panel P(Control p, int x, int y, int w, int h)
    {
        var pn = new Panel { Left = x, Top = y, Width = w, Height = h, BackColor = Card }; p.Controls.Add(pn); return pn;
    }
    public static NumericUpDown N(Control p, int x, int y, int w, int max)
    {
        var n = new NumericUpDown { Left = x, Top = y, Width = w, Minimum = 0, Maximum = max, BackColor = Fld, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle };
        p.Controls.Add(n); return n;
    }
    public static TextBox T(Control p, int x, int y, int w, bool pwd = false)
    {
        var t = new TextBox { Left = x, Top = y, Width = w, BackColor = Fld, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = pwd };
        p.Controls.Add(t); return t;
    }
    public static ComboBox C(Control p, int x, int y, int w)
    {
        var c = new ComboBox { Left = x, Top = y, Width = w, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Fld, ForeColor = Fg };
        c.Items.Add("--:--");
        for (int m = 0; m < 1440; m += 15) c.Items.Add($"{m / 60:00}:{m % 60:00}");
        c.SelectedIndex = 0; p.Controls.Add(c); return c;
    }
}

/// <summary>Modal de desbloqueo: fondo oscuro semitransparente a pantalla completa + tarjeta centrada. Siempre encima.</summary>
class UnlockForm : Form
{
    public bool Success;
    readonly Func<string, bool> check;
    readonly Form back;
    readonly TextBox tb;
    readonly Label err;
    readonly int timeout;
    int left = 3;
    DateTime last = DateTime.UtcNow;
    Button ok = null!;
    bool busy;

    public UnlockForm(int timeoutSec, Func<string, bool> check)
    {
        this.check = check; timeout = Math.Max(5, timeoutSec);
        Log.W("UnlockForm: ctor inicio");
        back = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = SystemInformation.VirtualScreen, BackColor = Color.Black, Opacity = 0.82, TopMost = true, ShowInTaskbar = false };
        back.Show();
        Log.W("UnlockForm: overlay mostrado");

        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = false;
        ClientSize = new Size(460, 420); BackColor = Ui.Card; ForeColor = Ui.Fg; KeyPreview = true;

        var icon = new Panel { Left = 190, Top = 28, Width = 80, Height = 92, BackColor = Ui.Card };
        icon.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.White, 9); using var wb = new SolidBrush(Color.White); using var kb = new SolidBrush(Ui.Card);
            g.DrawArc(pen, 20, 6, 40, 44, 180, 180); g.DrawLine(pen, 20, 28, 20, 42); g.DrawLine(pen, 60, 28, 60, 42);
            g.FillRectangle(wb, 6, 40, 68, 48);
            g.FillEllipse(kb, 34, 55, 12, 12); g.FillRectangle(kb, 37, 62, 6, 14);
        };
        Controls.Add(icon);
        Ui.L(this, "Equipo bloqueado", 0, 135, 460, 20, true).TextAlign = ContentAlignment.MiddleCenter;
        Ui.L(this, "Ingresá la contraseña para desbloquear", 0, 182, 460, 11, false, Ui.Mut).TextAlign = ContentAlignment.MiddleCenter;

        var box = new Panel { Left = 40, Top = 225, Width = 380, Height = 46, BackColor = Ui.Fld }; Controls.Add(box);
        tb = new TextBox { Left = 12, Top = 11, Width = 310, BorderStyle = BorderStyle.None, BackColor = Ui.Fld, ForeColor = Ui.Fg, UseSystemPasswordChar = true, Font = new Font("Segoe UI", 12) };
        box.Controls.Add(tb);
        var eye = new Button { Left = 334, Top = 6, Width = 36, Height = 34, FlatStyle = FlatStyle.Flat, Text = "👁", ForeColor = Ui.Mut, BackColor = Ui.Fld, Font = new Font("Segoe UI Emoji", 11), Cursor = Cursors.Hand, TabStop = false };
        eye.FlatAppearance.BorderSize = 0;
        eye.Click += (_, _) => { tb.UseSystemPasswordChar = !tb.UseSystemPasswordChar; tb.Focus(); };
        box.Controls.Add(eye);

        ok = Ui.B(this, "Desbloquear", 40, 285, 380, 46, true); ok.Font = new Font("Segoe UI", 11);
        ok.Click += (_, _) => Try();
        AcceptButton = ok;
        err = Ui.L(this, "", 0, 348, 460, 10, false, Ui.Red); err.TextAlign = ContentAlignment.MiddleCenter; err.Visible = false;

        tb.TextChanged += (_, _) => last = DateTime.UtcNow;
        MouseMove += (_, _) => last = DateTime.UtcNow;
        KeyDown += (_, e) => { last = DateTime.UtcNow; if (e.KeyCode == Keys.Escape) Close(); };
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += (_, _) => { if (!busy && (DateTime.UtcNow - last).TotalSeconds >= timeout) Close(); };
        t.Start();
        Shown += (_, _) =>
        {
            WindowState = FormWindowState.Normal;
            back.TopMost = true; TopMost = true;
            back.BringToFront(); BringToFront();
            Native.Front(back.Handle); Native.Front(Handle);
            Activate(); tb.Focus();
            Log.W("Modal visible en pantalla");
        };
        FormClosed += (_, _) => { t.Dispose(); back.Close(); back.Dispose(); };
        Log.W("UnlockForm: ctor fin");
    }

    void Try()
    {
        if (busy || tb.Text.Length == 0) return;
        busy = true; last = DateTime.UtcNow;
        var pwd = tb.Text;
        ok.Enabled = false; ok.Text = "Verificando…"; err.Visible = false;
        // La verificación habla con el servicio: va en segundo plano para que la ventana NUNCA se congele.
        Task.Run(() => { try { return check(pwd); } catch { return false; } })
            .ContinueWith(t =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        busy = false; last = DateTime.UtcNow;
                        ok.Enabled = true; ok.Text = "Desbloquear";
                        if (t.Result) { Success = true; Close(); return; }
                        left--; tb.Clear();
                        err.Text = $"Contraseña incorrecta. Intentos restantes: {left}"; err.Visible = true;
                        if (left <= 0) Close();
                    }));
                }
                catch { }
            });
    }
}

/// <summary>Captura de una combinación de teclas para el atajo.</summary>
class KeyCapture : Form
{
    public string Result = "";
    public KeyCapture()
    {
        Text = "Cambiar atajo"; ClientSize = new Size(420, 120); StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; ForeColor = Ui.Fg;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; KeyPreview = true; TopMost = true;
        var l = Ui.L(this, "Presioná la nueva combinación (Ctrl/Alt/Shift/Win + tecla)", 15, 20, 390, 10);
        var r = Ui.L(this, "", 15, 60, 390, 14, true, Ui.Blue);
        KeyDown += (_, e) =>
        {
            e.SuppressKeyPress = true; e.Handled = true;
            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl"); if (e.Alt) parts.Add("Alt"); if (e.Shift) parts.Add("Shift");
            if (e.KeyCode is Keys.LWin or Keys.RWin) parts.Add("Win");
            bool mod = e.KeyCode is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin;
            if (!mod && parts.Count > 0) { parts.Add(e.KeyCode.ToString()); Result = string.Join("+", parts); DialogResult = DialogResult.OK; return; }
            r.Text = string.Join("+", parts);
        };
    }
}

class PwdChangeForm : Form
{
    public string Old = "", New = "";
    public PwdChangeForm()
    {
        Text = "Cambiar contraseña"; ClientSize = new Size(360, 230); StartPosition = FormStartPosition.CenterParent; BackColor = Ui.Bg; ForeColor = Ui.Fg;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true;
        Ui.L(this, "Contraseña actual", 20, 15); var a = Ui.T(this, 20, 38, 320, true);
        Ui.L(this, "Nueva contraseña (mínimo 4 caracteres)", 20, 70); var b = Ui.T(this, 20, 93, 320, true);
        Ui.L(this, "Repetir nueva contraseña", 20, 125); var c = Ui.T(this, 20, 148, 320, true);
        var ok = Ui.B(this, "Aceptar", 230, 185, 110, 34, true);
        ok.Click += (_, _) =>
        {
            if (b.Text.Length < 4 || b.Text != c.Text) { Dlg.Show("La nueva contraseña no coincide o es muy corta."); return; }
            Old = a.Text; New = b.Text; DialogResult = DialogResult.OK;
        };
    }
}

class PanelForm : Form
{
    static readonly string[] Dias = { "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo" };
    readonly CheckBox[] chk = new CheckBox[7];
    readonly ComboBox[] ini = new ComboBox[7], fin = new ComboBox[7];
    readonly NumericUpDown extra, grace, maintMin;
    readonly TextBox sc;
    readonly CheckBox maint;
    Cfg d;
    bool dirty;                 // el usuario tocó algo sin guardar: no pisar con un cambio remoto
    bool loading;               // true mientras se poblan los controles: ignora los eventos de cambio
    Label? aviso;               // "cambios nuevos desde el celular"
    Button? recargar;
    Action? onCfg;

    public PanelForm()
    {
        Text = "Control de Horario"; ClientSize = new Size(900, 590); BackColor = Ui.Bg; ForeColor = Ui.Fg; Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; TopMost = true;
        var js = Link.Req("GETCFG");
        d = js != null && js.StartsWith("{") ? JsonSerializer.Deserialize<Cfg>(js)! : new Cfg();
        loading = true;

        // --- Horario semanal ---
        var a = Ui.P(this, 20, 15, 860, 340);
        Ui.L(a, "Horario semanal", 20, 8, 0, 16, true);
        Ui.L(a, "Tildado = con restricción horaria. Sin tildar = libre todo el día.", 20, 40, 0, 10, false, Ui.Mut);
        Ui.L(a, "Día", 60, 68, 0, 10, true); Ui.L(a, "Inicio", 430, 68, 0, 10, true); Ui.L(a, "Fin", 620, 68, 0, 10, true);
        for (int i = 0; i < 7; i++)
        {
            int y = 94 + 32 * i; int k = i;
            chk[i] = new CheckBox { Left = 24, Top = y + 3, Width = 22, Height = 22, BackColor = Ui.Card }; a.Controls.Add(chk[i]);
            Ui.L(a, Dias[i], 60, y + 3);
            ini[i] = Ui.C(a, 430, y, 160); fin[i] = Ui.C(a, 620, y, 160);
            var day = d.Days[i];
            chk[i].Checked = day.Restricted;
            Set(ini[i], day.Start); Set(fin[i], day.End);
            chk[i].CheckedChanged += (_, _) => { Toggle(k); MarkDirty(); };
            ini[i].SelectedIndexChanged += (_, _) => MarkDirty();
            fin[i].SelectedIndexChanged += (_, _) => MarkDirty();
            Toggle(i);
        }

        // --- Tiempo extra ---
        var b = Ui.P(this, 20, 365, 300, 150);
        Ui.L(b, "Tiempo extra", 20, 10, 0, 14, true);
        Ui.L(b, "Minutos", 20, 62); extra = Ui.N(b, 95, 58, 80, 1440); extra.Value = Math.Clamp(d.ExtraMinutes, 0, 1440);
        var ap = Ui.B(b, "Aplicar solo hoy", 20, 100, 160, 34);
        ap.Click += (_, _) => Dlg.Show(Link.Req("EXTRA " + (int)extra.Value) == "OK" ? "Tiempo extra aplicado para hoy." : "No se pudo aplicar.");

        // --- Opciones ---
        var c = Ui.P(this, 340, 365, 540, 150);
        Ui.L(c, "Opciones", 20, 10, 0, 14, true);
        Ui.L(c, "Atajo de desbloqueo", 20, 48); sc = Ui.T(c, 190, 45, 150); sc.ReadOnly = true; sc.Text = d.Shortcut;
        var ch = Ui.B(c, "Cambiar", 360, 43, 160, 30);
        ch.Click += (_, _) => { using var kc = new KeyCapture(); if (kc.ShowDialog(this) == DialogResult.OK) sc.Text = kc.Result; };
        Ui.L(c, "Ventana de gracia (segundos)", 20, 86); grace = Ui.N(c, 240, 83, 70, 600); grace.Value = Math.Clamp(d.GraceSeconds, 0, 600);
        grace.ValueChanged += (_, _) => MarkDirty();
        var cp = Ui.B(c, "Cambiar contraseña", 330, 80, 190, 30);
        cp.Click += (_, _) =>
        {
            using var f = new PwdChangeForm();
            if (f.ShowDialog(this) != DialogResult.OK) return;
            string E(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
            var r = Link.Req("CHPASS " + E(f.Old) + "|" + E(f.New));
            Dlg.Show(r == "OK" ? "Contraseña cambiada." : (r ?? "Sin respuesta del servicio").Replace("ERR ", ""));
        };
        Ui.L(c, "Modo mantenimiento", 20, 121);
        maint = new CheckBox { Left = 190, Top = 116, Width = 110, Height = 30, Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, BackColor = Ui.Fld, ForeColor = Ui.Fg, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
        maint.FlatAppearance.BorderColor = Ui.Line; maint.FlatAppearance.CheckedBackColor = Ui.Blue;
        maint.CheckedChanged += (_, _) => maint.Text = maint.Checked ? "Activado" : "Desactivado";
        maint.Checked = d.MaintOn; maint.Text = maint.Checked ? "Activado" : "Desactivado"; c.Controls.Add(maint);
        Ui.L(c, "Duración (min)", 315, 121); maintMin = Ui.N(c, 430, 118, 70, 1440); maintMin.Value = Math.Clamp(d.MaintMinutes, 0, 1440);
        maint.CheckedChanged += (_, _) => MarkDirty(); maintMin.ValueChanged += (_, _) => MarkDirty();

        // Aviso de cambios remotos (oculto hasta que llega un !CFG mientras hay ediciones sin guardar).
        aviso = Ui.L(this, "", 210, 542, 420, 10, false, Ui.Mut); aviso.AutoSize = true;
        recargar = Ui.B(this, "Recargar", 520, 530, 110, 40);
        recargar.Visible = false;
        recargar.Click += (_, _) => { dirty = false; aviso!.Text = ""; recargar!.Visible = false; Reload(); };

        var remoto = Ui.B(this, "Control remoto…", 20, 530, 180, 40);
        remoto.Click += (_, _) => { using var rf = new RemoteForm(); rf.ShowDialog(this); };
        var save = Ui.B(this, "Guardar", 640, 530, 110, 40, true);
        var close = Ui.B(this, "Cerrar", 770, 530, 110, 40);
        close.Click += (_, _) => Close();
        save.Click += (_, _) => Save();

        loading = false;
        // El servicio avisa (!CFG) cuando la config cambió, p. ej. desde el celular. Refrescar en vivo.
        onCfg = () => { try { BeginInvoke(new Action(OnRemoteCfg)); } catch { } };
        Agent.CfgChanged += onCfg;
        FormClosed += (_, _) => { if (onCfg != null) Agent.CfgChanged -= onCfg; };
    }

    void MarkDirty() { if (!loading) dirty = true; }

    // Llega un cambio remoto: si no hay ediciones sin guardar, recarga directo; si las hay, avisa y ofrece Recargar.
    void OnRemoteCfg()
    {
        if (!dirty) { Reload(); return; }
        if (aviso != null) aviso.Text = "Hay cambios nuevos desde el celular.";
        if (recargar != null) recargar.Visible = true;
    }

    // Vuelve a pedir la config al servicio y repuebla todos los controles.
    void Reload()
    {
        var js = Link.Req("GETCFG");
        if (js == null || !js.StartsWith("{")) return;
        d = JsonSerializer.Deserialize<Cfg>(js)!;
        loading = true;
        try
        {
            for (int i = 0; i < 7; i++)
            {
                var day = d.Days[i];
                chk[i].Checked = day.Restricted;
                Set(ini[i], day.Start); Set(fin[i], day.End);
                Toggle(i);
            }
            extra.Value = Math.Clamp(d.ExtraMinutes, 0, 1440);
            sc.Text = d.Shortcut;
            grace.Value = Math.Clamp(d.GraceSeconds, 0, 600);
            maint.Checked = d.MaintOn; maint.Text = maint.Checked ? "Activado" : "Desactivado";
            maintMin.Value = Math.Clamp(d.MaintMinutes, 0, 1440);
        }
        finally { loading = false; dirty = false; }
    }

    static void Set(ComboBox c, string? v)
    {
        if (string.IsNullOrEmpty(v)) return;
        if (!c.Items.Contains(v)) c.Items.Add(v);
        c.SelectedItem = v;
    }
    void Toggle(int i) { ini[i].Enabled = fin[i].Enabled = chk[i].Checked; }

    void Save()
    {
        var n = new Cfg { GraceSeconds = (int)grace.Value, Shortcut = sc.Text, MaintMinutes = (int)maintMin.Value, MaintOn = maint.Checked };
        for (int i = 0; i < 7; i++)
        {
            var day = n.Days[i]; day.Restricted = chk[i].Checked;
            if (!day.Restricted) continue;
            day.Start = ini[i].SelectedItem as string; day.End = fin[i].SelectedItem as string;
            if (!Scheduler.TryT(day.Start, out var s) || !Scheduler.TryT(day.End, out var e) || e <= s)
            { Dlg.Show($"Revisá el horario del {Dias[i]}: elegí inicio y fin (el fin debe ser posterior al inicio)."); return; }
        }
        var r = Link.Req("SETCFG " + JsonSerializer.Serialize(n));
        Dlg.Show(r == "OK" ? "Configuración guardada." : "No se pudo guardar: " + (r ?? "sin respuesta del servicio"));
    }
}

/// <summary>Aviso / confirmación con el mismo estilo oscuro (reemplaza a MessageBox).</summary>
static class Dlg
{
    public static void Show(string text) => Run(text, "Aceptar", null);
    public static DialogResult Show(string text, string caption, MessageBoxButtons b) =>
        Run(text, "Sí", "No") ? DialogResult.Yes : DialogResult.No;

    static bool Run(string text, string okText, string? noText)
    {
        using var f = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterScreen, TopMost = true, ShowInTaskbar = false,
            BackColor = Ui.Card, ForeColor = Ui.Fg, Font = new Font("Segoe UI", 10.5f), ClientSize = new Size(420, 170)
        };
        f.Paint += (_, e) => { using var p = new Pen(Ui.Line); e.Graphics.DrawRectangle(p, 0, 0, f.Width - 1, f.Height - 1); };
        var l = Ui.L(f, text, 24, 22, 372, 10.5f); l.AutoSize = false; l.Height = 90; l.TextAlign = ContentAlignment.MiddleCenter;
        var ok = Ui.B(f, okText, noText == null ? 150 : 214, 120, noText == null ? 120 : 182, 36, true);
        ok.Click += (_, _) => f.DialogResult = DialogResult.OK;
        f.AcceptButton = ok;
        if (noText != null)
        {
            var no = Ui.B(f, noText, 24, 120, 182, 36);
            no.Click += (_, _) => f.DialogResult = DialogResult.Cancel;
            f.CancelButton = no;
        }
        f.Shown += (_, _) => Native.Front(f.Handle);
        return f.ShowDialog() == DialogResult.OK;
    }
}

/// <summary>Control remoto: URL del servidor y código numérico para vincular el celular.</summary>
class RemoteForm : Form
{
    readonly TextBox url;
    readonly Label code, info, estado;

    public RemoteForm()
    {
        Text = "Control remoto"; ClientSize = new Size(560, 330); BackColor = Ui.Bg; ForeColor = Ui.Fg; Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true;

        Ui.L(this, "Control remoto desde el celular", 20, 18, 0, 14, true);
        estado = Ui.L(this, "", 20, 46, 520, 10, false, Ui.Mut);
        Ui.L(this, "URL del servidor (la que te dio el despliegue, ej: https://xxx.workers.dev)", 20, 74, 520, 9, false, Ui.Mut);
        url = Ui.T(this, 20, 96, 400);
        var activar = Ui.B(this, "Generar código", 430, 94, 110, 28, true);
        activar.Click += (_, _) => Vincular();

        info = Ui.L(this, "", 20, 150, 520, 10, false, Ui.Fg); info.TextAlign = ContentAlignment.MiddleCenter;
        code = Ui.L(this, "", 20, 178, 520, 34, true, Ui.Blue); code.TextAlign = ContentAlignment.MiddleCenter; code.Height = 60;

        var desvincular = Ui.B(this, "Desvincular celular", 20, 275, 180, 36);
        desvincular.Click += (_, _) =>
        {
            if (Dlg.Show("El celular vinculado perderá el acceso. ¿Seguir?", "Desvincular", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            var r = Link.Req("RUNLINK"); Cargar(); Dlg.Show(r == "OK" ? "Celular desvinculado." : "No se pudo.");
        };
        var apagar = Ui.B(this, "Desactivar remoto", 210, 275, 170, 36);
        apagar.Click += (_, _) => { var r = Link.Req("ROFF"); Cargar(); Dlg.Show(r == "OK" ? "Control remoto desactivado." : "No se pudo."); };
        var cerrar = Ui.B(this, "Cerrar", 450, 275, 90, 36);
        cerrar.Click += (_, _) => Close();

        Cargar();
    }

    void Cargar()
    {
        code.Text = ""; info.Text = "";
        var r = Link.Req("RGET");
        if (r != null && r.StartsWith("{"))
        {
            using var d = JsonDocument.Parse(r);
            var e = d.RootElement;
            bool on = e.TryGetProperty("enabled", out var en) && en.GetBoolean();
            var u = e.TryGetProperty("url", out var up) && up.ValueKind == JsonValueKind.String ? up.GetString() : null;
            url.Text = u ?? "";
            if (on && u != null) { estado.Text = "Estado: activo."; PedirCodigoAsync(); }
            else estado.Text = "Estado: desactivado. Poné la URL y tocá Generar código.";
        }
    }

    /// <summary>Pide un código nuevo sin congelar la ventana (puede tardar si el remoto recién conectó).</summary>
    void PedirCodigoAsync()
    {
        info.Text = "Pidiendo código al servidor…"; code.Text = "";
        Task.Run(() => Link.Req("RCODE", 16000)).ContinueWith(t =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    var c = t.Result;
                    if (c != null && !c.StartsWith("ERR")) { info.Text = "Ingresá este código en la app del celular (dura 5 minutos):"; code.Text = c; }
                    else { info.Text = "Código no disponible. Esperá unos segundos y tocá Generar código."; code.Text = ""; }
                }));
            }
            catch { }
        });
    }

    void Vincular()
    {
        if (string.IsNullOrWhiteSpace(url.Text) || !url.Text.StartsWith("http"))
        { Dlg.Show("Poné la URL del servidor (empieza con https://)."); return; }
        var r = Link.Req("RSET " + url.Text.Trim());
        if (r == null || !r.StartsWith("{")) { Dlg.Show("No se pudo activar: " + (r ?? "sin respuesta")); return; }
        estado.Text = "Estado: activo.";
        PedirCodigoAsync();
    }
}
