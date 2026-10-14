using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClawBox.Desktop;

public sealed class ThinkingIndicator : TextBlock
{
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(nameof(Active), typeof(bool), typeof(ThinkingIndicator),
        new PropertyMetadata(false, (owner, _) => ((ThinkingIndicator)owner).UpdateAnimation()));
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }
    private readonly TranslateTransform scan = new();
    public ThinkingIndicator()
    {
        Text = "思考中";
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => StopAnimation();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }
    private void UpdateAnimation()
    {
        StopAnimation();
        if (!Active || !IsLoaded || !IsVisible || !SystemParameters.ClientAreaAnimation) return;
        Color muted = Color.FromRgb(146, 146, 152);
        LinearGradientBrush light = new()
        {
            StartPoint = new(0, 0),
            EndPoint = new(1, 0),
            RelativeTransform = scan,
            GradientStops = new() { new(muted, 0), new(muted, 0.3), new(Color.FromRgb(225, 237, 252), 0.5), new(muted, 0.7), new(muted, 1) }
        };
        Foreground = light;
        DoubleAnimationUsingKeyFrames sweep = new() { Duration = TimeSpan.FromSeconds(3), RepeatBehavior = RepeatBehavior.Forever };
        sweep.KeyFrames.Add(new LinearDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3))));
        scan.BeginAnimation(TranslateTransform.XProperty, sweep);
    }
    private void StopAnimation()
    {
        scan.BeginAnimation(TranslateTransform.XProperty, null);
        scan.X = 0;
        Foreground = new SolidColorBrush(Color.FromRgb(146, 146, 152));
    }
}
