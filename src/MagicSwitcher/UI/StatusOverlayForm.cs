using System.Runtime.InteropServices;
using MagicSwitcher.Localization;
using MagicSwitcher.Service;

namespace MagicSwitcher.UI;

/// <summary>
/// The status line on the sign-in and lock screens (see <see cref="StatusOverlay"/>). Display only: no controls that take
/// input, never activated, transparent to the mouse.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class StatusOverlayForm : Form
{
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    private readonly Label _text;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly DateTime _started = DateTime.UtcNow;
    private int _missing;
    private string _shown = "";

    public StatusOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(32, 32, 32);
        ForeColor = Color.White;
        Opacity = 0.92;
        Padding = new Padding(18, 12, 18, 12);
        Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 12f);

        _text = new Label { AutoSize = true, MaximumSize = new Size(MaxTextWidth(), 0), UseMnemonic = false };
        Controls.Add(_text);

        _timer.Tick += (_, _) => Refresh(firstTime: false);
        Refresh(firstTime: true);
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WsExTopmost = 0x00000008, WsExTransparent = 0x00000020, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTopmost | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    private static int MaxTextWidth() => Math.Max(320, (int)((Screen.PrimaryScreen?.WorkingArea.Width ?? 1280) * 0.6));

    private void Refresh(bool firstTime)
    {
        var status = KeyboardStatus.Read();
        if (status is null)
        {
            // The file is replaced atomically, but allow a couple of misses before deciding there is nothing to show.
            if (firstTime || ++_missing >= 3 || DateTime.UtcNow - _started > MaxLifetime)
            {
                _timer.Stop();
                Close();
            }

            return;
        }

        if (DateTime.UtcNow - _started > MaxLifetime)
        {
            _timer.Stop();
            Close();
            return;
        }

        _missing = 0;
        string text = Describe(status.Value.Phase, status.Value.Name);
        if (text != _shown)
        {
            _shown = text;
            MagicSwitcher.Infrastructure.Log.Info($"Status window shows: {status.Value.Phase}");
            _text.Text = text;
            _text.Location = new Point(Padding.Left, Padding.Top);
            ClientSize = new Size(_text.PreferredWidth + Padding.Horizontal, _text.PreferredHeight + Padding.Vertical);
            var area = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Bottom - Height - area.Height / 8);
        }

        // The sign-in screen is a topmost window too: stay above it.
        const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private static string Describe(KeyboardPhase phase, string name) => phase switch
    {
        KeyboardPhase.Waiting => Strings.OverlayWaiting(name),
        KeyboardPhase.Idle => Strings.OverlayIdle(name),
        KeyboardPhase.Pairing => Strings.OverlayPairing(name),
        KeyboardPhase.Connecting => Strings.OverlayConnecting(name),
        KeyboardPhase.Connected => Strings.OverlayConnected(name),
        _ => "",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
