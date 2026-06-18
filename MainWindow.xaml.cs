using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AntigravityQuota
{
    public partial class MainWindow : Window
    {
        public const string AppVersion = "1.3.0";

        private GitHubRelease? _latestRelease;
        private bool _isExplicitShutdown = false;

        private readonly OAuthServer _oauthServer;
        private readonly QuotaService _quotaService;
        private readonly TrayService _trayService;
        private readonly SyncTimerService _timerService;
        private readonly List<ModelQuotaViewModel> _modelViewModels = new();
        private QuotaSnapshot? _currentSnapshot;

        public MainWindow()
        {
            InitializeComponent();
            AppVersionText.Text = $"v{AppVersion}";

            var config = ConfigService.LoadGlobalConfig();
            ApplyTheme(config.theme ?? "Mocha");

            MinimizeToTrayToggle.IsOn = config.minimizeToTray;
            CloseToTrayToggle.IsOn = config.closeToTray;
            StartWithWindowsToggle.IsOn = config.startWithWindows;
            NotificationsToggle.IsOn = config.notificationsEnabled;
            SelectSyncIntervalItem(config.syncIntervalMinutes);
            
            _quotaService = new QuotaService();
            _oauthServer = new OAuthServer(OnLoginSuccess);

            // Initialize services
            _trayService = new TrayService();
            _trayService.RestoreRequested += RestoreWindow;
            _trayService.ShutdownRequested += ShutdownApp;
            _trayService.Initialize();

            _timerService = new SyncTimerService();
            _timerService.TickElapsed += OnTick;
            _timerService.SyncRequested += () => SyncQuotaAsync(true);
            _timerService.Start(config.syncIntervalMinutes);

            _oauthServer.Start();
            LoadAccountsAndStatus();
            _ = SyncQuotaAsync(false);
            _ = CheckForUpdatesAsync();

            AutostartService.VerifyAndUpdateAutostart();
        }

        // ── Window Lifecycle ───────────────────────────────────────────────

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (WindowState == WindowState.Minimized)
            {
                var config = ConfigService.LoadGlobalConfig();
                if (config.minimizeToTray)
                {
                    this.Hide();
                }
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            var config = ConfigService.LoadGlobalConfig();
            if (!_isExplicitShutdown && config.closeToTray)
            {
                e.Cancel = true;
                this.Hide();
                _trayService.ShowBackgroundBalloon();
            }
            else
            {
                _timerService.Dispose();
                _oauthServer.Stop();
                _trayService.Dispose();
                base.OnClosing(e);
            }
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && e.Key == System.Windows.Input.Key.R) || 
                e.Key == System.Windows.Input.Key.F5)
            {
                e.Handled = true;
                _ = SyncQuotaAsync(true);
            }
        }

        private void RestoreWindow()
        {
            this.Show();
            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
            this.Activate();
        }

        private void ShutdownApp()
        {
            _isExplicitShutdown = true;
            this.Close();
        }

        // ── Core Data Flow ─────────────────────────────────────────────────

        private void OnLoginSuccess()
        {
            Dispatcher.Invoke(async () =>
            {
                LoadAccountsAndStatus();
                await SyncQuotaAsync(true);
            });
        }

        private void LoadAccountsAndStatus()
        {
            var config = ConfigService.LoadGlobalConfig();
            string? active = config.activeAccount;
            var accounts = ConfigService.ListAccounts();

            ActiveEmailText.Text = string.IsNullOrEmpty(active) ? "Not Logged In" : active;

            if (AccountsListControl != null)
            {
                AccountsListControl.ItemsSource = accounts;
            }

            // Check if Language Server is running
            bool isLspRunning = false;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name LIKE '%antigravity%' OR CommandLine LIKE '%antigravity%'"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        string? cmdLine = obj["CommandLine"]?.ToString();
                        if (cmdLine != null && (cmdLine.ToLower().Contains("language_server") || cmdLine.ToLower().Contains("lsp") || cmdLine.ToLower().Contains("codeium")))
                        {
                            isLspRunning = true;
                            break;
                        }
                    }
                }
            }
            catch {}

            if (isLspRunning)
            {
                ServiceStatusText.Text = "Active (Connected)";
                ServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else
            {
                ServiceStatusText.Text = "Disconnected (Local Offline)";
                ServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            }
        }

        private async Task SyncQuotaAsync(bool forceRefresh)
        {
            SyncButton.IsEnabled = false;
            EmptyStateCard.Visibility = Visibility.Collapsed;
            ModelsGridControl.Visibility = Visibility.Collapsed;

            string method = "google";

            try
            {
                var config = ConfigService.LoadGlobalConfig();
                _currentSnapshot = await _quotaService.FetchQuotaAsync(method, config.activeAccount);
                UpdateUI(_currentSnapshot);
                _trayService.UpdateMenu(_currentSnapshot);
                _trayService.CheckAndNotify(_currentSnapshot, config.notificationsEnabled);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Sync Failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                EmptyStateCard.Visibility = Visibility.Visible;
            }
            finally
            {
                SyncButton.IsEnabled = true;
            }
        }

        // ── UI Update ──────────────────────────────────────────────────────

        private void UpdateUI(QuotaSnapshot snapshot)
        {
            // 1. Update Credits
            if (snapshot.PromptCredits != null)
            {
                CreditsRadial.Percentage = snapshot.PromptCredits.RemainingPercentage;
                CreditsRadial.StrokeColor = (Resources["ProgressGradientEndColor"] as Color?) ?? Color.FromRgb(6, 182, 212);
                CreditsAvailableText.Text = snapshot.PromptCredits.Available.ToString("N0");
                CreditsMonthlyText.Text = snapshot.PromptCredits.Monthly.ToString("N0");
            }
            else
            {
                CreditsRadial.Percentage = 0.0;
                CreditsRadial.StrokeColor = Color.FromRgb(142, 142, 147);
                CreditsAvailableText.Text = "Unlimited";
                CreditsMonthlyText.Text = "Unlimited";
            }

            // 2. Update Sync Card
            LastCheckedText.Text = DateTime.Now.ToString("T");
            PlanTypeText.Text = snapshot.PlanType;

            bool isLocal = snapshot.Method == "local";
            StatusBadgeText.Text = isLocal ? "Local Connection" : "Cloud Connection";

            var greenColor = (Resources["ProgressGreenColor"] as Color?) ?? Color.FromRgb(52, 211, 153);
            var cloudColor = (Resources["ProgressGradientStartColor"] as Color?) ?? Color.FromRgb(139, 92, 246);

            StatusBadge.Background = isLocal 
                ? new SolidColorBrush(Color.FromArgb(25, greenColor.R, greenColor.G, greenColor.B)) 
                : new SolidColorBrush(Color.FromArgb(25, cloudColor.R, cloudColor.G, cloudColor.B));
            StatusBadge.BorderBrush = new SolidColorBrush(isLocal ? greenColor : cloudColor);
            StatusBadgeText.Foreground = new SolidColorBrush(isLocal ? greenColor : cloudColor);

            // 3. Populate Models Grid
            _modelViewModels.Clear();
            bool showAutocomplete = AutocompleteToggle.IsOn;

            foreach (var m in snapshot.Models)
            {
                if (!showAutocomplete && m.IsAutocompleteOnly) continue;
                _modelViewModels.Add(new ModelQuotaViewModel(m, isLocal));
            }

            ModelsGridControl.ItemsSource = null;
            if (_modelViewModels.Count > 0)
            {
                ModelsGridControl.ItemsSource = _modelViewModels;
                ModelsGridControl.Visibility = Visibility.Visible;
                EmptyStateCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyStateCard.Visibility = Visibility.Visible;
            }

            ModelCountText.Text = $"{_modelViewModels.Count} model(s) tracked";
        }

        private void OnTick()
        {
            foreach (var vm in _modelViewModels)
            {
                vm.Tick();
                vm.NotifyPropertyChanged(nameof(ModelQuotaViewModel.DisplayTimeLeft));
                vm.NotifyPropertyChanged(nameof(ModelQuotaViewModel.TimerColorBrush));
            }
        }

        // ── Theme ──────────────────────────────────────────────────────────

        private void ApplyTheme(string? theme)
        {
            var elementTheme = (theme != null && 
                                (theme.ToLower().Contains("latte") || 
                                 theme.ToLower().Contains("latté") ||
                                 theme.ToLower().Contains("alucard") ||
                                 theme.ToLower().Contains("gruvbox light") ||
                                 theme.ToLower().Contains("nord light") ||
                                 theme.ToLower().Contains("rosé pine dawn") ||
                                 theme.ToLower().Contains("rose pine dawn")))
                ? ModernWpf.ElementTheme.Light
                : ModernWpf.ElementTheme.Dark;
            ModernWpf.ThemeManager.SetRequestedTheme(this, elementTheme);

            SetThemeBrushes(theme);

            // Highlight active theme card
            if (MochaCard != null && MacchiatoCard != null && FrappeCard != null && LatteCard != null)
            {
                MochaCard.BorderBrush = IsMatchingTheme(theme, "Catppuchin Mocha") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                MochaCard.BorderThickness = IsMatchingTheme(theme, "Catppuchin Mocha") ? new Thickness(2) : new Thickness(1);

                MacchiatoCard.BorderBrush = IsMatchingTheme(theme, "Catppuchin Macchiato") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                MacchiatoCard.BorderThickness = IsMatchingTheme(theme, "Catppuchin Macchiato") ? new Thickness(2) : new Thickness(1);

                FrappeCard.BorderBrush = IsMatchingTheme(theme, "Catppuchin Frappé") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                FrappeCard.BorderThickness = IsMatchingTheme(theme, "Catppuchin Frappé") ? new Thickness(2) : new Thickness(1);

                LatteCard.BorderBrush = IsMatchingTheme(theme, "Catppuchin Latté") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                LatteCard.BorderThickness = IsMatchingTheme(theme, "Catppuchin Latté") ? new Thickness(2) : new Thickness(1);
            }

            if (DraculaClassicCard != null && AlucardClassicCard != null)
            {
                DraculaClassicCard.BorderBrush = IsMatchingTheme(theme, "Dracula Classic") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                DraculaClassicCard.BorderThickness = IsMatchingTheme(theme, "Dracula Classic") ? new Thickness(2) : new Thickness(1);

                AlucardClassicCard.BorderBrush = IsMatchingTheme(theme, "Alucard Classic") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                AlucardClassicCard.BorderThickness = IsMatchingTheme(theme, "Alucard Classic") ? new Thickness(2) : new Thickness(1);
            }

            if (GruvboxDarkCard != null && GruvboxLightCard != null)
            {
                GruvboxDarkCard.BorderBrush = IsMatchingTheme(theme, "Gruvbox Dark") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                GruvboxDarkCard.BorderThickness = IsMatchingTheme(theme, "Gruvbox Dark") ? new Thickness(2) : new Thickness(1);

                GruvboxLightCard.BorderBrush = IsMatchingTheme(theme, "Gruvbox Light") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                GruvboxLightCard.BorderThickness = IsMatchingTheme(theme, "Gruvbox Light") ? new Thickness(2) : new Thickness(1);
            }

            if (NordCard != null)
            {
                NordCard.BorderBrush = IsMatchingTheme(theme, "Nord") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                NordCard.BorderThickness = IsMatchingTheme(theme, "Nord") ? new Thickness(2) : new Thickness(1);
            }

            if (NordLightCard != null)
            {
                NordLightCard.BorderBrush = IsMatchingTheme(theme, "Nord Light") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                NordLightCard.BorderThickness = IsMatchingTheme(theme, "Nord Light") ? new Thickness(2) : new Thickness(1);
            }

            if (RosePineCard != null)
            {
                RosePineCard.BorderBrush = IsMatchingTheme(theme, "Rosé Pine") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                RosePineCard.BorderThickness = IsMatchingTheme(theme, "Rosé Pine") ? new Thickness(2) : new Thickness(1);
            }

            if (RosePineMoonCard != null)
            {
                RosePineMoonCard.BorderBrush = IsMatchingTheme(theme, "Rosé Pine Moon") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                RosePineMoonCard.BorderThickness = IsMatchingTheme(theme, "Rosé Pine Moon") ? new Thickness(2) : new Thickness(1);
            }

            if (RosePineDawnCard != null)
            {
                RosePineDawnCard.BorderBrush = IsMatchingTheme(theme, "Rosé Pine Dawn") ? Resources["ProgressGradientStartBrush"] as Brush : Resources["CardBorderBrush"] as Brush;
                RosePineDawnCard.BorderThickness = IsMatchingTheme(theme, "Rosé Pine Dawn") ? new Thickness(2) : new Thickness(1);
            }

            // Redraw radial progress with new theme track color
            CreditsRadial?.Redraw();

            // Refresh status badge programmatically if a snapshot is loaded
            if (_currentSnapshot != null)
            {
                UpdateUI(_currentSnapshot);
            }
        }

        private bool IsMatchingTheme(string? configTheme, string? tagTheme)
        {
            if (configTheme == tagTheme) return true;
            if (string.IsNullOrEmpty(configTheme) || string.IsNullOrEmpty(tagTheme)) return false;

            string normalize(string s) => s.ToLower()
                .Replace("catppuchin", "")
                .Replace("catppuccin", "")
                .Replace(" ", "")
                .Replace("é", "e")
                .Replace("á", "a");

            return normalize(configTheme) == normalize(tagTheme);
        }

        private void SetThemeBrushes(string? theme)
        {
            string normalized = (theme ?? "").ToLower();
            if (normalized.Contains("latte") || normalized.Contains("latté"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0xDC, 0xE0, 0xE8));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEF, 0xF1, 0xF5));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xDA));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEF));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEF));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xDA));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x4F, 0x69));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x6C, 0x6F, 0x85));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xD9, 0xDC, 0xE0, 0xE8));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xDA));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xDA));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0x88, 0x39, 0xEF));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x04, 0xA5, 0xE5));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xD2, 0x0F, 0x39));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xFE, 0x64, 0x0B));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0x88, 0x39, 0xEF);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x04, 0xA5, 0xE5);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x40, 0xA0, 0x2B);
            }
            else if (normalized.Contains("frappé") || normalized.Contains("frappe"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x26, 0x34));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x30, 0x34, 0x46));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x41, 0x45, 0x59));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x29, 0x2C, 0x3C));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x29, 0x2C, 0x3C));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x41, 0x45, 0x59));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xC6, 0xD0, 0xF5));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0xA5, 0xAD, 0xCE));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x23, 0x26, 0x34));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x41, 0x45, 0x59));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x41, 0x45, 0x59));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xCA, 0x9E, 0xE6));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x99, 0xD1, 0xDB));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xE7, 0x82, 0x84));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xEF, 0x9F, 0x76));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xCA, 0x9E, 0xE6);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x99, 0xD1, 0xDB);
                Resources["ProgressGreenColor"] = Color.FromRgb(0xA6, 0xD1, 0x89);
            }
            else if (normalized.Contains("macchiato"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x18, 0x19, 0x26));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x3A));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x36, 0x3A, 0x4F));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x30));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x30));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x36, 0x3A, 0x4F));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xCA, 0xD3, 0xF5));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0xA5, 0xAD, 0xCB));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x18, 0x19, 0x26));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x36, 0x3A, 0x4F));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x36, 0x3A, 0x4F));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xC6, 0xA0, 0xF6));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x91, 0xD7, 0xE3));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xED, 0x87, 0x96));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xA9, 0x7F));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xC6, 0xA0, 0xF6);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x91, 0xD7, 0xE3);
                Resources["ProgressGreenColor"] = Color.FromRgb(0xA6, 0xDA, 0x95);
            }
            else if (normalized.Contains("dracula"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x29));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x2A, 0x36));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x21));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x19, 0x1A, 0x21));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xF8, 0xF8, 0xF2));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x62, 0x72, 0xA4));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x1E, 0x1F, 0x29));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x5A));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xBD, 0x93, 0xF9));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x8B, 0xE9, 0xFD));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x6C));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xBD, 0x93, 0xF9);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x8B, 0xE9, 0xFD);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x50, 0xFA, 0x7B);
            }
            else if (normalized.Contains("alucard"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF3, 0xEF, 0xE0));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE2, 0xD3));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEA, 0xE6, 0xD5));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEA, 0xE6, 0xD5));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xDE));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x6C, 0x66, 0x4B));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFB, 0xEB));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEA, 0xE6, 0xD5));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xDE));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0x64, 0x4A, 0xC9));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x03, 0x6A, 0x96));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0x3A, 0x2A));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xA3, 0x4D, 0x14));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0x64, 0x4A, 0xC9);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x03, 0x6A, 0x96);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x14, 0x71, 0x0A);
            }
            else if (normalized.Contains("gruvbox dark"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1D, 0x20, 0x21));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x50, 0x49, 0x45));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x32, 0x30, 0x2F));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x32, 0x30, 0x2F));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x50, 0x49, 0x45));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0xF1, 0xC7));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x92, 0x83, 0x74));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x1D, 0x20, 0x21));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x3C, 0x38, 0x36));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x50, 0x49, 0x45));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xFA, 0xBD, 0x2F));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x83, 0xA5, 0x98));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0x49, 0x34));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xFE, 0x80, 0x19));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xFA, 0xBD, 0x2F);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x83, 0xA5, 0x98);
                Resources["ProgressGreenColor"] = Color.FromRgb(0xB8, 0xBB, 0x26);
            }
            else if (normalized.Contains("gruvbox light"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xF5, 0xD7));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0xF1, 0xC7));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xEB, 0xDB, 0xB2));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xE5, 0xBC));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xE5, 0xBC));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xEB, 0xDB, 0xB2));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x92, 0x83, 0x74));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xD0, 0xFB, 0xF1, 0xC7));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xE5, 0xBC));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xEB, 0xDB, 0xB2));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xB5, 0x76, 0x14));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x07, 0x66, 0x78));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0x9D, 0x00, 0x06));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xAF, 0x3A, 0x03));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xB5, 0x76, 0x14);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x07, 0x66, 0x78);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x79, 0x74, 0x0E);
            }
            else if (normalized.Contains("nord light"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE9, 0xF0));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE9, 0xF0));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE9, 0xF0));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xD0, 0xE5, 0xE9, 0xF0));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0x5E, 0x81, 0xAC));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x81, 0xA1, 0xC1));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xBF, 0x61, 0x6A));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xD0, 0x87, 0x70));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0x5E, 0x81, 0xAC);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x81, 0xA1, 0xC1);
                Resources["ProgressGreenColor"] = Color.FromRgb(0xA3, 0xBE, 0x8C);
            }
            else if (normalized.Contains("nord"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x3B, 0x42, 0x52));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEF, 0xF4));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE9));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x2E, 0x34, 0x40));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x43, 0x4C, 0x5E));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4C, 0x56, 0x6A));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0x88, 0xC0, 0xD0));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x81, 0xA1, 0xC1));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xBF, 0x61, 0x6A));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xD0, 0x87, 0x70));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0x88, 0xC0, 0xD0);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x81, 0xA1, 0xC1);
                Resources["ProgressGreenColor"] = Color.FromRgb(0xA3, 0xBE, 0x8C);
            }
            else if (normalized.Contains("rosé pine moon") || normalized.Contains("rose pine moon"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1B, 0x19, 0x29));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x23, 0x21, 0x36));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x39, 0x35, 0x52));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2A, 0x27, 0x3F));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2A, 0x27, 0x3F));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x39, 0x35, 0x52));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xDE, 0xF4));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x90, 0x8C, 0xAA));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x1B, 0x19, 0x29));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x39, 0x35, 0x52));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x39, 0x35, 0x52));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xC4, 0xA7, 0xE7));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x3E, 0x8F, 0xB0));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xEB, 0x6F, 0x92));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xF6, 0xC1, 0x77));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xC4, 0xA7, 0xE7);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x3E, 0x8F, 0xB0);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x3E, 0x8F, 0xB0);
            }
            else if (normalized.Contains("rosé pine dawn") || normalized.Contains("rose pine dawn"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xE9, 0xE1));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFA, 0xF4, 0xED));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE4, 0xDB, 0xD2));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFA, 0xF3));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFA, 0xF3));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE4, 0xDB, 0xD2));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0x57, 0x52, 0x79));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x98, 0x93, 0xA5));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xD0, 0xF2, 0xE9, 0xE1));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFA, 0xF3));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE4, 0xDB, 0xD2));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0x90, 0x7A, 0xA9));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x28, 0x69, 0x83));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xB8, 0x56, 0x5A));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xEA, 0x9D, 0x34));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0x90, 0x7A, 0xA9);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x28, 0x69, 0x83);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x28, 0x69, 0x83);
            }
            else if (normalized.Contains("rosé pine") || normalized.Contains("rose pine"))
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x12, 0x10, 0x1A));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x19, 0x17, 0x24));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x3A));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1F, 0x1D, 0x2E));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1F, 0x1D, 0x2E));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x3A));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xE0, 0xDE, 0xF4));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0x90, 0x8C, 0xAA));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x12, 0x10, 0x1A));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x3A));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x3A));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xC4, 0xA7, 0xE7));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x31, 0x74, 0x8F));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xEB, 0x6F, 0x92));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xF6, 0xC1, 0x77));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xC4, 0xA7, 0xE7);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x31, 0x74, 0x8F);
                Resources["ProgressGreenColor"] = Color.FromRgb(0x31, 0x74, 0x8F);
            }
            else // Default to Mocha
            {
                Resources["WindowBgBrush"] = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x1B));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
                Resources["HeaderButtonBgBrush"] = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
                Resources["AccountCapsuleBgBrush"] = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
                Resources["AccountCapsuleBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
                Resources["TextTitleBrush"] = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
                Resources["TextSubtitleBrush"] = new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8));
                Resources["ModalBgBrush"] = new SolidColorBrush(Color.FromArgb(0xC0, 0x11, 0x11, 0x1B));
                Resources["AccountItemBgBrush"] = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
                Resources["AccountItemBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));

                Resources["ProgressGradientStartBrush"] = new SolidColorBrush(Color.FromRgb(0xCB, 0xA6, 0xF7));
                Resources["ProgressGradientEndBrush"] = new SolidColorBrush(Color.FromRgb(0x89, 0xDC, 0xEB));
                Resources["ProgressExhaustedBrush"] = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8));
                Resources["ProgressWarningBrush"] = new SolidColorBrush(Color.FromRgb(0xFA, 0xB3, 0x87));

                Resources["ProgressGradientStartColor"] = Color.FromRgb(0xCB, 0xA6, 0xF7);
                Resources["ProgressGradientEndColor"] = Color.FromRgb(0x89, 0xDC, 0xEB);
                Resources["ProgressGreenColor"] = Color.FromRgb(0xA6, 0xE3, 0xA1);
            }
        }

        // ── Settings Event Handlers ────────────────────────────────────────

        private void OnSettingsButtonClicked(object sender, RoutedEventArgs e)
        {
            SwitchToSettingsTab("General");
            SettingsModal.Visibility = Visibility.Visible;
        }

        private void OnCloseSettingsModalClicked(object sender, RoutedEventArgs e)
        {
            SettingsModal.Visibility = Visibility.Collapsed;
        }

        private void OnGeneralTabClicked(object sender, RoutedEventArgs e)
        {
            SwitchToSettingsTab("General");
        }

        private void OnAppearanceTabClicked(object sender, RoutedEventArgs e)
        {
            SwitchToSettingsTab("Appearance");
        }

        private void OnAccountsTabClicked(object sender, RoutedEventArgs e)
        {
            SwitchToSettingsTab("Accounts");
        }

        private void OnAboutTabClicked(object sender, RoutedEventArgs e)
        {
            SwitchToSettingsTab("About");
        }

        private void OnVisitWebsiteClicked(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://ephf.de");
        }

        private void OnViewRepositoryClicked(object sender, RoutedEventArgs e)
        {
            OpenUrl("https://github.com/adorableAppa/antigravity-quota");
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open URL: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SwitchToSettingsTab(string tabName)
        {
            if (tabName == "General")
            {
                SettingsTabTitleText.Text = "General";
                GeneralSettingsView.Visibility = Visibility.Visible;
                AppearanceSettingsView.Visibility = Visibility.Collapsed;
                AccountsSettingsView.Visibility = Visibility.Collapsed;
                AboutSettingsView.Visibility = Visibility.Collapsed;
                
                GeneralTabBtn.Opacity = 1.0;
                AppearanceTabBtn.Opacity = 0.6;
                AccountsTabBtn.Opacity = 0.6;
                AboutTabBtn.Opacity = 0.6;
            }
            else if (tabName == "Appearance")
            {
                SettingsTabTitleText.Text = "Appearance";
                GeneralSettingsView.Visibility = Visibility.Collapsed;
                AppearanceSettingsView.Visibility = Visibility.Visible;
                AccountsSettingsView.Visibility = Visibility.Collapsed;
                AboutSettingsView.Visibility = Visibility.Collapsed;
                
                GeneralTabBtn.Opacity = 0.6;
                AppearanceTabBtn.Opacity = 1.0;
                AccountsTabBtn.Opacity = 0.6;
                AboutTabBtn.Opacity = 0.6;
            }
            else if (tabName == "Accounts")
            {
                SettingsTabTitleText.Text = "Accounts";
                GeneralSettingsView.Visibility = Visibility.Collapsed;
                AppearanceSettingsView.Visibility = Visibility.Collapsed;
                AccountsSettingsView.Visibility = Visibility.Visible;
                AboutSettingsView.Visibility = Visibility.Collapsed;
                
                GeneralTabBtn.Opacity = 0.6;
                AppearanceTabBtn.Opacity = 0.6;
                AccountsTabBtn.Opacity = 1.0;
                AboutTabBtn.Opacity = 0.6;
                
                AccountsListControl.ItemsSource = ConfigService.ListAccounts();
            }
            else if (tabName == "About")
            {
                SettingsTabTitleText.Text = "About";
                GeneralSettingsView.Visibility = Visibility.Collapsed;
                AppearanceSettingsView.Visibility = Visibility.Collapsed;
                AccountsSettingsView.Visibility = Visibility.Collapsed;
                AboutSettingsView.Visibility = Visibility.Visible;
                
                GeneralTabBtn.Opacity = 0.6;
                AppearanceTabBtn.Opacity = 0.6;
                AccountsTabBtn.Opacity = 0.6;
                AboutTabBtn.Opacity = 1.0;
            }
        }

        private void OnThemeCardClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border card && card.Tag is string newTheme)
            {
                var config = ConfigService.LoadGlobalConfig();
                if (config.theme != newTheme)
                {
                    config.theme = newTheme;
                    ConfigService.SaveGlobalConfig(config);
                    ApplyTheme(newTheme);
                }
            }
        }

        private async void OnSyncButtonClicked(object sender, RoutedEventArgs e)
        {
            await SyncQuotaAsync(true);
        }

        private async void OnConfigChanged(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                await SyncQuotaAsync(false);
            }
        }

        private void OnManageAccountsClicked(object sender, RoutedEventArgs e)
        {
            SwitchToSettingsTab("Accounts");
            SettingsModal.Visibility = Visibility.Visible;
        }

        private void OnManageAccountsBorderClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OnManageAccountsClicked(sender, e);
        }

        private void OnOAuthConnectClicked(object sender, RoutedEventArgs e)
        {
            _oauthServer.TriggerLoginFlow();
        }

        private async void OnSwitchAccountClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string email)
            {
                ConfigService.SwitchAccount(email);
                LoadAccountsAndStatus();
                AccountsListControl.ItemsSource = ConfigService.ListAccounts();
                await SyncQuotaAsync(true);
            }
        }

        private async void OnRemoveAccountClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string email)
            {
                var result = MessageBox.Show($"Are you sure you want to log out and remove credentials for {email}?", 
                    "Log Out", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    ConfigService.RemoveAccount(email);
                    LoadAccountsAndStatus();
                    AccountsListControl.ItemsSource = ConfigService.ListAccounts();
                    await SyncQuotaAsync(true);
                }
            }
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var updateService = new UpdateService();
                var release = await updateService.CheckForUpdatesAsync("adorableAppa", "antigravity-quota");
                if (release != null && UpdateService.IsNewerVersion(AppVersion, release.tag_name))
                {
                    _latestRelease = release;
                    UpdateTitleText.Text = $"Update Available! ({release.tag_name})";
                    UpdateDescriptionText.Text = string.IsNullOrEmpty(release.name) 
                        ? $"A new version {release.tag_name} is available on GitHub." 
                        : $"A new version {release.tag_name} is available: {release.name}";
                    UpdateBanner.Visibility = Visibility.Visible;
                }
            }
            catch {}
        }

        private void OnDownloadUpdateClicked(object sender, RoutedEventArgs e)
        {
            if (_latestRelease != null)
            {
                OpenUrl(_latestRelease.html_url);
            }
            else
            {
                OpenUrl("https://github.com/adorableAppa/antigravity-quota/releases");
            }
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        private void OnDismissUpdateClicked(object sender, RoutedEventArgs e)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
        }

        private void OnTraySettingsChanged(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                var config = ConfigService.LoadGlobalConfig();
                config.minimizeToTray = MinimizeToTrayToggle.IsOn;
                config.closeToTray = CloseToTrayToggle.IsOn;
                ConfigService.SaveGlobalConfig(config);
            }
        }

        private void OnNotificationsToggleChanged(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                var config = ConfigService.LoadGlobalConfig();
                config.notificationsEnabled = NotificationsToggle.IsOn;
                ConfigService.SaveGlobalConfig(config);
            }
        }

        private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
            {
                var config = ConfigService.LoadGlobalConfig();
                config.startWithWindows = StartWithWindowsToggle.IsOn;
                ConfigService.SaveGlobalConfig(config);
                AutostartService.SetAutostart(config.startWithWindows);
            }
        }

        private void OnSyncIntervalChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded && SyncIntervalComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int minutes))
            {
                var config = ConfigService.LoadGlobalConfig();
                config.syncIntervalMinutes = minutes;
                ConfigService.SaveGlobalConfig(config);
                _timerService.UpdateInterval(minutes);
            }
        }

        private void SelectSyncIntervalItem(int minutes)
        {
            if (SyncIntervalComboBox == null) return;
            foreach (ComboBoxItem item in SyncIntervalComboBox.Items)
            {
                if (int.TryParse(item.Tag?.ToString(), out int val) && val == minutes)
                {
                    SyncIntervalComboBox.SelectedItem = item;
                    break;
                }
            }
        }
    }
}
