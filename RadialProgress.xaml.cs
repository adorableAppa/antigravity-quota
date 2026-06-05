using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AntigravityQuota
{
    public partial class RadialProgress : UserControl
    {
        private WriteableBitmap? _writeableBitmap;

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

        private void Redraw()
        {
            if (!IsLoaded || RendererImage == null) return;

            int w = 120;
            int h = 120;

            if (_writeableBitmap == null)
            {
                _writeableBitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
                RendererImage.Source = _writeableBitmap;
            }

            PercentText.Text = $"{(int)Math.Round(Percentage * 100)}%";

            _writeableBitmap.Lock();
            try
            {
                IntPtr backBuffer = _writeableBitmap.BackBuffer;
                int stride = _writeableBitmap.BackBufferStride;

                // Create a destination bitmap wrapped around the WPF backbuffer
                using (var destBmp = new System.Drawing.Bitmap(w, h, stride, System.Drawing.Imaging.PixelFormat.Format32bppPArgb, backBuffer))
                {
                    // Create a source GDI+ bitmap for custom transparent rendering
                    using (var srcBmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                    {
                        using (var gSrc = System.Drawing.Graphics.FromImage(srcBmp))
                        {
                            gSrc.Clear(System.Drawing.Color.Transparent);
                            gSrc.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                            // Draw track circle (semi-transparent white)
                            using (var trackPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(15, 255, 255, 255), 8))
                            {
                                gSrc.DrawEllipse(trackPen, 6, 6, w - 12, h - 12);
                            }

                            // Draw active progress arc
                            System.Drawing.Color gdiColor = System.Drawing.Color.FromArgb(StrokeColor.A, StrokeColor.R, StrokeColor.G, StrokeColor.B);
                            using (var progressPen = new System.Drawing.Pen(gdiColor, 8))
                            {
                                progressPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                                progressPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                                float angle = (float)(Percentage * 360.0);
                                gSrc.DrawArc(progressPen, 6, 6, w - 12, h - 12, -90, angle);
                            }
                        }

                        // Use Win32 AlphaBlend to render the GDI+ source into the WriteableBitmap
                        using (var gDest = System.Drawing.Graphics.FromImage(destBmp))
                        {
                            IntPtr hdcDest = gDest.GetHdc();
                            using (var gSrcTemp = System.Drawing.Graphics.FromImage(srcBmp))
                            {
                                IntPtr hdcSrc = gSrcTemp.GetHdc();

                                GdiInterop.BLENDFUNCTION blend = new GdiInterop.BLENDFUNCTION
                                {
                                    BlendOp = GdiInterop.AC_SRC_OVER,
                                    BlendFlags = 0,
                                    SourceConstantAlpha = 255,
                                    AlphaFormat = GdiInterop.AC_SRC_ALPHA
                                };

                                GdiInterop.AlphaBlend(hdcDest, 0, 0, w, h, hdcSrc, 0, 0, w, h, blend);

                                gSrcTemp.ReleaseHdc(hdcSrc);
                            }
                            gDest.ReleaseHdc(hdcDest);
                        }
                    }
                }

                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally
            {
                _writeableBitmap.Unlock();
            }
        }
    }
}
