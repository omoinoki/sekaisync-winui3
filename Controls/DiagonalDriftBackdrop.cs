using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace SekaiSync.Desktop.Controls;

/// <summary>
/// 定向漂流背景（手册 §5「定向漂流」/ tokens 2.0 effects.drift）。
/// 三档密度：关闭 / 轻盈 24 枚 / 丰富 42 枚；切换保留已有图形的位置与姿态。
/// 只做装饰：不参与命中测试、不进读屏树，高对比与关闭动画时退场。
/// </summary>
public sealed class DiagonalDriftBackdrop : UserControl
{
    private const int QuietCount = 24;
    private const int FullCount = 42;

    private sealed class Fragment
    {
        public required Shape Shape;
        public required Visual Visual;
        public double X;
        public double Y;
        public double Speed;
        public double Direction;
        public float AngularSpeed;
        public double Phase;
        public double AxisPeriod;
        public Quaternion Orientation;
    }

    private readonly Canvas _canvas = new();
    private readonly List<Fragment> _fragments = [];
    private readonly Random _random = new(260926);
    private readonly UISettings _settings = new();
    private readonly DispatcherQueueTimer _timer;
    private long _lastTick;
    private double _activeTime;
    private int _targetCount = QuietCount;
    private bool _decorationEnabled = true;

    public DiagonalDriftBackdrop()
    {
        Content = _canvas;
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += OnTick;
        Loaded += (_, _) => UpdatePlayback();
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) => UpdateOpacity();
        UpdateOpacity();
    }

    /// <summary>Off / Quiet / Full。关闭时清空图形并停表，不重置随机序列。</summary>
    public void SetDensity(string density)
    {
        _targetCount = density switch
        {
            "Off" => 0,
            "Full" => FullCount,
            _ => QuietCount,
        };

        while (_fragments.Count > _targetCount)
        {
            var last = _fragments[^1];
            _fragments.RemoveAt(_fragments.Count - 1);
            _canvas.Children.Remove(last.Shape);
            ElementCompositionPreview.SetElementChildVisual(last.Shape, null);
        }

        if (_targetCount > 0 && ActualWidth > 0 && ActualHeight > 0)
        {
            AddFragments(_targetCount - _fragments.Count);
        }

        UpdatePlayback();
    }

    /// <summary>高对比等场景整段退场（手册 §7：装饰消失，内容层级不变）。</summary>
    public void SetDecorationEnabled(bool enabled)
    {
        _decorationEnabled = enabled;
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdatePlayback();
    }

    private void UpdateOpacity() => Opacity = ActualTheme == ElementTheme.Dark ? 0.32 : 0.58;

    private void UpdatePlayback()
    {
        _lastTick = Stopwatch.GetTimestamp();
        var shouldRun = IsLoaded && _decorationEnabled && _targetCount > 0 && _settings.AnimationsEnabled;
        if (shouldRun) _timer.Start();
        else _timer.Stop();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;

        if (_fragments.Count == 0)
        {
            AddFragments(_targetCount);
        }
        else
        {
            // 缩放保留相对构图（手册 §5 lifecycle：resize retains relative composition）。
            foreach (var fragment in _fragments)
            {
                fragment.X *= e.NewSize.Width / e.PreviousSize.Width;
                fragment.Y *= e.NewSize.Height / e.PreviousSize.Height;
                Place(fragment);
            }
        }
    }

    private void AddFragments(int count)
    {
        string[] colors = ["SkFragmentCyanBrush", "SkFragmentPinkBrush", "SkFragmentLemonBrush"];
        for (var added = 0; added < count; added++)
        {
            var index = _fragments.Count;
            var size = 24 + _random.NextDouble() * 40;
            Shape shape = (index % 4) switch
            {
                0 or 1 => new Polygon { Points = new PointCollection { new(0, size), new(size * 0.42, 0), new(size, size * 0.85) } },
                2 => new Rectangle(),
                _ => new Ellipse(),
            };
            var brush = (Brush)Application.Current.Resources[colors[index % 3]];
            // 空心与实心按单枚判定（各半），密度切换时已有图形不会因总数变化而换相。
            var outline = index % 2 == 0;
            shape.Width = size;
            shape.Height = size;
            shape.Fill = outline ? null : brush;
            shape.Stroke = outline ? brush : null;
            shape.StrokeThickness = 2;
            AutomationProperties.SetAccessibilityView(shape, AccessibilityView.Raw);
            _canvas.Children.Add(shape);

            var visual = ElementCompositionPreview.GetElementVisual(shape);
            visual.CenterPoint = new Vector3((float)size / 2, (float)size / 2, 0);
            var perspective = Matrix4x4.Identity;
            perspective.M34 = -1f / 900;
            visual.TransformMatrix = perspective;

            _fragments.Add(new Fragment
            {
                Shape = shape,
                Visual = visual,
                X = _random.NextDouble() * ActualWidth,
                Y = _random.NextDouble() * ActualHeight,
                Speed = 28 + _random.NextDouble() * 20,
                Direction = (28 + _random.NextDouble() * 14) * Math.PI / 180,
                AngularSpeed = (float)((7 + _random.NextDouble() * 6) * Math.PI / 180),
                Phase = _random.NextDouble() * Math.PI * 2,
                AxisPeriod = 24 + _random.NextDouble() * 24,
                Orientation = Quaternion.CreateFromYawPitchRoll((float)_random.NextDouble(), (float)_random.NextDouble(), (float)(_random.NextDouble() * Math.PI * 2)),
            });
            Place(_fragments[^1]);
        }
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        if (!_settings.AnimationsEnabled)
        {
            UpdatePlayback();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var delta = Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;
        _activeTime += delta;
        var scale = Math.Clamp(ActualWidth / 1000, 0.5, 1);
        foreach (var fragment in _fragments)
        {
            fragment.X += Math.Cos(fragment.Direction) * fragment.Speed * scale * delta;
            fragment.Y -= Math.Sin(fragment.Direction) * fragment.Speed * scale * delta;
            if (fragment.X > ActualWidth + 120 || fragment.Y < -120)
            {
                var fromLeft = _random.Next(2) == 0;
                fragment.X = fromLeft ? -120 : _random.NextDouble() * ActualWidth;
                fragment.Y = fromLeft ? _random.NextDouble() * ActualHeight : ActualHeight + 120;
            }
            var phase = fragment.Phase + _activeTime * Math.PI * 2 / fragment.AxisPeriod;
            var axis = Vector3.Normalize(new Vector3((float)Math.Cos(phase), (float)Math.Sin(phase), 0.6f));
            fragment.Orientation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(axis, fragment.AngularSpeed * (float)delta) * fragment.Orientation);
            Place(fragment);
        }
    }

    private static void Place(Fragment fragment)
    {
        Canvas.SetLeft(fragment.Shape, fragment.X);
        Canvas.SetTop(fragment.Shape, fragment.Y);
        fragment.Visual.Orientation = fragment.Orientation;
    }
}
