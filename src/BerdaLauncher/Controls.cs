using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BerdaLauncher;

// ============================================================
//  TextBox with built-in placeholder text (hint shown while empty).
//  (PasswordBox is sealed in WPF, so password fields use plain
//   PasswordBox + a hint TextBlock wired in the view.)
// ============================================================
public class HintBox : TextBox
{
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(HintBox), new PropertyMetadata(""));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    static HintBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HintBox),
            new FrameworkPropertyMetadata(typeof(HintBox)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        TextChanged += (_, _) => UpdateHint();
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (GetTemplateChild("PART_Hint") is TextBlock tb)
            tb.Visibility = string.IsNullOrEmpty(Text) ? Visibility.Visible : Visibility.Collapsed;
    }
}

public static class HintHelper
{
    public static void HookPasswordHint(PasswordBox box, TextBlock hint)
    {
        void Update()
        {
            hint.Visibility = string.IsNullOrEmpty(box.Password) ? Visibility.Visible : Visibility.Collapsed;
        }

        box.PasswordChanged += (_, _) => Update();
        Update();
    }
}