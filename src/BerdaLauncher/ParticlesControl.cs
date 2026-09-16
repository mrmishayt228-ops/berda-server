using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BerdaLauncher;

// ============================================================
//  Animated purple floating-particles background.
// ============================================================
public class ParticlesControl : FrameworkElement
{
    private class Orb
    {
        public DrawingVisual V = new();
        public double X, Y, R;
        public double Vx, Vy;
        public double Phase;
        public double BaseA;
        public TranslateTransform T = new();
    }

    private readonly List<Orb> _orbs = new();
    private DateTime _last = DateTime.UtcNow;
    private bool _started;

    private static readonly Brush[] Palette =
    {
        BrushOf("#FF9B59FF"), // bright violet
        BrushOf("#FF7B3FD4"), // violet
        BrushOf("#FFD06BFF"), // light purple
        BrushOf("#FF4A2BD6"), // deep indigo
        BrushOf("#FFC084FF"), // soft lilac
    };

    private static SolidColorBrush BrushOf(string s)
    {
        var c = System.Windows.Media.ColorConverter.ConvertFromString(s);
        return new SolidColorBrush(c is System.Windows.Media.Color cc ? cc : Colors.Magenta);
    }

    private static readonly Random Rng = new();

    public ParticlesControl()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;
        Loaded += (_, _) =>
        {
            if (_started) return;
            _started = true;
            SpawnOrbs();
            CompositionTarget.Rendering += OnRenderFrame;
            SizeChanged += (_, _) =>
            {
                foreach (var o in _orbs)
                    if (o.X > ActualWidth) o.X = Rng.NextDouble() * ActualWidth;
            };
        };
        Unloaded += (_, _) =>
        {
            if (!_started) return;
            _started = false;
            CompositionTarget.Rendering -= OnRenderFrame;
        };
    }

    private void SpawnOrbs()
    {
        _orbs.Clear();
        int count = Math.Max(10, (int)(ActualWidth * ActualHeight / 24000.0));
        for (int i = 0; i < count; i++)
        {
            var o = new Orb
            {
                X = Rng.NextDouble() * Math.Max(ActualWidth, 1),
                Y = Rng.NextDouble() * Math.Max(ActualHeight, 1),
                R = 16 + Rng.NextDouble() * 42,
                Vx = (Rng.NextDouble() - 0.5) * 14,
                Vy = (Rng.NextDouble() - 0.5) * 9 - 6,
                Phase = Rng.NextDouble() * Math.PI * 2,
                BaseA = 0.25 + Rng.NextDouble() * 0.45,
            };
            var brush = (SolidColorBrush)Palette[i % Palette.Length];
            var grad = new RadialGradientBrush(brush.Color, Colors.Transparent);
            grad.GradientOrigin = new Point(0.5, 0.5);
            grad.Center = new Point(0.5, 0.5);
            grad.RadiusX = 0.5;
            grad.RadiusY = 0.5;
            using var dc = o.V.RenderOpen();
            double w = o.R * 2;
            dc.DrawRectangle(grad, null, new Rect(0, 0, w, w));
            o.V.Transform = o.T;
            AddVisualChild(o.V);
            _orbs.Add(o);
        }
    }

    protected override int VisualChildrenCount => _orbs.Count;

    protected override Visual GetVisualChild(int index) => _orbs[index].V;

    private void OnRenderFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min((now - _last).TotalSeconds, 0.1);
        _last = now;
        double t = now.Ticks / 1e7;

        foreach (var o in _orbs)
        {
            o.X += o.Vx * dt;
            o.Y += o.Vy * dt;
            double bob = Math.Sin(t * 0.5 + o.Phase) * 6.0;
            double w = o.R * 2;
            if (o.X < -w) { o.X = ActualWidth + w; }
            if (o.X > ActualWidth + w) { o.X = -w; }
            if (o.Y < -w) { o.Y = ActualHeight + w; }
            if (o.Y > ActualHeight + w) { o.Y = -w; }
            o.T.X = o.X - o.R;
            o.T.Y = o.Y - o.R + bob;
            o.V.Opacity = Math.Clamp(o.BaseA + Math.Sin(t * 0.8 + o.Phase) * 0.12, 0.08, 0.85);
        }
    }
}