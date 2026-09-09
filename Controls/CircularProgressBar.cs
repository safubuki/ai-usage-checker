using System;
using System.Windows;
using System.Windows.Media;

namespace AIUsageChecker.Controls;

public class CircularProgressBar : FrameworkElement
{
    public static readonly DependencyProperty PercentageProperty =
        DependencyProperty.Register(nameof(Percentage), typeof(double), typeof(CircularProgressBar),
            new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CircularProgressBar),
            new FrameworkPropertyMetadata(3.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressBrushProperty =
        DependencyProperty.Register(nameof(ProgressBrush), typeof(Brush), typeof(CircularProgressBar),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(CircularProgressBar),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x40, 0x55, 0x55, 0x55)), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Percentage
    {
        get => (double)GetValue(PercentageProperty);
        set => SetValue(PercentageProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeThickness * 2) return;

        double radius = (size - StrokeThickness) / 2.0;
        Point center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);

        // 背景のトラック円を描画
        var trackPen = new Pen(TrackBrush, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        double pct = Math.Clamp(Percentage, 0.0, 100.0);
        if (pct <= 0.0) return;

        var progressPen = new Pen(ProgressBrush, StrokeThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        if (pct >= 99.9)
        {
            // ほぼ100%の場合は完全な円を描画
            dc.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        // 開始角度: 真上 (-90度)
        double startAngle = -90.0;
        double sweepAngle = (pct / 100.0) * 360.0;
        double endAngle = startAngle + sweepAngle;

        Point startPoint = PointOnCircle(center, radius, startAngle);
        Point endPoint = PointOnCircle(center, radius, endAngle);

        var geometry = new PathGeometry();
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false
        };

        var arc = new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepAngle > 180.0
        };

        figure.Segments.Add(arc);
        geometry.Figures.Add(figure);

        dc.DrawGeometry(null, progressPen, geometry);
    }

    private static Point PointOnCircle(Point center, double radius, double angleInDegrees)
    {
        double rad = (Math.PI / 180.0) * angleInDegrees;
        return new Point(
            center.X + radius * Math.Cos(rad),
            center.Y + radius * Math.Sin(rad)
        );
    }
}
