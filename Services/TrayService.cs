using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Aether.Services;

/// <summary>
/// Icône de la zone de notification : menu, infobulle d'état et notifications.
///
/// Les bulles de <see cref="Forms.NotifyIcon"/> s'affichent sous Windows 10/11 comme de
/// vraies notifications système (centre de notifications compris), sans dépendre du SDK
/// Windows App ni d'un identifiant d'application enregistré.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _alertsItem;

    /// <summary>Une icône par gravité (0 vert, 1 orange, 2 rouge), dessinée une fois.</summary>
    private readonly Icon[] _icons;
    private readonly IntPtr[] _handles;
    private int _severity = -1;

    public event Action? OpenRequested;
    public event Action? QuitRequested;
    public event Action<bool>? AlertsToggled;

    public TrayService(bool alertsEnabled)
    {
        _handles = new IntPtr[3];
        _icons = new[]
        {
            Draw(Color.FromArgb(0x2B, 0xE8, 0xA4), 0),
            Draw(Color.FromArgb(0xFF, 0xB0, 0x20), 1),
            Draw(Color.FromArgb(0xFF, 0x3B, 0x5C), 2),
        };

        _alertsItem = new Forms.ToolStripMenuItem("Alertes activées") { CheckOnClick = true, Checked = alertsEnabled };
        _alertsItem.CheckedChanged += (_, _) => AlertsToggled?.Invoke(_alertsItem.Checked);

        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem("Ouvrir AETHER") { Font = new Font(Forms.Control.DefaultFont, FontStyle.Bold) };
        open.Click += (_, _) => OpenRequested?.Invoke();
        menu.Items.Add(open);
        menu.Items.Add(_alertsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = new Forms.ToolStripMenuItem("Quitter");
        quit.Click += (_, _) => QuitRequested?.Invoke();
        menu.Items.Add(quit);

        _icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "AETHER",
            Visible = true
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();

        SetSeverity(0);
    }

    /// <summary>Couleur de l'icône : l'état se lit sans ouvrir la fenêtre.</summary>
    public void SetSeverity(int severity)
    {
        severity = Math.Clamp(severity, 0, 2);
        if (severity == _severity) return;
        _severity = severity;
        _icon.Icon = _icons[severity];
    }

    /// <summary>Infobulle au survol. Windows la tronque à 63 caractères.</summary>
    public void SetTooltip(string text) =>
        _icon.Text = text.Length > 63 ? text[..63] : text;

    public void SetAlertsChecked(bool enabled)
    {
        if (_alertsItem.Checked != enabled) _alertsItem.Checked = enabled;
    }

    public void Notify(string title, string message, bool warning = true) =>
        _icon.ShowBalloonTip(8000, title, message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    /// <summary>Anneau coloré sur fond sombre, dans l'esprit du halo de l'interface.</summary>
    private Icon Draw(Color accent, int slot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0x12, 0x16, 0x1F));
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var ring = new Pen(accent, 4);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            using var dot = new SolidBrush(accent);
            g.FillEllipse(dot, 12, 12, 8, 8);
        }

        _handles[slot] = bmp.GetHicon();
        return Icon.FromHandle(_handles[slot]);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        // Sans Visible = false, l'icône reste affichée jusqu'au survol de la souris.
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons) i.Dispose();
        // Icon.FromHandle ne possède pas le handle : il faut le libérer explicitement.
        foreach (var h in _handles) if (h != IntPtr.Zero) DestroyIcon(h);
    }
}
