using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VoipNet.Samples.Theme;

/// <summary>
/// The signature element of the sample apps: a thin oscilloscope trace that draws the real audio
/// level of a call, newest on the right. Push one level per audio frame.
/// </summary>
public sealed class TraceLine : Control
{
    /// <summary>Line colour.</summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<TraceLine, IBrush?>(nameof(Stroke));

    /// <summary>Background fill.</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<TraceLine, IBrush?>(nameof(Background));

    /// <summary>Levels from 0 to 1, oldest first. Replace the array to redraw.</summary>
    public static readonly StyledProperty<double[]?> LevelsProperty =
        AvaloniaProperty.Register<TraceLine, double[]?>(nameof(Levels));

    /// <summary>When true the line mirrors around the centre like a waveform envelope.</summary>
    public static readonly StyledProperty<bool> MirroredProperty =
        AvaloniaProperty.Register<TraceLine, bool>(nameof(Mirrored), true);

    static TraceLine()
    {
        AffectsRender<TraceLine>(StrokeProperty, BackgroundProperty, LevelsProperty, MirroredProperty);
    }

    /// <summary>Line colour.</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Background fill.</summary>
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>Levels from 0 to 1, oldest first.</summary>
    public double[]? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    /// <summary>Mirror the trace around the centre line.</summary>
    public bool Mirrored
    {
        get => GetValue(MirroredProperty);
        set => SetValue(MirroredProperty, value);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
        {
            context.DrawRectangle(background, null, bounds, 3, 3);
        }

        var middle = bounds.Height / 2;
        var baseline = new Pen(Stroke, 1) { DashStyle = new DashStyle([2, 4], 0) };
        context.DrawLine(baseline, new Point(6, middle), new Point(bounds.Width - 6, middle));

        var levels = Levels;
        if (levels is not { Length: > 1 } || Stroke is null)
        {
            return;
        }

        var pen = new Pen(Stroke, 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var step = (bounds.Width - 12) / (levels.Length - 1);
        var amplitude = (bounds.Height / 2) - 5;

        var upper = new StreamGeometry();
        using (var geometry = upper.Open())
        {
            geometry.BeginFigure(new Point(6, middle - (Shape(levels[0]) * amplitude)), false);
            for (var i = 1; i < levels.Length; i++)
            {
                geometry.LineTo(new Point(6 + (i * step), middle - (Shape(levels[i]) * amplitude)));
            }

            geometry.EndFigure(false);
        }

        context.DrawGeometry(null, pen, upper);

        if (!Mirrored)
        {
            return;
        }

        var lower = new StreamGeometry();
        using (var geometry = lower.Open())
        {
            geometry.BeginFigure(new Point(6, middle + (Shape(levels[0]) * amplitude)), false);
            for (var i = 1; i < levels.Length; i++)
            {
                geometry.LineTo(new Point(6 + (i * step), middle + (Shape(levels[i]) * amplitude)));
            }

            geometry.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(Stroke, 1, lineCap: PenLineCap.Round) { Brush = Stroke }, lower);
    }

    /// <summary>Perceptual shaping so quiet speech is still visible.</summary>
    private static double Shape(double level) => Math.Clamp(Math.Sqrt(Math.Max(level, 0)) * 1.6, 0, 1);
}

/// <summary>Keeps a rolling window of audio levels for a <see cref="TraceLine"/>.</summary>
/// <param name="capacity">Number of points kept.</param>
public sealed class LevelHistory(int capacity = 120)
{
    private readonly double[] _buffer = new double[capacity];
    private int _next;
    private readonly Lock _gate = new();

    /// <summary>Adds a level from 0 to 1.</summary>
    /// <param name="level">The level.</param>
    public void Push(double level)
    {
        lock (_gate)
        {
            _buffer[_next] = level;
            _next = (_next + 1) % _buffer.Length;
        }
    }

    /// <summary>Returns the levels oldest first, as a new array suitable for binding.</summary>
    public double[] Snapshot()
    {
        lock (_gate)
        {
            var result = new double[_buffer.Length];
            for (var i = 0; i < _buffer.Length; i++)
            {
                result[i] = _buffer[(_next + i) % _buffer.Length];
            }

            return result;
        }
    }

    /// <summary>Resets all levels to zero.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_buffer);
        }
    }
}
