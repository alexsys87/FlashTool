using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlashTool.App.Localization;
using FlashTool.Core.Operations;

namespace FlashTool.App.Views.Controls;

/// <summary>
/// Speed by offset for the device test: one line for writing and one for reading, slow stretches
/// shaded, bad areas marked, and the part past the real capacity of a counterfeit device covered.
/// Drawn directly, without a charting library.
/// </summary>
public sealed class SpeedChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = Register<IReadOnlyList<DeviceTestSample>?>(nameof(Samples), null);
    public static readonly DependencyProperty SlowAreasProperty = Register<IReadOnlyList<DeviceSlowArea>?>(nameof(SlowAreas), null);
    public static readonly DependencyProperty BadAreasProperty = Register<IReadOnlyList<DeviceBadArea>?>(nameof(BadAreas), null);
    public static readonly DependencyProperty DeviceLengthProperty = Register(nameof(DeviceLength), 0L);
    public static readonly DependencyProperty RealCapacityProperty = Register<long?>(nameof(RealCapacity), null);
    public static readonly DependencyProperty ForegroundProperty = Register<Brush>(nameof(Foreground), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = Register<Brush>(nameof(GridBrush), Brushes.LightGray);
    public static readonly DependencyProperty WriteBrushProperty = Register<Brush>(nameof(WriteBrush), Brushes.DodgerBlue);
    public static readonly DependencyProperty ReadBrushProperty = Register<Brush>(nameof(ReadBrush), Brushes.SeaGreen);
    public static readonly DependencyProperty ExportBackgroundProperty = Register<Brush>(nameof(ExportBackground), Brushes.White);

    private static readonly Brush SlowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xF2, 0xA6, 0x00)));
    private static readonly Brush BadBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0xE8, 0x11, 0x23)));
    private static readonly Brush FakeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xE8, 0x11, 0x23)));

    private const double Left = 64;
    private const double Right = 12;
    private const double Top = 26;
    private const double Bottom = 24;

    public IReadOnlyList<DeviceTestSample>? Samples
    {
        get => (IReadOnlyList<DeviceTestSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public IReadOnlyList<DeviceSlowArea>? SlowAreas
    {
        get => (IReadOnlyList<DeviceSlowArea>?)GetValue(SlowAreasProperty);
        set => SetValue(SlowAreasProperty, value);
    }

    public IReadOnlyList<DeviceBadArea>? BadAreas
    {
        get => (IReadOnlyList<DeviceBadArea>?)GetValue(BadAreasProperty);
        set => SetValue(BadAreasProperty, value);
    }

    public long DeviceLength
    {
        get => (long)GetValue(DeviceLengthProperty);
        set => SetValue(DeviceLengthProperty, value);
    }

    public long? RealCapacity
    {
        get => (long?)GetValue(RealCapacityProperty);
        set => SetValue(RealCapacityProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush WriteBrush
    {
        get => (Brush)GetValue(WriteBrushProperty);
        set => SetValue(WriteBrushProperty, value);
    }

    public Brush ReadBrush
    {
        get => (Brush)GetValue(ReadBrushProperty);
        set => SetValue(ReadBrushProperty, value);
    }

    /// <summary>Fills the picture saved by <see cref="SavePng"/>, which has no window behind it.</summary>
    public Brush ExportBackground
    {
        get => (Brush)GetValue(ExportBackgroundProperty);
        set => SetValue(ExportBackgroundProperty, value);
    }

    /// <summary>Saves the chart as it is shown, at twice the screen resolution.</summary>
    public void SavePng(string path)
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            dc.DrawRectangle(ExportBackground, null, bounds);
            dc.DrawRectangle(new VisualBrush(this), null, bounds);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var plot = new Rect(Left, Top, Math.Max(1, width - Left - Right), Math.Max(1, height - Top - Bottom));

        // Transparent background so that the whole area takes the mouse (tooltips of the page).
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var samples = Samples ?? [];
        var length = DeviceLength;
        if (length <= 0)
        {
            return;
        }

        double X(long offset) => plot.Left + (plot.Width * Math.Clamp(offset / (double)length, 0, 1));

        // Areas first, lines on top.
        if (RealCapacity is { } real && real < length)
        {
            drawingContext.DrawRectangle(FakeBrush, null, new Rect(new Point(X(real), plot.Top), new Point(plot.Right, plot.Bottom)));
        }

        foreach (var area in SlowAreas ?? [])
        {
            drawingContext.DrawRectangle(SlowBrush, null, new Rect(new Point(X(area.Offset), plot.Top), new Point(X(area.Offset + area.Length), plot.Bottom)));
        }

        foreach (var area in BadAreas ?? [])
        {
            var x = X(area.Offset);
            drawingContext.DrawRectangle(BadBrush, null, new Rect(x, plot.Bottom - 6, Math.Max(2, X(area.Offset + area.Length) - x), 6));
        }

        var max = samples.Count > 0 ? samples.Max(s => s.BytesPerSecond) : 0;
        var step = NiceStep(Math.Max(max, 1024 * 1024) * 1.1 / 4);
        var top = Math.Max(step, Math.Ceiling(Math.Max(max, 1) * 1.08 / step) * step);
        double Y(double speed) => plot.Bottom - (plot.Height * Math.Clamp(speed / top, 0, 1));

        var gridPen = new Pen(GridBrush, 1);
        for (var value = 0.0; value <= top + (step / 2); value += step)
        {
            var y = Math.Round(Y(value)) + 0.5;
            drawingContext.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(Loc.Format("Test_SpeedValue", (value / (1024 * 1024)).ToString(value / (1024 * 1024) >= 10 ? "0" : "0.#", Loc.Instance.Culture)));
            drawingContext.DrawText(label, new Point(plot.Left - label.Width - 6, y - (label.Height / 2)));
        }

        for (var i = 0; i <= 4; i++)
        {
            var offset = length / 4 * i;
            var x = X(offset);
            var label = Text(Formatting.Size(offset));
            drawingContext.DrawLine(gridPen, new Point(x, plot.Bottom), new Point(x, plot.Bottom + 3));
            var labelX = Math.Clamp(x - (label.Width / 2), 0, Math.Max(0, width - label.Width));
            drawingContext.DrawText(label, new Point(labelX, plot.Bottom + 4));
        }

        var legendX = plot.Left;
        foreach (var phase in new[] { DeviceTestPhase.Write, DeviceTestPhase.Read })
        {
            var points = samples.Where(s => s.Phase == phase).OrderBy(s => s.Offset).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            var brush = phase == DeviceTestPhase.Write ? WriteBrush : ReadBrush;
            var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(X(points[0].Offset + (points[0].Length / 2)), Y(points[0].BytesPerSecond)), false, false);
                foreach (var p in points.Skip(1))
                {
                    g.LineTo(new Point(X(p.Offset + (p.Length / 2)), Y(p.BytesPerSecond)), true, true);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);

            var name = Text(Loc.Get(phase == DeviceTestPhase.Write ? "Test_LegendWrite" : "Test_LegendRead"));
            drawingContext.DrawLine(pen, new Point(legendX, 12), new Point(legendX + 18, 12));
            drawingContext.DrawText(name, new Point(legendX + 22, 12 - (name.Height / 2)));
            legendX += 22 + name.Width + 18;
        }
    }

    /// <summary>1, 2 or 5 times a power of ten (of bytes per second, in MiB/s).</summary>
    private static double NiceStep(double raw)
    {
        const double mib = 1024 * 1024;
        var value = raw / mib;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(value, 1e-3))));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude * mib;
    }

    private FormattedText Text(string text)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static DependencyProperty Register<T>(string name, T defaultValue)
        => DependencyProperty.Register(name, typeof(T), typeof(SpeedChart), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));
}
