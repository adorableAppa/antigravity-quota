using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace AntigravityQuota
{
    /// <summary>
    /// Manages the system tray NotifyIcon, themed context menu, and balloon notifications.
    /// </summary>
    public class TrayService : IDisposable
    {
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private bool _hasShownTrayBalloon = false;

        // Tracks per-model exhaustion state to detect transitions
        private readonly Dictionary<string, bool> _previousModelStates = new();

        /// <summary>Raised when the user double-clicks the tray icon or selects "Open Dashboard".</summary>
        public event Action? RestoreRequested;

        /// <summary>Raised when the user selects "Exit" from the tray menu.</summary>
        public event Action? ShutdownRequested;

        public void Initialize()
        {
            try
            {
                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                BuildBaseTrayMenu(contextMenu);

                // Apply Windows 11 rounded corners + themed colors when the popup opens
                contextMenu.Opening += (s, e) =>
                {
                    if (s is System.Windows.Forms.ContextMenuStrip strip)
                    {
                        strip.Renderer = CreateTrayRenderer();
                        GdiInterop.ApplyRoundedCorners(strip.Handle);
                    }
                };

                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Text = "Antigravity Quota",
                    ContextMenuStrip = contextMenu,
                    Visible = true
                };

                // Extract and assign the application icon
                string processPath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
                {
                    _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);
                }

                _notifyIcon.DoubleClick += (s, e) => RestoreRequested?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize system tray: {ex.Message}");
            }
        }

        /// <summary>Shows the "still running in background" balloon once.</summary>
        public void ShowBackgroundBalloon()
        {
            if (!_hasShownTrayBalloon)
            {
                _notifyIcon?.ShowBalloonTip(3000,
                    "Antigravity Quota",
                    "The application is still running in the background.",
                    System.Windows.Forms.ToolTipIcon.Info);
                _hasShownTrayBalloon = true;
            }
        }

        public void UpdateMenu(QuotaSnapshot snapshot)
        {
            if (_notifyIcon?.ContextMenuStrip == null) return;

            try
            {
                var menu = _notifyIcon.ContextMenuStrip;
                menu.Items.Clear();

                // ── Header ──
                menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Antigravity Quota")
                {
                    Enabled = false,
                    Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold)
                });
                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                // ── Prompt Credits ──
                if (snapshot.PromptCredits != null)
                {
                    int pct = (int)Math.Round(snapshot.PromptCredits.RemainingPercentage * 100);
                    string creditsLabel = $"Credits:  {snapshot.PromptCredits.Available:N0} / {snapshot.PromptCredits.Monthly:N0}  ({pct}% left)";
                    menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(creditsLabel) { Enabled = false });
                    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                }

                // ── Per-model quota ──
                bool hasModels = false;
                foreach (var m in snapshot.Models)
                {
                    if (m.IsAutocompleteOnly) continue;
                    hasModels = true;

                    string status;
                    if (m.IsExhausted)
                    {
                        status = "EXHAUSTED";
                    }
                    else if (m.RemainingPercentage.HasValue)
                    {
                        int pct = (int)Math.Round(m.RemainingPercentage.Value * 100);
                        status = $"{pct}% remaining";
                    }
                    else
                    {
                        status = "Unlimited";
                    }

                    string icon = m.IsExhausted ? "✗" : "✓";
                    string label = $"{icon}  {m.Label}:  {status}";

                    var item = new System.Windows.Forms.ToolStripMenuItem(label) { Enabled = false };
                    if (m.IsExhausted)
                    {
                        item.ForeColor = System.Drawing.Color.FromArgb(220, 80, 80);
                    }
                    menu.Items.Add(item);
                }

                if (!hasModels)
                {
                    menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("No models tracked") { Enabled = false });
                }

                menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

                // ── Actions ──
                var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Dashboard");
                openItem.Click += (s, e) => RestoreRequested?.Invoke();
                menu.Items.Add(openItem);

                var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
                exitItem.Click += (s, e) => ShutdownRequested?.Invoke();
                menu.Items.Add(exitItem);

                // Update tooltip with a compact summary
                string tooltipSummary = "Antigravity Quota";
                if (snapshot.PromptCredits != null)
                {
                    tooltipSummary += $" — {snapshot.PromptCredits.Available:N0} credits left";
                }
                _notifyIcon.Text = tooltipSummary.Length > 63
                    ? tooltipSummary[..63]
                    : tooltipSummary;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update tray menu: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks for model exhaustion transitions and shows balloon notifications.
        /// </summary>
        public void CheckAndNotify(QuotaSnapshot snapshot, bool notificationsEnabled)
        {
            if (!notificationsEnabled || _notifyIcon == null) return;

            foreach (var m in snapshot.Models)
            {
                if (m.IsAutocompleteOnly) continue;

                bool wasExhausted = _previousModelStates.TryGetValue(m.ModelId, out bool prev) && prev;
                bool isExhausted  = m.IsExhausted;

                if (!wasExhausted && isExhausted)
                {
                    _notifyIcon.ShowBalloonTip(
                        4000,
                        "Quota Exhausted ✗",
                        $"{m.Label} has reached its quota limit.",
                        System.Windows.Forms.ToolTipIcon.Warning);
                }
                else if (wasExhausted && !isExhausted)
                {
                    _notifyIcon.ShowBalloonTip(
                        4000,
                        "Quota Reset ✓",
                        $"{m.Label} quota has been reset and is available again.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }

                _previousModelStates[m.ModelId] = isExhausted;
            }
        }

        public void Dispose()
        {
            _notifyIcon?.Dispose();
            _notifyIcon = null;
        }

        // ── Private: Base tray menu ────────────────────────────────────────

        private void BuildBaseTrayMenu(System.Windows.Forms.ContextMenuStrip contextMenu)
        {
            contextMenu.Items.Clear();

            var headerItem = new System.Windows.Forms.ToolStripMenuItem("Antigravity Quota")
            {
                Enabled = false,
                Font = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold)
            };
            contextMenu.Items.Add(headerItem);
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var loadingItem = new System.Windows.Forms.ToolStripMenuItem("Loading quota...")
            {
                Enabled = false
            };
            contextMenu.Items.Add(loadingItem);
            contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Dashboard");
            openItem.Click += (s, e) => RestoreRequested?.Invoke();
            contextMenu.Items.Add(openItem);

            var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => ShutdownRequested?.Invoke();
            contextMenu.Items.Add(exitItem);
        }

        // ── Private: Themed tray renderer ──────────────────────────────────

        private ThemedTrayRenderer CreateTrayRenderer()
        {
            var config = ConfigService.LoadGlobalConfig();
            string t = (config.theme ?? "").ToLower();

            System.Drawing.Color bg, iconMargin, border, accent, text, dimText;

            if (t.Contains("latte") || t.Contains("latté"))
            {
                bg         = C(0xEF, 0xF1, 0xF5);
                iconMargin = C(0xDC, 0xE0, 0xE8);
                border     = C(0xCC, 0xD0, 0xDA);
                accent     = C(0x88, 0x39, 0xEF);
                text       = C(0x4C, 0x4F, 0x69);
                dimText    = C(0x6C, 0x6F, 0x85);
            }
            else if (t.Contains("frappé") || t.Contains("frappe"))
            {
                bg         = C(0x30, 0x34, 0x46);
                iconMargin = C(0x23, 0x26, 0x34);
                border     = C(0x41, 0x45, 0x59);
                accent     = C(0xCA, 0x9E, 0xE6);
                text       = C(0xC6, 0xD0, 0xF5);
                dimText    = C(0xA5, 0xAD, 0xCE);
            }
            else if (t.Contains("macchiato"))
            {
                bg         = C(0x24, 0x27, 0x3A);
                iconMargin = C(0x18, 0x19, 0x26);
                border     = C(0x36, 0x3A, 0x4F);
                accent     = C(0xC6, 0xA0, 0xF6);
                text       = C(0xCA, 0xD3, 0xF5);
                dimText    = C(0xA5, 0xAD, 0xCB);
            }
            else if (t.Contains("dracula"))
            {
                bg         = C(0x28, 0x2A, 0x36);
                iconMargin = C(0x1E, 0x1F, 0x29);
                border     = C(0x44, 0x47, 0x5A);
                accent     = C(0xBD, 0x93, 0xF9);
                text       = C(0xF8, 0xF8, 0xF2);
                dimText    = C(0x62, 0x72, 0xA4);
            }
            else if (t.Contains("alucard"))
            {
                bg         = C(0xFF, 0xFB, 0xEB);
                iconMargin = C(0xF3, 0xEF, 0xE0);
                border     = C(0xE5, 0xE2, 0xD3);
                accent     = C(0x64, 0x4A, 0xC9);
                text       = C(0x1F, 0x1F, 0x1F);
                dimText    = C(0x6C, 0x66, 0x4B);
            }
            else if (t.Contains("gruvbox dark"))
            {
                bg         = C(0x28, 0x28, 0x28);
                iconMargin = C(0x1D, 0x20, 0x21);
                border     = C(0x50, 0x49, 0x45);
                accent     = C(0xFA, 0xBD, 0x2F);
                text       = C(0xFB, 0xF1, 0xC7);
                dimText    = C(0x92, 0x83, 0x74);
            }
            else if (t.Contains("gruvbox light"))
            {
                bg         = C(0xFB, 0xF1, 0xC7);
                iconMargin = C(0xF9, 0xF5, 0xD7);
                border     = C(0xEB, 0xDB, 0xB2);
                accent     = C(0xB5, 0x76, 0x14);
                text       = C(0x28, 0x28, 0x28);
                dimText    = C(0x92, 0x83, 0x74);
            }
            else if (t.Contains("nord light"))
            {
                bg         = C(0xEC, 0xEF, 0xF4);
                iconMargin = C(0xE5, 0xE9, 0xF0);
                border     = C(0xD8, 0xDE, 0xE9);
                accent     = C(0x5E, 0x81, 0xAC);
                text       = C(0x2E, 0x34, 0x40);
                dimText    = C(0x4C, 0x56, 0x6A);
            }
            else if (t.Contains("nord"))
            {
                bg         = C(0x3B, 0x42, 0x52);
                iconMargin = C(0x2E, 0x34, 0x40);
                border     = C(0x4C, 0x56, 0x6A);
                accent     = C(0x88, 0xC0, 0xD0);
                text       = C(0xEC, 0xEF, 0xF4);
                dimText    = C(0xD8, 0xDE, 0xE9);
            }
            else if (t.Contains("rosé pine moon") || t.Contains("rose pine moon"))
            {
                bg         = C(0x23, 0x21, 0x36);
                iconMargin = C(0x1B, 0x19, 0x29);
                border     = C(0x39, 0x35, 0x52);
                accent     = C(0xC4, 0xA7, 0xE7);
                text       = C(0xE0, 0xDE, 0xF4);
                dimText    = C(0x90, 0x8C, 0xAA);
            }
            else if (t.Contains("rosé pine dawn") || t.Contains("rose pine dawn"))
            {
                bg         = C(0xFA, 0xF4, 0xED);
                iconMargin = C(0xF2, 0xE9, 0xE1);
                border     = C(0xE4, 0xDB, 0xD2);
                accent     = C(0x90, 0x7A, 0xA9);
                text       = C(0x57, 0x52, 0x79);
                dimText    = C(0x98, 0x93, 0xA5);
            }
            else if (t.Contains("rosé pine") || t.Contains("rose pine"))
            {
                bg         = C(0x19, 0x17, 0x24);
                iconMargin = C(0x12, 0x10, 0x1A);
                border     = C(0x26, 0x23, 0x3A);
                accent     = C(0xC4, 0xA7, 0xE7);
                text       = C(0xE0, 0xDE, 0xF4);
                dimText    = C(0x90, 0x8C, 0xAA);
            }
            else // Default: Mocha
            {
                bg         = C(0x1E, 0x1E, 0x2E);
                iconMargin = C(0x11, 0x11, 0x1B);
                border     = C(0x31, 0x32, 0x44);
                accent     = C(0xCB, 0xA6, 0xF7);
                text       = C(0xCD, 0xD6, 0xF4);
                dimText    = C(0xA6, 0xAD, 0xC8);
            }

            var hover = Blend(border, accent, 0.15f);
            var table = new ThemeColorTable(bg, iconMargin, border, hover, accent);
            return new ThemedTrayRenderer(table, text, dimText);
        }

        private static System.Drawing.Color C(int r, int g, int b)
            => System.Drawing.Color.FromArgb(r, g, b);

        private static System.Drawing.Color Blend(System.Drawing.Color a, System.Drawing.Color b, float t)
            => System.Drawing.Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));

        // ── Nested WinForms renderer classes ──────────────────────────────

        private sealed class ThemeColorTable : System.Windows.Forms.ProfessionalColorTable
        {
            private readonly System.Drawing.Color _bg;
            private readonly System.Drawing.Color _iconMargin;
            private readonly System.Drawing.Color _border;
            private readonly System.Drawing.Color _hover;
            private readonly System.Drawing.Color _accent;

            public ThemeColorTable(
                System.Drawing.Color bg,
                System.Drawing.Color iconMargin,
                System.Drawing.Color border,
                System.Drawing.Color hover,
                System.Drawing.Color accent)
            {
                UseSystemColors = false;
                _bg = bg; _iconMargin = iconMargin; _border = border;
                _hover = hover; _accent = accent;
            }

            public override System.Drawing.Color ToolStripDropDownBackground => _bg;
            public override System.Drawing.Color MenuStripGradientBegin      => _bg;
            public override System.Drawing.Color MenuStripGradientEnd        => _bg;
            public override System.Drawing.Color MenuBorder => _border;
            public override System.Drawing.Color MenuItemSelected                => _hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin   => _hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd     => _hover;
            public override System.Drawing.Color MenuItemBorder                  => _accent;
            public override System.Drawing.Color MenuItemPressedGradientBegin    => _hover;
            public override System.Drawing.Color MenuItemPressedGradientMiddle   => _hover;
            public override System.Drawing.Color MenuItemPressedGradientEnd      => _hover;
            public override System.Drawing.Color ImageMarginGradientBegin => _iconMargin;
            public override System.Drawing.Color ImageMarginGradientMiddle => _iconMargin;
            public override System.Drawing.Color ImageMarginGradientEnd   => _iconMargin;
            public override System.Drawing.Color SeparatorDark  => _border;
            public override System.Drawing.Color SeparatorLight => System.Drawing.Color.Transparent;
        }

        private sealed class ThemedTrayRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
        {
            private readonly System.Drawing.Color _text;
            private readonly System.Drawing.Color _dimText;

            public ThemedTrayRenderer(ThemeColorTable table, System.Drawing.Color text, System.Drawing.Color dimText)
                : base(table)
            {
                RoundedEdges = false;
                _text = text;
                _dimText = dimText;
            }

            protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? _text : _dimText;
                base.OnRenderItemText(e);
            }
        }
    }
}
