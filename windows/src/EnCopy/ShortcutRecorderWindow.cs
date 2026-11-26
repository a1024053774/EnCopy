using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EnCopy;

/// <summary>
/// 全局快捷键录制窗口：按下带 Ctrl / Alt / Win 修饰键的组合生效；Esc 取消（不写入设置），Delete / Backspace 移除快捷键；
/// 单键、仅 Shift 组合或被其他程序占用的热键会在当前窗口内即时提示，不会误存非法值。
/// </summary>
public sealed class ShortcutRecorderWindow : Window
{
    private readonly Func<Shortcut?, bool> _tryApplyOnly;
    private readonly Action<Shortcut?> _commitShortcut;
    private readonly Shortcut? _originalShortcut;
    private readonly TextBlock _feedbackLabel;
    private bool _finished;

    public ShortcutRecorderWindow(
        Shortcut? currentShortcut,
        Func<Shortcut?, bool> tryApplyOnly,
        Action<Shortcut?> commitShortcut)
    {
        _originalShortcut = currentShortcut;
        _tryApplyOnly = tryApplyOnly;
        _commitShortcut = commitShortcut;

        bool dark = ThemePalette.IsDarkMode();
        var panelBg = ThemePalette.Brush(dark ? Color.FromRgb(0x22, 0x22, 0x26) : Color.FromRgb(0xFA, 0xF7, 0xF0));

        Title = Localization.L("Change Shortcut");
        Width = 390;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = panelBg;
        InputMethod.SetIsInputMethodEnabled(this, false);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        stack.Children.Add(new TextBlock
        {
            Text = Localization.L("Press the new shortcut"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemePalette.Brush(ThemePalette.ChromeText(dark))
        });

        stack.Children.Add(new TextBlock
        {
            Text = currentShortcut is { } s ? s.Display : Localization.L("None"),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = ThemePalette.Brush(ThemePalette.AccentPrimary(dark)),
            Margin = new Thickness(0, 10, 0, 6)
        });

        stack.Children.Add(new TextBlock
        {
            Text = Localization.L("Escape cancels. Delete removes the shortcut."),
            FontSize = 12,
            Foreground = ThemePalette.Brush(ThemePalette.ChromeSubText(dark)),
            TextWrapping = TextWrapping.Wrap
        });

        _feedbackLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemePalette.Brush(Color.FromRgb(0xD9, 0x45, 0x35)),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(_feedbackLabel);

        // 显式在内容容器 Border 上铺主题底色，使离屏渲染和无边框截图都能保持正确对比度
        Content = new Border
        {
            Background = panelBg,
            Child = stack
        };

        PreviewKeyDown += OnRecorderKeyDown;
        Closed += (_, _) =>
        {
            if (!_finished)
            {
                _finished = true;
                _tryApplyOnly(_originalShortcut);
            }
        };
    }

    public void CloseSilently()
    {
        _finished = true;
        Close();
    }

    private void OnRecorderKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var parsed = Shortcut.TryFromKeyInput(e.Key, e.SystemKey, Keyboard.Modifiers, out var nextShortcut, e.ImeProcessedKey);
        switch (parsed)
        {
            case ShortcutInputResult.ModifierOnly:
                return;

            case ShortcutInputResult.Cancel:
                _finished = true;
                _tryApplyOnly(_originalShortcut);
                Close();
                return;

            case ShortcutInputResult.Clear:
                _finished = true;
                _tryApplyOnly(null);
                _commitShortcut(null);
                Close();
                return;

            case ShortcutInputResult.InvalidNeedsModifier:
                ShowFeedback(Localization.L("Include Ctrl, Alt, or Win with a key."));
                return;

            case ShortcutInputResult.Valid:
                if (_tryApplyOnly(nextShortcut))
                {
                    _finished = true;
                    _commitShortcut(nextShortcut);
                    Close();
                }
                else
                {
                    ShowFeedback(Localization.Format("Shortcut {0} is in use by another app.", nextShortcut.Display));
                }
                return;
        }
    }

    private void ShowFeedback(string message)
    {
        _feedbackLabel.Text = message;
        _feedbackLabel.Visibility = Visibility.Visible;
        System.Media.SystemSounds.Beep.Play();
    }
}
