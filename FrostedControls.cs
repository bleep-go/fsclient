using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FsClient;

/// <summary>
/// Элемент с матовым стеклом. Позволяет временно снять VisualBrush,
/// чтобы окно в трее не удерживало размытую копию фона в памяти.
/// </summary>
public interface IBlurHost
{
    UIElement BlurSource { get; set; }
    void ReleaseBlur();
    void RestoreBlur(UIElement source);
}

/// <summary>
/// Панель с матовым стеклом: берёт размытую копию фона через VisualBrush
/// и добавляет полупрозрачную подложку + тонкую границу.
/// </summary>
public class FrostedBorder : Border, IBlurHost
{
    public static readonly DependencyProperty BlurSourceProperty = DependencyProperty.Register(
        nameof(BlurSource), typeof(UIElement), typeof(FrostedBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(System.Windows.Media.Brush), typeof(FrostedBorder),
        new FrameworkPropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromArgb(115, 20, 19, 32)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrostBorderBrushProperty = DependencyProperty.Register(
        nameof(FrostBorderBrush), typeof(System.Windows.Media.Brush), typeof(FrostedBorder),
        new FrameworkPropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, 255, 255, 255)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public UIElement BlurSource
    {
        get => (UIElement)GetValue(BlurSourceProperty);
        set => SetValue(BlurSourceProperty, value);
    }

    public System.Windows.Media.Brush Tint
    {
        get => (System.Windows.Media.Brush)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public System.Windows.Media.Brush FrostBorderBrush
    {
        get => (System.Windows.Media.Brush)GetValue(FrostBorderBrushProperty);
        set => SetValue(FrostBorderBrushProperty, value);
    }

    private VisualBrush _brush;
    private bool _updating;

    public FrostedBorder()
    {
        Loaded += (_, _) => Invalidate();
        SizeChanged += (_, _) => Invalidate();
        LayoutUpdated += (_, _) => Invalidate();
    }

    private void Invalidate()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            if (BlurSource == null || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            GeneralTransform transform = TransformToVisual(BlurSource);
            if (transform == null) return;

            System.Windows.Point topLeft = transform.Transform(new System.Windows.Point(0, 0));
            System.Windows.Point bottomRight = transform.Transform(new System.Windows.Point(ActualWidth, ActualHeight));
            var viewbox = new Rect(topLeft.X, topLeft.Y,
                bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);

            if (viewbox.Width <= 0 || viewbox.Height <= 0) return;

            if (_brush == null)
            {
                _brush = new VisualBrush
                {
                    Visual = BlurSource,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewport = new Rect(0, 0, 1, 1),
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                Background = _brush;
            }

            if (_brush.Viewbox != viewbox)
                _brush.Viewbox = viewbox;
        }
        catch
        {
            Background = null;
        }
        finally
        {
            _updating = false;
        }
    }

    public void ReleaseBlur()
    {
        _brush = null;
        Background = null;
        BlurSource = null;
    }

    public void RestoreBlur(UIElement source)
    {
        BlurSource = source;
        Invalidate();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (rect.IsEmpty) return;

        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(rect.Width, rect.Height) / 2);
        dc.DrawRoundedRectangle(Tint, null, rect, radius, radius);
        dc.DrawRoundedRectangle(null, new System.Windows.Media.Pen(FrostBorderBrush, 1), rect, radius, radius);
    }
}

/// <summary>
/// Кнопка с тем же эффектом матового стекла.
/// </summary>
public class FrostedButton : System.Windows.Controls.Button, IBlurHost
{
    public static readonly DependencyProperty BlurSourceProperty = DependencyProperty.Register(
        nameof(BlurSource), typeof(UIElement), typeof(FrostedButton),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(System.Windows.Media.Brush), typeof(FrostedButton),
        new FrameworkPropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromArgb(115, 20, 19, 32)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrostBorderBrushProperty = DependencyProperty.Register(
        nameof(FrostBorderBrush), typeof(System.Windows.Media.Brush), typeof(FrostedButton),
        new FrameworkPropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromArgb(36, 255, 255, 255)),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public UIElement BlurSource
    {
        get => (UIElement)GetValue(BlurSourceProperty);
        set => SetValue(BlurSourceProperty, value);
    }

    public System.Windows.Media.Brush Tint
    {
        get => (System.Windows.Media.Brush)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public System.Windows.Media.Brush FrostBorderBrush
    {
        get => (System.Windows.Media.Brush)GetValue(FrostBorderBrushProperty);
        set => SetValue(FrostBorderBrushProperty, value);
    }

    private VisualBrush _brush;
    private bool _updating;

    static FrostedButton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(FrostedButton),
            new FrameworkPropertyMetadata(typeof(FrostedButton)));
    }

    public FrostedButton()
    {
        Loaded += (_, _) => Invalidate();
        SizeChanged += (_, _) => Invalidate();
        LayoutUpdated += (_, _) => Invalidate();
        MouseEnter += (_, _) => SetHighlight(true);
        MouseLeave += (_, _) => SetHighlight(false);
    }

    private void SetHighlight(bool hover)
    {
        Tint = new SolidColorBrush(hover
            ? System.Windows.Media.Color.FromArgb(145, 20, 19, 32)
            : System.Windows.Media.Color.FromArgb(115, 20, 19, 32));
        FrostBorderBrush = new SolidColorBrush(hover
            ? System.Windows.Media.Color.FromArgb(60, 255, 255, 255)
            : System.Windows.Media.Color.FromArgb(36, 255, 255, 255));
    }

    private void Invalidate()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            if (BlurSource == null || ActualWidth <= 0 || ActualHeight <= 0)
                return;

            GeneralTransform transform = TransformToVisual(BlurSource);
            if (transform == null) return;

            System.Windows.Point topLeft = transform.Transform(new System.Windows.Point(0, 0));
            System.Windows.Point bottomRight = transform.Transform(new System.Windows.Point(ActualWidth, ActualHeight));
            var viewbox = new Rect(topLeft.X, topLeft.Y,
                bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            if (viewbox.Width <= 0 || viewbox.Height <= 0) return;

            if (_brush == null)
            {
                _brush = new VisualBrush
                {
                    Visual = BlurSource,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                    Viewport = new Rect(0, 0, 1, 1),
                    Stretch = Stretch.Fill,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                Background = _brush;
            }

            if (_brush.Viewbox != viewbox)
                _brush.Viewbox = viewbox;
        }
        catch
        {
            Background = null;
        }
        finally
        {
            _updating = false;
        }
    }

    public void ReleaseBlur()
    {
        _brush = null;
        Background = null;
        BlurSource = null;
    }

    public void RestoreBlur(UIElement source)
    {
        BlurSource = source;
        Invalidate();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (rect.IsEmpty) return;

        double radius = Math.Min(12, Math.Min(rect.Width, rect.Height) / 2);
        dc.DrawRoundedRectangle(Tint, null, rect, radius, radius);
        dc.DrawRoundedRectangle(null, new System.Windows.Media.Pen(FrostBorderBrush, 1), rect, radius, radius);
    }
}

/// <summary>
/// Скруглённый прогресс-бар с плавной анимацией заполнения.
/// </summary>
public class RoundedProgressBar : System.Windows.Controls.ProgressBar
{
    private ScaleTransform _scale;

    static RoundedProgressBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RoundedProgressBar),
            new FrameworkPropertyMetadata(typeof(RoundedProgressBar)));
        ValueProperty.OverrideMetadata(typeof(RoundedProgressBar),
            new FrameworkPropertyMetadata(0.0, OnValueChanged));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scale = GetTemplateChild("FillScale") as ScaleTransform;
        ApplyValue(false);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((RoundedProgressBar)d).ApplyValue(true);
    }

    private void ApplyValue(bool animate)
    {
        if (_scale == null) return;

        double target = Math.Max(0, Math.Min(100, Value)) / 100.0;

        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (!animate)
        {
            _scale.ScaleX = target;
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
    }
}
