using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace EnCopy;

public sealed record SharePayload(string Title, Uri? WebLink, string? PlainText, IReadOnlyList<string> FilePaths);

/// <summary>
/// 调用 Windows 原生系统共享面板（IDataTransferManagerInterop）：
/// 链接按 Uri 分享、文件/图片按 StorageItem 分享、纯文本只传文本不带内部 .txt 文件；
/// 先置位 IsOpen 再激活宿主锚点窗防止 LinePanel 提前失焦收起，且不在 DataRequested 填充瞬间过早关闭。
/// </summary>
public sealed class WindowsShareService : IDisposable
{
    private static readonly Guid DataTransferManagerIid = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow([In] IntPtr appWindow);
    }

    private Window? _anchorWindow;
    private SharePayload? _activePayload;
    private bool _dataRequestedDelivered;
    private readonly DispatcherTimer _safetyTimer;

    public bool IsOpen { get; private set; }

    public WindowsShareService()
    {
        _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _safetyTimer.Tick += (_, _) => Finish();
    }

    public static SharePayload BuildPayload(IReadOnlyList<Note> notes)
    {
        if (notes.Count == 0)
        {
            return new SharePayload(AppIdentity.AppName, null, null, []);
        }

        if (notes.Count == 1)
        {
            var single = notes[0];
            if (single.Link is not null)
            {
                return new SharePayload(single.SummaryLine, single.Link, single.FullText, []);
            }
            if (single.Kind is ItemKind.Image or ItemKind.Video or ItemKind.File)
            {
                var files = single.FileTargets.Count > 0
                    ? single.FileTargets.Where(p => File.Exists(p) || Directory.Exists(p)).ToList()
                    : (File.Exists(single.FilePath) ? [single.FilePath] : []);
                return new SharePayload(single.SummaryLine, null, null, files);
            }
            return new SharePayload(
                string.IsNullOrWhiteSpace(single.SummaryLine) ? AppIdentity.AppName : single.SummaryLine,
                null,
                single.FullText,
                []);
        }

        var allFiles = new List<string>();
        var texts = new List<string>();
        foreach (var n in notes)
        {
            if (n.Kind is ItemKind.Image or ItemKind.Video or ItemKind.File)
            {
                if (n.FileTargets.Count > 0)
                {
                    allFiles.AddRange(n.FileTargets.Where(p => File.Exists(p) || Directory.Exists(p)));
                }
                else if (File.Exists(n.FilePath))
                {
                    allFiles.Add(n.FilePath);
                }
            }
            else
            {
                texts.Add(n.FullText);
            }
        }
        return new SharePayload(
            Localization.Format("{0} items", notes.Count),
            null,
            texts.Count > 0 ? string.Join(Environment.NewLine, texts) : null,
            allFiles);
    }

    public bool ShowForNotes(IReadOnlyList<Note> notes, Rect anchorDip)
    {
        if (notes.Count == 0) return false;
        _activePayload = BuildPayload(notes);
        _dataRequestedDelivered = false;

        EnsureAnchorWindow();
        if (_anchorWindow is null) return false;

        // 必须在 Activate 抢夺焦点之前先打开 IsOpen 锁，防止 LinePanel.Deactivated 误判面板失焦收起
        IsOpen = true;
        _safetyTimer.Stop();
        _safetyTimer.Start();

        _anchorWindow.Left = anchorDip.Left + anchorDip.Width / 2.0 - 2;
        _anchorWindow.Top = anchorDip.Top + anchorDip.Height / 2.0 - 2;
        _anchorWindow.Show();
        _anchorWindow.Activate();

        var hwnd = new WindowInteropHelper(_anchorWindow).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Finish();
            return false;
        }

        try
        {
            var interop = DataTransferManager.As<IDataTransferManagerInterop>();
            Guid iid = DataTransferManagerIid;
            IntPtr dtmPtr = interop.GetForWindow(hwnd, ref iid);
            var dtm = WinRT.MarshalInterface<DataTransferManager>.FromAbi(dtmPtr);
            Marshal.Release(dtmPtr);

            dtm.DataRequested -= OnDataRequested;
            dtm.DataRequested += OnDataRequested;

            interop.ShowShareUIForWindow(hwnd);
            return true;
        }
        catch (COMException)
        {
            Finish();
            return false;
        }
    }

    public void Dispose()
    {
        Finish();
        if (_anchorWindow is not null)
        {
            _anchorWindow.Close();
            _anchorWindow = null;
        }
    }

    private async void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var payload = _activePayload;
        if (payload is null) return;

        var deferral = args.Request.GetDeferral();
        try
        {
            args.Request.Data.Properties.Title = string.IsNullOrWhiteSpace(payload.Title) ? AppIdentity.AppName : payload.Title;
            if (payload.WebLink is not null)
            {
                args.Request.Data.SetWebLink(payload.WebLink);
            }
            if (!string.IsNullOrEmpty(payload.PlainText))
            {
                args.Request.Data.SetText(payload.PlainText);
            }
            if (payload.FilePaths.Count > 0)
            {
                var items = new List<IStorageItem>();
                foreach (var p in payload.FilePaths)
                {
                    if (File.Exists(p))
                    {
                        items.Add(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(p)));
                    }
                    else if (Directory.Exists(p))
                    {
                        items.Add(await StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(p)));
                    }
                }
                if (items.Count > 0)
                {
                    args.Request.Data.SetStorageItems(items);
                }
            }
            _dataRequestedDelivered = true;
        }
        catch (Exception ex)
        {
            args.Request.FailWithDisplayText(ex.Message);
            Finish();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void EnsureAnchorWindow()
    {
        if (_anchorWindow is not null) return;
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Width = 4,
            Height = 4,
            ShowInTaskbar = false,
            Topmost = true
        };
        win.Activated += (_, _) =>
        {
            // 系统共享浮层关闭后焦点退回锚点窗：此时安全收尾释放 IsOpen 锁
            if (IsOpen && _dataRequestedDelivered)
            {
                Finish();
            }
        };
        win.Deactivated += (_, _) =>
        {
            if (!IsOpen || !_dataRequestedDelivered) return;
            var checkClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            checkClose.Tick += (_, _) =>
            {
                checkClose.Stop();
                if (IsOpen && _dataRequestedDelivered)
                {
                    Finish();
                }
            };
            checkClose.Start();
        };
        _anchorWindow = win;
    }

    private void Finish()
    {
        _safetyTimer.Stop();
        _dataRequestedDelivered = false;
        IsOpen = false;
        _anchorWindow?.Hide();
    }
}
