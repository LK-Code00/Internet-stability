using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetStability.Core.Models;

namespace NetStability.App.Controls;

public sealed class NetworkChart : FrameworkElement
{
    private IReadOnlyList<ProbeSample> _samples = Array.Empty<ProbeSample>();
    public bool UseGameTargetForJitter { get; set; }

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(NetworkChart),
        new FrameworkPropertyMetadata("Ping", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Mode
    {
        get => (string)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public void SetSamples(IReadOnlyList<ProbeSample> samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 40) return;

        var left = 42d;
        var right = 9d;
        var top = 8d;
        var bottom = 22d;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(38, 53, 74)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(145, 162, 186));

        var values = Values().Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        var max = values.Length == 0 ? 10 : Math.Max(10, values.Max() * 1.15);
        if (Mode.Equals("Jitter", StringComparison.OrdinalIgnoreCase)) max = Math.Max(5, Math.Ceiling(max / 5) * 5);
        else max = Math.Max(20, Math.Ceiling(max / 20) * 20);

        for (var index = 0; index <= 4; index++)
        {
            var y = top + plotHeight * index / 4;
            drawingContext.DrawLine(gridPen, new Point(left, y), new Point(left + plotWidth, y));
            var labelValue = max * (4 - index) / 4;
            var label = new FormattedText(labelValue.ToString("0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, labelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(label, new Point(0, y - label.Height / 2));
        }

        if (_samples.Count == 0)
        {
            var empty = new FormattedText("Aguardando amostras", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, labelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawingContext.DrawText(empty, new Point(left + 8, top + plotHeight / 2 - empty.Height / 2));
            return;
        }

        if (Mode.Equals("Jitter", StringComparison.OrdinalIgnoreCase))
        {
            DrawSeries(drawingContext, JitterValues(),
                new SolidColorBrush(Color.FromRgb(245, 187, 97)), left, top, plotWidth, plotHeight, max);
        }
        else
        {
            DrawSeries(drawingContext, _samples.Select(sample => sample.GatewayMs).ToArray(),
                new SolidColorBrush(Color.FromRgb(91, 214, 192)), left, top, plotWidth, plotHeight, max);
            DrawSeries(drawingContext, _samples.Select(sample => sample.InternetMs).ToArray(),
                new SolidColorBrush(Color.FromRgb(132, 165, 255)), left, top, plotWidth, plotHeight, max);
            DrawSeries(drawingContext, _samples.Select(sample => sample.GameMs).ToArray(),
                new SolidColorBrush(Color.FromRgb(242, 118, 131)), left, top, plotWidth, plotHeight, max);
        }

        var xLabel = new FormattedText("tempo →", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, labelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(xLabel, new Point(left + plotWidth - xLabel.Width, top + plotHeight + 4));
    }

    private IEnumerable<double?> Values() => Mode.Equals("Jitter", StringComparison.OrdinalIgnoreCase)
        ? JitterValues()
        : _samples.SelectMany(sample => new[] { sample.GatewayMs, sample.InternetMs, sample.GameMs });

    private double?[] JitterValues()
    {
        double? previous = null;
        var result = new double?[_samples.Count];
        for (var index = 0; index < _samples.Count; index++)
        {
            var current = UseGameTargetForJitter ? _samples[index].GameMs : _samples[index].InternetMs;
            result[index] = current.HasValue && previous.HasValue ? Math.Abs(current.Value - previous.Value) : null;
            previous = current;
        }
        return result;
    }

    private static void DrawSeries(DrawingContext dc, double?[] values, Brush brush,
        double left, double top, double width, double height, double max)
    {
        var pen = new Pen(brush, 2.2);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var active = false;
            for (var i = 0; i < values.Length; i++)
            {
                if (!values[i].HasValue)
                {
                    active = false;
                    continue;
                }

                var x = left + (values.Length <= 1 ? 0 : width * i / (values.Length - 1));
                var y = top + height * (1 - Math.Clamp(values[i]!.Value / max, 0, 1));
                if (!active)
                {
                    context.BeginFigure(new Point(x, y), false, false);
                    active = true;
                }
                else context.LineTo(new Point(x, y), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
