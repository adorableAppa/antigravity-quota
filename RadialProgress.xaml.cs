using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AntigravityQuota
{
    public partial class RadialProgress : UserControl
    {
        public static readonly DependencyProperty PercentageProperty =
            DependencyProperty.Register(nameof(Percentage), typeof(double), typeof(RadialProgress),
                new PropertyMetadata(0.0, OnVisualPropertyChanged));

        public static readonly DependencyProperty StrokeColorProperty =
            DependencyProperty.Register(nameof(StrokeColor), typeof(Color), typeof(RadialProgress),
                new PropertyMetadata(Colors.DodgerBlue, OnVisualPropertyChanged));

        public double Percentage
        {
            get => (double)GetValue(PercentageProperty);
            set => SetValue(PercentageProperty, value);
        }

        public Color StrokeColor
        {
            get => (Color)GetValue(StrokeColorProperty);
            set => SetValue(StrokeColorProperty, value);
        }

        public RadialProgress()
        {
            InitializeComponent();
            Loaded += (s, e) => Redraw();
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RadialProgress control)
            {
                control.Redraw();
            }
        }

        public void Redraw()
        {
            if (!IsLoaded || ArcPath == null || TrackEllipse == null) return;

            // Update track ring opacity based on current theme
            var currentTheme = ModernWpf.ThemeManager.GetActualTheme(this);
            TrackEllipse.Stroke = currentTheme == ModernWpf.ElementTheme.Light
                ? new SolidColorBrush(Color.FromArgb(20, 0, 0, 0))
                : new SolidColorBrush(Color.FromArgb(15, 255, 255, 255));

            double pct = Math.Clamp(Percentage, 0.0, 1.0);
            PercentText.Text = $"{(int)Math.Round(pct * 100)}%";

            if (pct <= 0.0)
            {
                ArcPath.Data = null;
                return;
            }

            // Arc geometry: 120×120 control, 108×108 ellipse → radius = 54, center = (60, 60)
            double radius = 54.0;
            double cx = 60.0, cy = 60.0;
            double angleDeg = pct * 360.0;

            // Handle full circle (ArcSegment can't draw a 360° arc)
            if (pct >= 1.0)
            {
                // Draw as a full ellipse geometry
                ArcPath.Data = new EllipseGeometry(new Point(cx, cy), radius, radius);
                ArcPath.Stroke = new SolidColorBrush(StrokeColor);
                return;
            }

            bool isLargeArc = angleDeg > 180.0;
            double angleRad = angleDeg * Math.PI / 180.0;

            // Start at 12 o'clock (top center)
            var startPoint = new Point(cx, cy - radius);
            var endPoint = new Point(
                cx + radius * Math.Sin(angleRad),
                cy - radius * Math.Cos(angleRad));

            var figure = new PathFigure
            {
                StartPoint = startPoint,
                IsClosed = false
            };
            figure.Segments.Add(new ArcSegment(
                endPoint,
                new Size(radius, radius),
                0,
                isLargeArc,
                SweepDirection.Clockwise,
                true));

            ArcPath.Data = new PathGeometry(new[] { figure });
            ArcPath.Stroke = new SolidColorBrush(StrokeColor);
        }
    }
}
