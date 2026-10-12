using System.Drawing.Drawing2D;

namespace GhostDeck;

/// <summary>
/// EcoCool Focus home page. It deliberately exposes only the four operating states that matter
/// for the verified 1583EMS1.110 workflow. The older GhostDeck feature catalogue remains in the
/// source for compatibility/rollback, but it is not part of this edition's primary UI.
/// </summary>
public sealed class FocusHomePage : ThemedPage
{
    private const int Pad = 28;
    private readonly ModeCard _ecoCool;
    private readonly ModeCard _oemEco;
    private readonly ModeCard _balanced;
    private readonly ModeCard _extreme;
    private readonly Button _applyClose = new();
    private readonly Button _restoreOem = new();
    private readonly Button _hdrButton = new();
    private readonly Button _turboButton = new();
    private HwSnapshot _hw;

    public FocusHomePage(MainDeps d) : base(d)
    {
        AutoScroll = false;

        _ecoCool = new ModeCard(
            "ECO COOL",
            "ECO MSI + curva de ventiladores personalizada",
            () => D.EcoCoolOn(),
            () => { if (D.EcoCoolAvailable() && D.Writable()) D.SetEcoCool(true); RefreshNow(); });

        _oemEco = new ModeCard(
            "ECO ORIGINAL MSI",
            "Perfil ECO original · ventiladores automáticos",
            () => D.ProfileKnown() && !D.EcoCoolOn() && D.Current() == ProfileId.SuperBattery,
            () => { if (D.Writable()) D.SetProfile(ProfileId.SuperBattery); RefreshNow(); });

        _balanced = new ModeCard(
            "EQUILIBRADO",
            "Perfil equilibrado del controlador MSI",
            () => D.ProfileKnown() && !D.EcoCoolOn() && D.Current() == ProfileId.Balanced,
            () => { if (D.Writable()) D.SetProfile(ProfileId.Balanced); RefreshNow(); });

        _extreme = new ModeCard(
            "RENDIMIENTO",
            "Perfil de máximo rendimiento MSI",
            () => D.ProfileKnown() && !D.EcoCoolOn() && D.Current() == ProfileId.Extreme,
            () => { if (D.Writable()) D.SetProfile(ProfileId.Extreme); RefreshNow(); });

        foreach (var c in Cards) Controls.Add(c);

        _restoreOem.Text = "Restaurar ECO original MSI";
        _restoreOem.AutoSize = false;
        _restoreOem.Height = 42;
        Ui.StyleGhost(_restoreOem);
        _restoreOem.Click += (_, _) =>
        {
            if (D.Settings.EcoCoolPhase.Length > 0) D.SetEcoCool(false);
            else if (D.Writable()) D.SetProfile(ProfileId.SuperBattery);
            RefreshNow();
        };
        Controls.Add(_restoreOem);

        _applyClose.Text = "Cerrar";
        _applyClose.AutoSize = false;
        _applyClose.Height = 42;
        Ui.StylePrimary(_applyClose);
        _applyClose.Click += (_, _) => (FindForm() as MainForm)?.RequestFocusExit();
        Controls.Add(_applyClose);

        _hdrButton.AccessibleName = "Alternar HDR de Windows";
        _hdrButton.AutoSize = false;
        _hdrButton.Height = 42;
        _hdrButton.Click += (_, _) => ToggleHdr();
        Controls.Add(_hdrButton);

        _turboButton.AccessibleName = "Alternar Turbo Boost de CPU de Windows";
        _turboButton.AutoSize = false;
        _turboButton.Height = 42;
        _turboButton.Click += (_, _) => ToggleTurbo();
        Controls.Add(_turboButton);

        Resize += (_, _) => Relayout();
        RefreshNow();
    }

    private ModeCard[] Cards => new[] { _ecoCool, _oemEco, _balanced, _extreme };

    public override void OnEnter() { RefreshNow(); Relayout(); }
    public override void LiveRefresh() => RefreshNow();
    public override void ApplyTheme()
    {
        base.ApplyTheme();
        foreach (var c in Cards) c.Invalidate();
        Ui.StyleGhost(_restoreOem);
        Ui.StylePrimary(_applyClose);
        Ui.StyleGhost(_hdrButton);
        Ui.StyleGhost(_turboButton);
        Invalidate();
    }

    private void RefreshNow()
    {
        _restoreOem.Text = D.Settings.EcoCoolPhase.Length > 0 ? "Restaurar estado anterior" : "Restaurar ECO original MSI";
        _restoreOem.Enabled = D.Settings.EcoCoolPhase.Length > 0 || D.Writable();
        foreach (var c in Cards) c.Enabled = D.Writable();
        try { _hw = D.Hw(); } catch { _hw = default; }
        foreach (var c in Cards) c.Invalidate();
        // These are Windows-only actions: neither routes through the EC controller.
        // Unknown/unavailable state is non-actionable; no guessed UI state.
        bool hdrSupported = Hdr.Supported();
        _hdrButton.Enabled = hdrSupported;
        _hdrButton.Text = hdrSupported ? (Hdr.Enabled() ? "HDR: ACTIVADO" : "HDR: DESACTIVADO") : "HDR: NO DISPONIBLE";
        var turbo = PowerPlan.TurboStatus();
        _turboButton.Enabled = turbo.StartsWith("turbo boost: on (", StringComparison.Ordinal) || turbo.StartsWith("turbo boost: off (", StringComparison.Ordinal);
        _turboButton.Text = turbo.StartsWith("turbo boost: on (", StringComparison.Ordinal) ? "Turbo CPU: ACTIVADO" : turbo.StartsWith("turbo boost: off (", StringComparison.Ordinal) ? "Turbo CPU: DESACTIVADO" : "Turbo CPU: NO DISPONIBLE / MIXTO";
        Invalidate();
    }

    private void Relayout()
    {
        int inner = Math.Max(600, ClientSize.Width - Pad * 2);
        int gap = 16;
        int cols = inner >= 1100 ? 4 : 2;
        int cardW = (inner - gap * (cols - 1)) / cols;
        int cardH = 185;
        int top = 213;
        for (int i = 0; i < Cards.Length; i++)
        {
            int row = i / cols, col = i % cols;
            Cards[i].SetBounds(Pad + col * (cardW + gap), top + row * (cardH + gap), cardW, cardH);
        }

        int rows = (Cards.Length + cols - 1) / cols;
        int y = top + rows * cardH + (rows - 1) * gap + 28;
        int btnW = Math.Min(310, Math.Max(220, (inner - gap) / 2));
        _restoreOem.SetBounds(Pad, y, btnW, 42);
        _applyClose.SetBounds(Pad + btnW + gap, y, btnW, 42);
        // Additional Windows switches sit below the original actions; no changes to EC profile cards.
        int switchesY = y + 58;
        _hdrButton.SetBounds(Pad, switchesY, btnW, 42);
        _turboButton.SetBounds(Pad + btnW + gap, switchesY, btnW, 42);
    }

    private void ToggleHdr()
    {
        if (!Hdr.Supported()) { RefreshNow(); return; }
        bool previous = Hdr.Enabled();
        try
        {
            if (!Hdr.Set(!previous) || Hdr.Enabled() == previous)
                MessageBox.Show("Windows no confirmó el cambio HDR. Verifica la compatibilidad del monitor.", "EcoCool HDR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "EcoCool HDR", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshNow();
    }

    private void ToggleTurbo()
    {
        // Never choose a setting when the Windows power plan is unknown or AC/DC differ.
        string before = PowerPlan.TurboStatus();
        bool on = before.StartsWith("turbo boost: on (", StringComparison.Ordinal);
        bool off = before.StartsWith("turbo boost: off (", StringComparison.Ordinal);
        if (!on && !off) { RefreshNow(); return; }
        try
        {
            string error = on ? PowerPlan.TurboOff(D.Settings) : PowerPlan.TurboOn(D.Settings);
            string after = PowerPlan.TurboStatus();
            bool verified = on ? after.StartsWith("turbo boost: off (", StringComparison.Ordinal) : after.StartsWith("turbo boost: on (", StringComparison.Ordinal);
            if (error.Length > 0 || !verified)
                MessageBox.Show(error.Length > 0 ? error : "No se verificó el estado efectivo del Turbo Boost de Windows.", "EcoCool Turbo CPU", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "EcoCool Turbo CPU", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshNow();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleF = new Font("Segoe UI", 24f, FontStyle.Bold);
        using var subF = new Font("Segoe UI", 10.5f);
        using var valueF = new Font("Segoe UI", 19f, FontStyle.Bold);
        using var labelF = new Font("Segoe UI", 9.5f);

        Ui.DrawText(g, "ECO COOL", titleF, new Point(Pad, 24), Theme.Text);
        string fw = D.Firmware();
        string contract = string.IsNullOrWhiteSpace(fw)
            ? "EC no identificado"
            : $"MSI EC {fw} · identidad de hardware pendiente de validación";
        Ui.DrawText(g, contract, subF, new Point(Pad, 66), Theme.Muted);
        if (!SafetyEdition.HardwareWritesAuthorized)
            Ui.DrawText(g, "Control manual bloqueado: falta verificar modelo, BIOS y contrato EC.", subF,
                new Point(Pad, 82), Theme.Amber);
        else if (!D.EcoCoolAvailable())
            Ui.DrawText(g, "ECO COOL requiere calificación de esta versión: --qualify-ecocool", subF,
                new Point(Pad, 82), Theme.Amber);

        var tel = new Rectangle(Pad, 104, Math.Max(600, ClientSize.Width - Pad * 2), 82);
        Ui.FillCard(g, tel);
        DrawMetric(g, tel.X + 18, tel.Y + 13, "CPU", _hw.CpuTemp > 0 ? $"{_hw.CpuTemp} °C" : "—", _hw.CpuRpm > 0 ? $"{_hw.CpuRpm} RPM" : "—", valueF, labelF);
        DrawMetric(g, tel.X + 220, tel.Y + 13, "GPU", _hw.GpuTemp > 0 ? $"{_hw.GpuTemp} °C" : "—", _hw.GpuRpm > 0 ? $"{_hw.GpuRpm} RPM" : "—", valueF, labelF);

        string mode = !D.ProfileKnown() ? "DESCONOCIDO" : D.EcoCoolOn() ? "ECO COOL" : D.Current() switch
        {
            ProfileId.SuperBattery => "SUPER BATTERY",
            ProfileId.Balanced => "EQUILIBRADO",
            ProfileId.Extreme => "RENDIMIENTO",
            ProfileId.Silent => "SILENT (no es ECO MSI)",
            _ => D.Current().ToString().ToUpperInvariant(),
        };
        int modeX = tel.X + Math.Min(470, Math.Max(400, tel.Width / 2));
        Ui.DrawText(g, "MODO EFECTIVO", labelF, new Point(modeX, tel.Y + 14), Theme.Muted);
        Ui.DrawText(g, mode, valueF, new Rectangle(modeX, tel.Y + 34, Math.Max(120, tel.Right - modeX - 18), 34), Theme.Accent,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private static void DrawMetric(Graphics g, int x, int y, string label, string value, string rpm, Font valueF, Font labelF)
    {
        Ui.DrawText(g, label, labelF, new Point(x, y), Theme.Muted);
        Ui.DrawText(g, value, valueF, new Point(x, y + 18), Theme.Text);
        Ui.DrawText(g, rpm, labelF, new Point(x + 92, y + 31), Theme.Muted);
    }

    private sealed class ModeCard : Control
    {
        private readonly string _title;
        private readonly string _sub;
        private readonly Func<bool> _active;
        private readonly Action _action;
        private bool _hover;

        public ModeCard(string title, string sub, Func<bool> active, Action action)
        {
            _title = title; _sub = sub; _active = active; _action = action;
            DoubleBuffered = true; ResizeRedraw = true; Cursor = Cursors.Hand;
            AccessibleName = title; AccessibleDescription = sub; AccessibleRole = AccessibleRole.PushButton;
            TabStop = true;
            KeyDown += (_, e) => { if (Enabled && e.KeyCode is Keys.Enter or Keys.Space) { _action(); e.Handled = true; } };
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
            MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _action(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Surface);
            bool active = _active();
            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Theme.RoundRect(rect, 8))
            using (var fill = new SolidBrush(active ? Theme.AccentSoft : Theme.Card))
            using (var pen = new Pen(active ? Theme.Accent : (_hover ? Theme.BorderStrong : Theme.Border), active ? 2f : 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }

            using var titleF = new Font("Segoe UI", 14.5f, FontStyle.Bold);
            using var subF = new Font("Segoe UI", 9.5f);
            using var footF = new Font("Segoe UI", 9f, FontStyle.Bold);
            Color icon = active ? Theme.Accent : Theme.Muted;
            using (var b = new SolidBrush(Color.FromArgb(active ? 58 : 30, icon)))
                g.FillEllipse(b, Width / 2 - 25, 24, 50, 50);
            using (var p = new Pen(icon, 3f))
            {
                g.DrawArc(p, Width / 2 - 15, 34, 30, 30, 205, 285);
                g.DrawLine(p, Width / 2 - 2, 38, Width / 2 + 11, 52);
            }

            Ui.DrawText(g, _title, titleF, new Rectangle(12, 88, Width - 24, titleF.Height + 4), Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
            Ui.DrawText(g, _sub, subF, new Rectangle(14, 118, Width - 28, 36), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            Ui.DrawText(g, active ? "ACTIVO" : !Enabled ? "BLOQUEADO" : "ELEGIR", footF, new Rectangle(12, Height - 31, Width - 24, 20),
                active ? Theme.Accent : Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
