using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace AntigravityQuota
{
    public partial class MainWindow : Window
    {
        private readonly OAuthServer _oauthServer;
        private readonly QuotaService _quotaService;
        private readonly DispatcherTimer _tickTimer;
        private readonly List<ModelQuotaViewModel> _modelViewModels = new();
        private QuotaSnapshot? _currentSnapshot;

        public MainWindow()
        {
            InitializeComponent();
            
            _quotaService = new QuotaService();
            _oauthServer = new OAuthServer(OnLoginSuccess);

            // Ticks every second to animate countdown timers
            _tickTimer = new DispatcherTimer();
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.Tick += OnTick;
            _tickTimer.Start();

            _oauthServer.Start();
            LoadAccountsAndStatus();
            _ = SyncQuotaAsync(false);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _tickTimer.Stop();
            _oauthServer.Stop();
            base.OnClosing(e);
        }

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
            int accountCount = accounts.Count;

            ActiveEmailText.Text = string.IsNullOrEmpty(active) ? "Not Logged In" : active;

            // Show/hide account manager button based on account count
            ManageAccountsButton.Visibility = accountCount > 1 ? Visibility.Visible : Visibility.Collapsed;

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
                ServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Emerald-400
            }
            else
            {
                ServiceStatusText.Text = "Disconnected (Local Offline)";
                ServiceStatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)); // Red-400
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

        private void UpdateUI(QuotaSnapshot snapshot)
        {
            // 1. Update Credits
            if (snapshot.PromptCredits != null)
            {
                CreditsRadial.Percentage = snapshot.PromptCredits.RemainingPercentage;
                CreditsRadial.StrokeColor = Color.FromRgb(6, 182, 212); // Cyan-500
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
            StatusBadge.Background = isLocal 
                ? new SolidColorBrush(Color.FromArgb(25, 52, 211, 153)) 
                : new SolidColorBrush(Color.FromArgb(25, 139, 92, 246));
            StatusBadge.BorderBrush = isLocal 
                ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) 
                : new SolidColorBrush(Color.FromRgb(139, 92, 246));
            StatusBadgeText.Foreground = isLocal 
                ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) 
                : new SolidColorBrush(Color.FromRgb(167, 139, 250));

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

        private void OnTick(object? sender, EventArgs e)
        {
            foreach (var vm in _modelViewModels)
            {
                vm.Tick();
                vm.NotifyPropertyChanged(nameof(ModelQuotaViewModel.DisplayTimeLeft));
                vm.NotifyPropertyChanged(nameof(ModelQuotaViewModel.TimerColorBrush));
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
            AccountsListControl.ItemsSource = ConfigService.ListAccounts();
            AccountsModal.Visibility = Visibility.Visible;
        }

        private void OnManageAccountsBorderClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OnManageAccountsClicked(sender, e);
        }

        private void OnCloseAccountsModalClicked(object sender, RoutedEventArgs e)
        {
            AccountsModal.Visibility = Visibility.Collapsed;
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
    }

    public class ModelQuotaViewModel : INotifyPropertyChanged
    {
        public ModelQuota Model { get; }
        private readonly bool _isLocal;

        public string Label => Model.Label;
        public string ModelId => Model.ModelId;
        public bool IsAutocompleteOnly => Model.IsAutocompleteOnly;

        public string DisplayPercentage => Model.IsExhausted 
            ? "0% Remaining" 
            : $"{(int)Math.Round((Model.RemainingPercentage ?? 1.0) * 100)}% Available";

        public Brush TextColorBrush => Model.IsExhausted
            ? new SolidColorBrush(Color.FromRgb(248, 113, 113))
            : new SolidColorBrush(Colors.White);

        public double ProgressBarValue => Model.IsExhausted ? 0.0 : (Model.RemainingPercentage ?? 1.0) * 100;

        public Brush ProgressBrush
        {
            get
            {
                if (Model.IsExhausted) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                double pct = (Model.RemainingPercentage ?? 1.0) * 100;
                if (pct <= 20) return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                if (pct <= 50) return new SolidColorBrush(Color.FromRgb(251, 191, 36));

                var lgb = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 0)
                };
                lgb.GradientStops.Add(new GradientStop(Color.FromRgb(139, 92, 246), 0));
                lgb.GradientStops.Add(new GradientStop(Color.FromRgb(6, 182, 212), 1));
                return lgb;
            }
        }

        public string DisplayTimeLeft
        {
            get
            {
                if (Model.ResetTime == null) return "No Limit Set";
                if (Model.TimeUntilResetMs <= 0) return "Ready / Reset";

                TimeSpan t = TimeSpan.FromMilliseconds(Model.TimeUntilResetMs);
                if (t.TotalHours >= 1)
                {
                    return $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";
                }
                return $"{t.Minutes}m {t.Seconds}s";
            }
        }

        public Brush TimerColorBrush
        {
            get
            {
                if (Model.ResetTime != null && Model.TimeUntilResetMs > 0 && Model.TimeUntilResetMs < 15 * 60 * 1000)
                {
                    return new SolidColorBrush(Color.FromRgb(251, 113, 133));
                }
                return new SolidColorBrush(Color.FromRgb(142, 142, 147));
            }
        }

        // Monitor vs Cloud icon geometry path
        public string MethodIconPath => _isLocal
            ? "M2,3 H22 V17 H2 Z M8,21 H16 M12,17 V21"
            : "M12,2 A10,10 0 1,0 22,12 H12 Z";

        public ModelQuotaViewModel(ModelQuota model, bool isLocal)
        {
            Model = model;
            _isLocal = isLocal;
        }

        public void Tick()
        {
            if (Model.TimeUntilResetMs > 0)
            {
                Model.TimeUntilResetMs = Math.Max(0, Model.TimeUntilResetMs - 1000);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
