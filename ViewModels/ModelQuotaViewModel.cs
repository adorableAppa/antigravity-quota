using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AntigravityQuota
{
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
                if (t.Days >= 1)
                {
                    return $"{t.Days}d {t.Hours}h {t.Minutes}m {t.Seconds}s";
                }
                if (t.Hours >= 1)
                {
                    return $"{t.Hours}h {t.Minutes}m {t.Seconds}s";
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
