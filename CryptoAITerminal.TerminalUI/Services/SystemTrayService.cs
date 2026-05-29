using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// Управляет иконкой в системном трее и всплывающими уведомлениями Windows.
/// Должен создаваться и использоваться только в UI-потоке.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private bool _disposed;

    /// <summary>Вызывается при двойном клике или выборе «Открыть» в трее.</summary>
    public Action? OnShowRequested  { get; set; }

    /// <summary>Вызывается при выборе «Выход» в трее.</summary>
    public Action? OnExitRequested  { get; set; }

    public SystemTrayService()
    {
        _notifyIcon = new NotifyIcon
        {
            Text    = "Crypto AI Terminal",
            Icon    = BuildIcon(),
            Visible = true,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть",    null, (_, _) => OnShowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход",      null, (_, _) => OnExitRequested?.Invoke());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick     += (_, _) => OnShowRequested?.Invoke();
    }

    // ── Уведомления ──────────────────────────────────────────────────────────

    public void ShowInfo(string title, string body)
        => Show(title, body, ToolTipIcon.Info);

    public void ShowWarning(string title, string body)
        => Show(title, body, ToolTipIcon.Warning);

    public void ShowError(string title, string body)
        => Show(title, body, ToolTipIcon.Error);

    private void Show(string title, string body, ToolTipIcon level)
    {
        if (_disposed) return;
        try
        {
            _notifyIcon.ShowBalloonTip(5_000, title, body, level);
        }
        catch { /* best-effort */ }
    }

    // ── Иконка ───────────────────────────────────────────────────────────────

    private static Icon BuildIcon()
    {
        var baseDir = System.IO.Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;

        // 1. Try loading the ICO file (multi-size, best quality)
        try
        {
            var icoPath = System.IO.Path.Combine(baseDir, "Assets", "app.ico");
            if (System.IO.File.Exists(icoPath))
                return new Icon(icoPath, 32, 32);
        }
        catch { /* fall through */ }

        // 2. Try loading the brand PNG and converting to icon
        try
        {
            var pngPath = System.IO.Path.Combine(baseDir, "Assets", "app-icon-square.png");
            if (!System.IO.File.Exists(pngPath))
                pngPath = System.IO.Path.Combine(baseDir, "Assets", "app-icon.png");

            if (System.IO.File.Exists(pngPath))
            {
                using var bmp = new Bitmap(pngPath);
                using var resized = new Bitmap(bmp, new System.Drawing.Size(32, 32));
                return Icon.FromHandle(resized.GetHicon());
            }
        }
        catch { /* fall through to generated icon */ }

        // Fallback: generate programmatically (candlestick chart style)
        try
        {
            const int S = 32;
            using var bmp = new Bitmap(S, S);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(10, 20, 35));

                // Teal border
                using var border = new Pen(Color.FromArgb(20, 224, 193), 1.5f);
                g.DrawRectangle(border, 1, 1, S - 2, S - 2);

                // 3 quick candlesticks
                float[] xs     = { 7f, 14f, 21f };
                float[] opens  = { 22f, 16f, 19f };
                float[] closes = { 16f, 19f, 9f  };
                bool[]  bull   = { false, true, true };

                using var wickPen = new Pen(Color.FromArgb(130, 140, 155, 175), 1f);
                for (int i = 0; i < 3; i++)
                {
                    var top = Math.Min(opens[i], closes[i]) - 3;
                    var bot = Math.Max(opens[i], closes[i]) + 3;
                    g.DrawLine(wickPen, xs[i], top, xs[i], bot);

                    var c = bull[i] ? Color.FromArgb(33, 200, 160) : Color.FromArgb(255, 75, 75);
                    using var b = new SolidBrush(c);
                    var by = Math.Min(opens[i], closes[i]);
                    var bh = Math.Max(2f, Math.Abs(opens[i] - closes[i]));
                    g.FillRectangle(b, xs[i] - 2.5f, by, 5f, bh);
                }

                // 'AI' label
                using var font = new Font("Segoe UI", 7, FontStyle.Bold, GraphicsUnit.Pixel);
                using var tb   = new SolidBrush(Color.FromArgb(200, 20, 224, 193));
                g.DrawString("AI", font, tb, 19f, 24f);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
