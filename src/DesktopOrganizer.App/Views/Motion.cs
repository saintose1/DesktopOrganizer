using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DesktopOrganizer.App.Views;

/// <summary>
/// Small presentation-only motion for the main window: fade/slide when an element becomes visible and a soft
/// lift on hover. Transforms are created per element in code (never shared/frozen). Respects the Windows
/// "Animation effects" setting: with it off, elements simply appear.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterChanged));
    public static readonly DependencyProperty OffsetXProperty = DependencyProperty.RegisterAttached("OffsetX", typeof(double), typeof(Motion), new PropertyMetadata(0.0));
    public static readonly DependencyProperty OffsetYProperty = DependencyProperty.RegisterAttached("OffsetY", typeof(double), typeof(Motion), new PropertyMetadata(10.0));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.RegisterAttached("Duration", typeof(double), typeof(Motion), new PropertyMetadata(240.0));
    public static readonly DependencyProperty TargetOpacityProperty = DependencyProperty.RegisterAttached("TargetOpacity", typeof(double), typeof(Motion), new PropertyMetadata(1.0));
    public static readonly DependencyProperty LiftProperty = DependencyProperty.RegisterAttached(
        "Lift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLiftChanged));

    public static bool GetEnter(DependencyObject o) => (bool)o.GetValue(EnterProperty);
    public static void SetEnter(DependencyObject o, bool v) => o.SetValue(EnterProperty, v);
    public static double GetOffsetX(DependencyObject o) => (double)o.GetValue(OffsetXProperty);
    public static void SetOffsetX(DependencyObject o, double v) => o.SetValue(OffsetXProperty, v);
    public static double GetOffsetY(DependencyObject o) => (double)o.GetValue(OffsetYProperty);
    public static void SetOffsetY(DependencyObject o, double v) => o.SetValue(OffsetYProperty, v);
    public static double GetDuration(DependencyObject o) => (double)o.GetValue(DurationProperty);
    public static void SetDuration(DependencyObject o, double v) => o.SetValue(DurationProperty, v);
    public static double GetTargetOpacity(DependencyObject o) => (double)o.GetValue(TargetOpacityProperty);
    public static void SetTargetOpacity(DependencyObject o, double v) => o.SetValue(TargetOpacityProperty, v);
    public static bool GetLift(DependencyObject o) => (bool)o.GetValue(LiftProperty);
    public static void SetLift(DependencyObject o, bool v) => o.SetValue(LiftProperty, v);

    private static bool Enabled => SystemParameters.ClientAreaAnimation;

    private static TranslateTransform OwnTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } own) return own;
        var created = new TranslateTransform();
        element.RenderTransform = created;
        return created;
    }

    private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.IsVisibleChanged -= OnVisibleChanged;
        if ((bool)e.NewValue) element.IsVisibleChanged += OnVisibleChanged;
    }

    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || e.NewValue is not true) return;
        var target = GetTargetOpacity(element);
        if (!Enabled) { element.BeginAnimation(UIElement.OpacityProperty, null); element.Opacity = target; return; }
        var duration = TimeSpan.FromMilliseconds(GetDuration(element));
        var transform = OwnTransform(element);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, target, duration) { EasingFunction = Ease });
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(GetOffsetX(element), 0, duration) { EasingFunction = Ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(GetOffsetY(element), 0, duration) { EasingFunction = Ease });
    }

    private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.MouseEnter -= OnLiftEnter; element.MouseLeave -= OnLiftLeave;
        if ((bool)e.NewValue) { element.MouseEnter += OnLiftEnter; element.MouseLeave += OnLiftLeave; }
    }
    private static void OnLiftEnter(object sender, MouseEventArgs e) => Lift(sender, -3);
    private static void OnLiftLeave(object sender, MouseEventArgs e) => Lift(sender, 0);
    private static void Lift(object sender, double y)
    {
        if (sender is not UIElement element) return;
        var transform = OwnTransform(element);
        if (!Enabled) { transform.BeginAnimation(TranslateTransform.YProperty, null); transform.Y = y; return; }
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease });
    }
}
