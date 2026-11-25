using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace EnCopy;

/// <summary>
/// 面板按键路由器：IME 组字态下完整让渡方向键、Enter 与 Esc 给输入法；
/// 非组字态负责列表选择、粘贴、纯文本粘贴与两次 Esc 退回前台窗口。
/// </summary>
public static class TracerKeyRouter
{
    public static bool HandleKeyDown(
        Key key,
        ModifierKeys modifiers,
        bool isImeComposing,
        string currentQuery,
        Line line,
        Action<bool> paste,
        Action dismiss,
        Action clearQuery)
    {
        if (isImeComposing || key == Key.ImeProcessed || key == Key.DeadCharProcessed)
        {
            return false;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (!string.IsNullOrEmpty(currentQuery))
            {
                clearQuery();
            }
            else
            {
                dismiss();
            }
            return true;
        }

        if (key == Key.Enter || key == Key.Return)
        {
            if (modifiers == ModifierKeys.None)
            {
                paste(false);
                return true;
            }
            if (modifiers == ModifierKeys.Shift)
            {
                paste(true);
                return true;
            }
            return false;
        }

        if (modifiers == ModifierKeys.None)
        {
            if (key == Key.Up)
            {
                line.MovePick(-1);
                return true;
            }
            if (key == Key.Down)
            {
                line.MovePick(1);
                return true;
            }
        }

        return false;
    }
}

public partial class TracerPanel : Window
{
    private const int GcsCompStr = 0x0008;

    private readonly Line _line;
    private readonly Func<Note, IntPtr, bool, bool> _onPaste;
    private readonly Action _onVisibilityChanged;

    private IntPtr _previousForegroundHwnd;
    private bool _syncingSelection;
    private bool _isDismissing;
    private DateTime _lastDeactivatedUtc = DateTime.MinValue;
    private string? _conflictNotice;

    public bool IsSummoned => IsVisible;

    public TracerPanel(Line line, Func<Note, IntPtr, bool, bool> onPaste, Action onVisibilityChanged)
    {
        InitializeComponent();
        _line = line;
        _onPaste = onPaste;
        _onVisibilityChanged = onVisibilityChanged;

        PlaceholderText.Text = Localization.L("Search all history");
        FooterHintText.Text = Localization.L("Up/Down pick · Enter paste · Shift+Enter plain · Esc close");
        ItemsList.ItemsSource = _line.ShownItems;

        _line.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Line.Picked) or nameof(Line.MatchCount) or nameof(Line.TotalCount) or nameof(Line.Query))
            {
                SyncFromLine();
            }
        };

        QueryBox.PreviewKeyDown += QueryBox_PreviewKeyDown;
        PreviewKeyDown += TracerPanel_PreviewKeyDown;
        Deactivated += (_, _) =>
        {
            _lastDeactivatedUtc = DateTime.UtcNow;
            if (IsVisible)
            {
                Dismiss(restoreForeground: false);
            }
        };

        SyncFromLine();
    }

    public void SetConflictNotice(string? message)
    {
        _conflictNotice = message;
        UpdateConflictNotice();
    }

    public void SummonOrToggle()
    {
        if (IsVisible)
        {
            Dismiss(restoreForeground: true);
            return;
        }
        Summon();
    }

    public void SummonOrToggleFromTray()
    {
        if (IsVisible)
        {
            Dismiss(restoreForeground: true);
            return;
        }
        if ((DateTime.UtcNow - _lastDeactivatedUtc).TotalMilliseconds < 250)
        {
            return;
        }
        Summon();
    }

    public void Summon()
    {
        var selfHwnd = new WindowInteropHelper(this).Handle;
        var fg = WindowInteropHelperEx.CurrentForegroundWindow();
        if (fg != IntPtr.Zero && fg != selfHwnd && !IsTaskbarWindow(fg))
        {
            _previousForegroundHwnd = fg;
        }

        _line.Query = string.Empty;
        if (!string.Equals(QueryBox.Text, string.Empty, StringComparison.Ordinal))
        {
            QueryBox.Text = string.Empty;
        }
        _line.ReloadFromHistory();
        _line.PickNewest();
        SyncFromLine();
        UpdateConflictNotice();

        var (cx, cy) = ScreenPlacement.CurrentCursorPhysical();
        var rect = ScreenPlacement.TopCenterPanel(cx, cy, ScreenPlacement.EnumerateMonitors());
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        Show();
        Activate();
        QueryBox.Focus();
        _onVisibilityChanged();
    }

    public void Dismiss(bool restoreForeground, IntPtr? targetOverride = null)
    {
        if (!IsVisible || _isDismissing) return;
        _isDismissing = true;
        try
        {
            var target = targetOverride ?? _previousForegroundHwnd;
            _line.Query = string.Empty;
            if (!string.Equals(QueryBox.Text, string.Empty, StringComparison.Ordinal))
            {
                QueryBox.Text = string.Empty;
            }
            // 先趁面板仍处于前台窗口时把焦点归还原窗口，再 Hide() 避免失去 Win32 切焦点资格
            if (restoreForeground && target != IntPtr.Zero)
            {
                WindowInteropHelperEx.RestoreForeground(target);
            }
            Hide();
            _onVisibilityChanged();
        }
        finally
        {
            _isDismissing = false;
        }
    }

    private void TracerPanel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!QueryBox.IsKeyboardFocusWithin)
        {
            QueryBox.Focus();
            HandleRouting(e);
        }
    }

    private void QueryBox_PreviewKeyDown(object sender, KeyEventArgs e) => HandleRouting(e);

    private void HandleRouting(KeyEventArgs e)
    {
        bool composing = e.Key == Key.ImeProcessed || e.Key == Key.DeadCharProcessed || HasActiveImeCompositionString();
        bool handled = TracerKeyRouter.HandleKeyDown(
            e.Key,
            Keyboard.Modifiers,
            composing,
            QueryBox.Text,
            _line,
            paste: plain => PastePicked(plain),
            dismiss: () => Dismiss(restoreForeground: true),
            clearQuery: () =>
            {
                QueryBox.Text = string.Empty;
                _line.Query = string.Empty;
            });

        if (handled)
        {
            e.Handled = true;
        }
    }

    private void PastePicked(bool plain)
    {
        if (_line.Picked is null) return;
        _onPaste(_line.Picked, _previousForegroundHwnd, plain);
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(QueryBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _line.Query = QueryBox.Text;
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (ItemsList.SelectedItem is Note note)
        {
            _line.Picked = note;
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        QueryBox.Focus();
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_line.Picked is not null)
        {
            PastePicked(plain: false);
        }
    }

    private void SyncFromLine()
    {
        _syncingSelection = true;
        try
        {
            ItemsList.SelectedItem = _line.Picked;
            if (_line.Picked is not null)
            {
                ItemsList.ScrollIntoView(_line.Picked);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        CountText.Text = _line.IsSearching
            ? Localization.Format("{0} of {1}", _line.MatchCount, _line.TotalCount)
            : string.Empty;

        if (_line.ShownItems.Count == 0)
        {
            EmptyText.Text = _line.IsSearching ? Localization.L("Nothing matches") : Localization.L("The line is empty");
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateConflictNotice()
    {
        if (string.IsNullOrWhiteSpace(_conflictNotice))
        {
            NoticeBanner.Visibility = Visibility.Collapsed;
            NoticeText.Text = string.Empty;
        }
        else
        {
            NoticeText.Text = _conflictNotice;
            NoticeBanner.Visibility = Visibility.Visible;
        }
    }

    private bool HasActiveImeCompositionString()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        IntPtr hImc = ImmGetContext(hwnd);
        if (hImc == IntPtr.Zero) return false;
        try
        {
            int len = ImmGetCompositionString(hImc, GcsCompStr, IntPtr.Zero, 0);
            return len > 0;
        }
        finally
        {
            ImmReleaseContext(hwnd, hImc);
        }
    }

    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        if (len <= 0) return false;
        var cls = sb.ToString();
        return string.Equals(cls, "Shell_TrayWnd", StringComparison.Ordinal) ||
               string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.Ordinal);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionString(IntPtr hIMC, int dwIndex, IntPtr lpBuf, int dwBufLen);

    [DllImport("imm32.dll")]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
}
