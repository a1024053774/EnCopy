using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EnCopy.Checks;

/// <summary>
/// 隔离集成与行为契约测试：必须在 ENCOPY_CHECKS_RUNNER=1 + 临时目录下运行，
/// 严禁直接执行或读写用户真实数据目录及系统剪贴板。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var runnerFlag = Environment.GetEnvironmentVariable(DataFolder.ChecksRunnerEnv);
        var checkDir = Environment.GetEnvironmentVariable(DataFolder.ChecksDataDirEnv);
        if (!string.Equals(runnerFlag, "1", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(checkDir))
        {
            Console.Error.WriteLine("Refusing to run EnCopy.Checks directly: run powershell -ExecutionPolicy Bypass -File windows/scripts/check.ps1.");
            return 2;
        }

        var fullCheckDir = Path.GetFullPath(checkDir);
        var dailyDefault = Path.GetFullPath(DataFolder.DefaultRootForEdition("EnCopy"));
        var devDefault = Path.GetFullPath(DataFolder.DefaultRootForEdition("EnCopy Dev"));
        if (fullCheckDir.StartsWith(dailyDefault, StringComparison.OrdinalIgnoreCase) ||
            fullCheckDir.StartsWith(devDefault, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Refusing to run inside real user data folder: {fullCheckDir}");
            return 3;
        }

        Directory.CreateDirectory(fullCheckDir);
        var results = new List<(string Name, Action Run)>
        {
            ("EditionIsolation", TestEditionIsolation),
            ("ClipboardAndBurstPersistence", TestClipboardAndBurstPersistence),
            ("Fts5TrigramAndShortWordSearch", TestFts5TrigramAndShortWordSearch),
            ("KeyboardAndImeRouting", TestKeyboardAndImeRouting),
            ("PasteKeyAndUipiFallback", TestPasteKeyAndUipiFallback),
            ("ScreenPlacementMultiMonitorDpi", TestScreenPlacementMultiMonitorDpi),
            ("HotKeyConflictNotice", TestHotKeyConflictNotice),
            ("LocalizationBilingual", TestLocalizationBilingual),
            ("SettingsAndNoteParsing", TestSettingsAndNoteParsing),
            ("TracerPanelXamlAndRender", () => TestTracerPanelXamlAndRender(PreviewOutputPath(args))),
        };

        int failed = 0;
        foreach (var (name, run) in results)
        {
            var caseDir = Path.Combine(fullCheckDir, name);
            Directory.CreateDirectory(caseDir);
            DataFolder.UseTestRoot(caseDir);
            Localization.SetLanguageOverride(null);
            try
            {
                run();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {name}: {ex}");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void TestEditionIsolation()
    {
        Check(AppIdentity.IsDev, "Checks build runs under dev edition");
        var dailyDir = DataFolder.DefaultRootForEdition("EnCopy");
        var devDir = DataFolder.DefaultRootForEdition("EnCopy Dev");
        Check(!string.Equals(dailyDir, devDir, StringComparison.OrdinalIgnoreCase), "Daily and dev data folders differ");
        Check(DataFolder.Root != dailyDir && DataFolder.Root != devDir, "Checks run in isolated temp folder");
    }

    private static void TestClipboardAndBurstPersistence()
    {
        var backend = new FakeClipboardBackend();
        using var history = new History();
        var settings = new AppSettings();
        var line = new Line(history);
        using var watcher = new ClipboardWatcher(
            backend, history, settings,
            onNew: p => line.AddNew(p),
            onAgain: (p, ts) => line.Use(p, ts));
        watcher.Start();

        // 0. 剪贴板占用重试耗尽时不抛出 COMException，而是返回 fallback（P1 回归）
        int retryWaits = 0;
        var exhaustedRead = Win32ClipboardBackend.RetryClipboard<string?>(
            () => throw new System.Runtime.InteropServices.COMException("locked", unchecked((int)0x800401D0)),
            fallback: null,
            maxAttempts: 5,
            onRetryWait: () => retryWaits++);
        Check(exhaustedRead is null && retryWaits == 4, $"Exhausted clipboard read returns null after 4 retries (waits={retryWaits})");

        int writeAttempts = 0;
        var transientWrite = Win32ClipboardBackend.RetryClipboard(
            () =>
            {
                writeAttempts++;
                if (writeAttempts < 3) throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0));
                return true;
            },
            fallback: false,
            maxAttempts: 5,
            onRetryWait: () => { });
        Check(transientWrite && writeAttempts == 3, "Clipboard retry succeeds after transient lock");

        // 1. 隐私与自身标记必须过滤，不入库
        backend.Push(new ClipboardSnapshot(1, ContainsSelfCopy: true, ContainsFiles: false, IsPrivacyBlocked: false, "self-secret", null));
        backend.Push(new ClipboardSnapshot(2, ContainsSelfCopy: false, ContainsFiles: false, IsPrivacyBlocked: true, "pw-secret", null));
        Check(history.Count == 0, "SelfCopy and privacy-marked copies are ignored");

        // 2. 200ms 内连续两次不同复制（模拟 50ms 突发），两条都要进 SQLite
        var sw = Stopwatch.StartNew();
        backend.Push(new ClipboardSnapshot(3, false, false, false, "First burst note alpha", null));
        Thread.Sleep(40);
        backend.Push(new ClipboardSnapshot(4, false, false, false, "Second burst note beta", null));
        Check(sw.ElapsedMilliseconds < 200 || history.Count == 2, "Burst timing stayed under test expectation");
        Check(history.Count == 2, $"Expected 2 burst items in history, got {history.Count}");
        Check(line.ShownItems.Count == 2, "Both burst items appear on line");
        Check(line.ShownItems[0].FullText == "Second burst note beta", "Newest burst item sits at head");

        // 3. 链接与 RTF 富文本入库
        var rtfBytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Bold} word}");
        backend.Push(new ClipboardSnapshot(5, false, false, false, "Bold word", rtfBytes));
        backend.Push(new ClipboardSnapshot(6, false, false, false, "https://github.com/a1024053774/EnCopy", null));
        Check(history.Count == 4, "Link and RTF note recorded");
        Check(line.ShownItems[0].Kind == ItemKind.Link && line.ShownItems[0].Title == "github.com", "Link parsed with site title");

        // 4. 重复复制旧项不重复写文件，而是挪到最前
        backend.Push(new ClipboardSnapshot(7, false, false, false, "First burst note alpha", null));
        Check(history.Count == 4, "Duplicate text does not create a 5th row");
        Check(line.ShownItems[0].FullText == "First burst note alpha", "Re-copied item moves back to head");

        // 5. 模拟重启：重新打开 History 与 Line 仍保留全部 4 条且顺序保持
        using var historyReopened = new History();
        var lineReopened = new Line(historyReopened);
        Check(lineReopened.ShownItems.Count == 4, "Items persist across reopen");
        Check(lineReopened.ShownItems[0].FullText == "First burst note alpha", "Most recently used item remains first after reopen");

        // 6. 同毫秒突发后立刻重复复制旧项：重开 History 后最近一次复制仍排第一（P3 回归）
        backend.Push(new ClipboardSnapshot(8, false, false, false, "Same-ms item one", null));
        backend.Push(new ClipboardSnapshot(9, false, false, false, "Same-ms item two", null));
        backend.Push(new ClipboardSnapshot(10, false, false, false, "Same-ms item three", null));
        // 连续交替复现相同条目，全部落在纯 onAgain 单调时间戳路径，确保不倒挂回 UtcNow
        for (uint seq = 11; seq <= 25; seq += 3)
        {
            backend.Push(new ClipboardSnapshot(seq, false, false, false, "Same-ms item two", null));
            backend.Push(new ClipboardSnapshot(seq + 1, false, false, false, "Same-ms item three", null));
            backend.Push(new ClipboardSnapshot(seq + 2, false, false, false, "Same-ms item one", null));
        }
        using var historyBurstReopen = new History();
        var lineBurstReopen = new Line(historyBurstReopen);
        Check(lineBurstReopen.ShownItems[0].FullText == "Same-ms item one",
            $"Duplicate copy after same-ms burst must stay first after reopen (got '{lineBurstReopen.ShownItems[0].FullText}')");

        // 7. 异步突发合并复现：队列中有连续两份快照只触发一次 Changed 时，首条与次条都必须入库
        backend.QueueSnapshotWithoutNotify(new ClipboardSnapshot(100, false, false, false, "EC_RAPID_FIRST", null));
        backend.QueueSnapshotWithoutNotify(new ClipboardSnapshot(101, false, false, false, "EC_RAPID_SECOND", null));
        backend.NotifyChanged();
        Check(history.NoteHolding("EC_RAPID_FIRST") is not null && history.NoteHolding("EC_RAPID_SECOND") is not null,
            "Coalesced burst notification must persist both EC_RAPID_FIRST and EC_RAPID_SECOND");
        Check(line.ShownItems[0].FullText == "EC_RAPID_SECOND",
            $"EC_RAPID_SECOND must sit at head after coalesced burst (got '{line.ShownItems[0].FullText}')");

        // 8. 瞬时返回 null 时不能提前推进 _lastSequence，下次读到同一序号时仍应正常入库
        backend.FailNextReadsWithNull(1);
        backend.Push(new ClipboardSnapshot(102, false, false, false, "After transient lock", null));
        Check(history.NoteHolding("After transient lock") is null, "First CheckNow sees null transiently");
        watcher.CheckNow();
        Check(history.NoteHolding("After transient lock") is not null,
            "Subsequent CheckNow at same SequenceNumber must still record item after transient null read");

        // 9. 纯 Win32 HGLOBAL 读取辅助校验（零碰系统剪贴板）
        TestWin32GlobalReaders();

        // 10. 暂停/停止监听生命周期回归（P1）：Stop() 后通知后端停止入队并清空残留队列
        Check(backend.IsListening, "Backend listening is active after Start()");
        backend.QueueSnapshotWithoutNotify(new ClipboardSnapshot(103, false, false, false, "Before pause item", null));
        watcher.Stop();
        Check(!backend.IsListening && !backend.TryDequeueSnapshot(out _),
            "Stop() must disable backend capture and discard any unconsumed queued snapshots");
        backend.QueueSnapshotWithoutNotify(new ClipboardSnapshot(104, false, false, false, "Copied while paused", null));
        Check(!backend.TryDequeueSnapshot(out _), "Backend must not enqueue snapshots while paused");
        watcher.Start();
        backend.Push(new ClipboardSnapshot(105, false, false, false, "Resumed item", null));
        Check(history.NoteHolding("Copied while paused") is null && history.NoteHolding("Resumed item") is not null,
            "Paused copy stays ignored while resumed copy records cleanly");
    }

    private static void TestWin32GlobalReaders()
    {
        static T WithMoveableGlobal<T>(byte[] bytes, Func<IntPtr, T> probe)
        {
            IntPtr hMem = GlobalAlloc(0x0002, (nuint)bytes.Length);
            Check(hMem != IntPtr.Zero, "GlobalAlloc(GMEM_MOVEABLE) succeeds");
            try
            {
                IntPtr ptr = GlobalLock(hMem);
                Check(ptr != IntPtr.Zero, "GlobalLock succeeds");
                try
                {
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(hMem);
                }
                return probe(hMem);
            }
            finally
            {
                GlobalFree(hMem);
            }
        }

        // CF_UNICODETEXT 带尾随 \0 与额外对齐填充
        var unicodeBytes = Encoding.Unicode.GetBytes("EC_RAPID_你好\0extra");
        var parsedUnicode = WithMoveableGlobal(unicodeBytes, Win32ClipboardBackend.ReadUnicodeFromGlobal);
        Check(parsedUnicode == "EC_RAPID_你好", $"ReadUnicodeFromGlobal trims at null terminator (got '{parsedUnicode}')");

        // RTF 带尾随 \0 剥离后可被 Note.ExtractTextFromRtf 正常解析
        var rtfBytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Hi} Win32}" + "\0\0");
        var parsedRtf = WithMoveableGlobal(rtfBytes, Win32ClipboardBackend.ReadRtfFromGlobal);
        Check(parsedRtf is not null && parsedRtf[^1] == (byte)'}', "ReadRtfFromGlobal strips trailing null terminators");
        Check(Note.ExtractTextFromRtf(parsedRtf!) == "Hi Win32", "Stripped RTF parses cleanly");

        // CanIncludeInClipboardHistory DWORD 0 vs 1
        var zeroDword = BitConverter.GetBytes(0);
        var oneDword = BitConverter.GetBytes(1);
        Check(WithMoveableGlobal(zeroDword, Win32ClipboardBackend.IsZeroDwordGlobal), "DWORD 0 detected as privacy blocked");
        Check(!WithMoveableGlobal(oneDword, Win32ClipboardBackend.IsZeroDwordGlobal), "DWORD 1 allowed");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private static void TestFts5TrigramAndShortWordSearch()
    {
        var backend = new FakeClipboardBackend();
        using var history = new History();
        var settings = new AppSettings();
        var line = new Line(history);
        using var watcher = new ClipboardWatcher(backend, history, settings, line.AddNew, (p, ts) => line.Use(p, ts));
        watcher.Start();

        backend.Push(new ClipboardSnapshot(1, false, false, false, "周会改到周四下午三点\n带上项目进度表", null));
        backend.Push(new ClipboardSnapshot(2, false, false, false, "Menú de configuración", null));
        backend.Push(new ClipboardSnapshot(3, false, false, false, "发票 #2041：¥1,280", null));

        // 短词（2个中文字）
        line.Query = "周会";
        Check(line.ShownItems.Count == 1 && line.ShownItems[0].Title.Contains("周会"), "2-char Chinese search finds match via instr");

        // 单字
        line.Query = "票";
        Check(line.ShownItems.Count == 1 && line.ShownItems[0].Title.Contains("2041"), "1-char Chinese search finds match");

        // 长词（>=3 字符 FTS5 trigram + 重音/大小写折叠）
        line.Query = "menu";
        Check(line.ShownItems.Count == 1 && line.ShownItems[0].FullText.Contains("Menú"), "Diacritic & case folded trigram search matches Menú");

        // 混合短词 + 长词 AND 过滤
        line.Query = "周会 进度表";
        Check(line.ShownItems.Count == 1, "Combined short + long words match expected note");

        line.Query = "周会 2041";
        Check(line.ShownItems.Count == 0, "Disjoint words return empty match");

        // >500 条历史搜索回归（P2）：第 1 条落到 500 名之后仍可搜到且 ShownItems.Count == MatchCount
        var notesDir = DataFolder.NotesFolder;
        Directory.CreateDirectory(notesDir);
        var baseUtc = DateTime.UtcNow.AddMinutes(-10);
        var deepPath = Path.Combine(notesDir, "Text deep-history.txt");
        File.WriteAllText(deepPath, "DeepHistoryNeedle999 远期历史条目", new UTF8Encoding(false));
        history.Record(deepPath, "DeepHistoryNeedle999 远期历史条目", baseUtc);
        for (int i = 1; i <= 505; i++)
        {
            var p = Path.Combine(notesDir, $"Text bulk-{i:D4}.txt");
            var content = $"Bulk filler note number {i}";
            File.WriteAllText(p, content, new UTF8Encoding(false));
            history.Record(p, content, baseUtc.AddSeconds(i));
        }
        line.ReloadFromHistory();
        line.Query = "DeepHistoryNeedle999";
        Check(line.MatchCount == 1 && line.ShownItems.Count == line.MatchCount && line.ShownItems[0].FullText.Contains("DeepHistoryNeedle999"),
            $"Search beyond 500 items must return matched item (MatchCount={line.MatchCount}, ShownItems={line.ShownItems.Count})");
    }

    private static void TestKeyboardAndImeRouting()
    {
        using var history = new History();
        var line = new Line(history);
        var backend = new FakeClipboardBackend();
        using var watcher = new ClipboardWatcher(backend, history, new AppSettings(), line.AddNew, (p, ts) => line.Use(p, ts));
        watcher.Start();
        backend.Push(new ClipboardSnapshot(1, false, false, false, "older item", null));
        backend.Push(new ClipboardSnapshot(2, false, false, false, "newer item", null));
        line.PickNewest();

        int pastedPlain = -1;
        int dismissed = 0;
        int cleared = 0;

        // 组字态下方向键、回车、Esc 全都不拦截，交还输入法
        foreach (var k in new[] { Key.Up, Key.Down, Key.Enter, Key.Escape })
        {
            bool handled = TracerKeyRouter.HandleKeyDown(k, ModifierKeys.None, isImeComposing: true, "pin", line,
                p => pastedPlain = p ? 1 : 0, () => dismissed++, () => cleared++);
            Check(!handled, $"IME composing must not swallow {k}");
        }
        Check(pastedPlain == -1 && dismissed == 0 && cleared == 0, "No actions triggered during IME composition");

        // 非组字态：Down 选第二条，Up 回第一条
        Check(TracerKeyRouter.HandleKeyDown(Key.Down, ModifierKeys.None, false, "", line, _ => { }, () => { }, () => { }), "Down handled");
        Check(line.Picked?.FullText == "older item", "Down picks older item");
        Check(TracerKeyRouter.HandleKeyDown(Key.Up, ModifierKeys.None, false, "", line, _ => { }, () => { }, () => { }), "Up handled");
        Check(line.Picked?.FullText == "newer item", "Up picks newer item");

        // Shift+Enter 纯文本粘贴，Enter 普通粘贴
        Check(TracerKeyRouter.HandleKeyDown(Key.Enter, ModifierKeys.Shift, false, "", line, p => pastedPlain = p ? 1 : 0, () => { }, () => { }), "Shift+Enter handled");
        Check(pastedPlain == 1, "Shift+Enter requests plain paste");

        // 第一次 Esc 清搜索词，第二次 Esc 收起
        string q = "hello";
        Check(TracerKeyRouter.HandleKeyDown(Key.Escape, ModifierKeys.None, false, q, line, _ => { }, () => dismissed++, () => { q = ""; cleared++; }), "First Esc handled");
        Check(cleared == 1 && dismissed == 0 && q == "", "First Esc clears query without dismissing");
        Check(TracerKeyRouter.HandleKeyDown(Key.Escape, ModifierKeys.None, false, q, line, _ => { }, () => dismissed++, () => cleared++), "Second Esc handled");
        Check(dismissed == 1, "Second Esc dismisses panel");
    }

    private static void TestPasteKeyAndUipiFallback()
    {
        var strokes = PasteKey.CtrlV;
        Check(strokes.Count == 4, "Ctrl+V generates 4 key strokes");
        Check(strokes[0].KeyDown && strokes[0].VirtualKey == KeyStroke.VkLControl, "First stroke presses LCtrl");
        Check(strokes[1].KeyDown && strokes[1].VirtualKey == KeyStroke.VkV, "Second stroke presses V");
        Check(!strokes[2].KeyDown && strokes[2].VirtualKey == KeyStroke.VkV, "Third stroke releases V");
        Check(!strokes[3].KeyDown && strokes[3].VirtualKey == KeyStroke.VkLControl && strokes[3].ModifiersClearedAfter, "Final stroke releases LCtrl with zero lingering modifiers");

        using var history = new History();
        var backend = new FakeClipboardBackend();
        var line = new Line(history);
        using var watcher = new ClipboardWatcher(backend, history, new AppSettings(), line.AddNew, (p, ts) => line.Use(p, ts));
        watcher.Start();

        var rtfBytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Styled}}");
        backend.Push(new ClipboardSnapshot(1, false, false, false, "Styled", rtfBytes));

        // 普通窗口：发 Ctrl+V
        var normalPlatform = new FakePastePlatform { CanSend = true };
        var paster = new Paster(line, normalPlatform);
        Check(paster.PastePicked(new IntPtr(100), plain: false), "Normal paste succeeds");
        Check(normalPlatform.LastPlainText == "Styled" && normalPlatform.LastRtfBytes is not null, "Normal paste keeps RTF bytes");
        Check(normalPlatform.SendCalls == 1 && normalPlatform.HintCalls == 0, "Normal window receives SendInput without PasteHint");

        // ⇧⏎ 纯文本贴：去掉 RTF
        Check(paster.PastePicked(new IntPtr(100), plain: true), "Plain paste succeeds");
        Check(normalPlatform.LastPlainText == "Styled" && normalPlatform.LastRtfBytes is null, "Plain paste strips RTF");

        // 管理员窗口（UIPI 拦截）：只复制并显示提示，不发 SendInput
        var adminPlatform = new FakePastePlatform { CanSend = false };
        var adminPaster = new Paster(line, adminPlatform);
        Check(adminPaster.PastePicked(new IntPtr(200), plain: false), "Admin fallback returns true after copying");
        Check(adminPlatform.SendCalls == 0 && adminPlatform.HintCalls == 1, "UIPI-blocked window skips SendInput and triggers PasteHint");
    }

    private static void TestScreenPlacementMultiMonitorDpi()
    {
        var monitors = new List<DisplayMonitor>
        {
            new(new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1380), 1.25, true),
            new(new PixelRect(-1920, 0, 0, 1080), new PixelRect(-1920, 0, 0, 1040), 1.0, false)
        };

        var onPrimary = ScreenPlacement.TopCenterPanel(500, 300, monitors);
        Check(onPrimary.Left >= 0 && onPrimary.Left + onPrimary.Width <= 2048, $"Primary DIP bounds expected (got Left={onPrimary.Left})");
        Check(Math.Abs(onPrimary.Top - 12.0) < 0.01, "Top margin matches 12 DIP on primary");

        var onSecondary = ScreenPlacement.TopCenterPanel(-1000, 200, monitors);
        Check(onSecondary.Left < 0 && onSecondary.Left >= -1920 && onSecondary.Left + onSecondary.Width <= 0, $"Secondary DIP bounds expected (got Left={onSecondary.Left})");
    }

    private static void TestHotKeyConflictNotice()
    {
        var busyBackend = new FakeHotKeyBackend(succeeds: false);
        using var hk = new HotKey(Shortcut.Standard, () => { }, busyBackend);
        Check(!hk.IsRegistered, "HotKey cleanly reports conflict when OS registration fails");
        Check(hk.Shortcut.Display == "Alt+C", "Default shortcut display is Alt+C");
    }

    private static void TestLocalizationBilingual()
    {
        Localization.SetLanguageOverride("en");
        Check(Localization.L("Copied. Press Ctrl+V to paste.") == "Copied. Press Ctrl+V to paste.", "English copy matches");

        Localization.SetLanguageOverride("zh-Hans");
        Check(Localization.L("Copied. Press Ctrl+V to paste.") == "已复制，按 Ctrl+V 粘贴", "Simplified Chinese copy matches");
        Check(Localization.Format("{0} of {1}", 3, 10) == "3 / 10", "Chinese count format matches");
    }

    private static void TestSettingsAndNoteParsing()
    {
        var store = new SettingsStore();
        var saved = new AppSettings { RecordingPaused = true, TextEnabled = true, LinksEnabled = false };
        store.Save(saved);
        var loaded = store.Load();
        Check(loaded.RecordingPaused && loaded.TextEnabled && !loaded.LinksEnabled, "SettingsStore round-trips settings atomically");

        var codeNote = Note.Parse("Notes/Code.txt", "Notes/Code.txt", "  var x = 1;\n  return x + 2;", DateTime.Now);
        Check(codeNote.Kind == ItemKind.Code && codeNote.IsCode, "Indented semicolon block classifies as Code");

        var linkNote = Note.Parse("Notes/Link.txt", "Notes/Link.txt", "https://www.example.com/docs/intro?ref=123", DateTime.Now);
        Check(linkNote.Kind == ItemKind.Link && linkNote.Title == "example.com" && linkNote.LinkPath == "/docs/intro"
            && linkNote.FullText == "https://www.example.com/docs/intro?ref=123", "Link keeps full URL while summarizing host and path");

        var rtfBytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Hello} EnCopy}");
        var extracted = Note.ExtractTextFromRtf(rtfBytes);
        Check(extracted == "Hello EnCopy", $"RTF extracts plain text cleanly (got '{extracted}')");
    }

    private static string? PreviewOutputPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--render-preview", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static void TestTracerPanelXamlAndRender(string? previewPath)
    {
        Localization.SetLanguageOverride("zh-Hans");
        var backend = new FakeClipboardBackend();
        using var history = new History();
        var line = new Line(history);
        using var watcher = new ClipboardWatcher(backend, history, new AppSettings(), line.AddNew, (p, ts) => line.Use(p, ts));
        watcher.Start();

        backend.Push(new ClipboardSnapshot(1, false, false, false, "周会改到周四下午三点\n记得带上项目进度表", null));
        backend.Push(new ClipboardSnapshot(2, false, false, false, "  var total = items.Count;\n  return total;", null));
        backend.Push(new ClipboardSnapshot(3, false, false, false, "https://github.com/a1024053774/EnCopy", null));
        line.PickNewest();

        var panel = new TracerPanel(line, onPaste: (_, _, _) => true, onVisibilityChanged: () => { });
        try
        {
            panel.SetConflictNotice(Localization.Format("Shortcut {0} is in use by another app.", Shortcut.Standard.Display));
            var root = panel.Content as UIElement ?? throw new InvalidOperationException("TracerPanel root content is missing");
            root.Measure(new Size(680, 440));
            root.Arrange(new Rect(0, 0, 680, 440));
            root.UpdateLayout();

            var bitmap = new RenderTargetBitmap(680, 440, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            Check(bitmap.PixelWidth == 680 && bitmap.PixelHeight == 440, "Offscreen XAML render matches requested dimensions");
            Check(ms.Length > 1000, $"Offscreen XAML render produced non-trivial PNG ({ms.Length} bytes)");

            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(previewPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(previewPath, ms.ToArray());
            }
        }
        finally
        {
            panel.Close();
        }
    }

    private sealed class FakeClipboardBackend : IClipboardBackend
    {
        private readonly Queue<ClipboardSnapshot> _queue = new();
        private ClipboardSnapshot? _current;
        private int _nullReadsRemaining;
        public event EventHandler? Changed;
        public uint SequenceNumber { get; private set; }
        public bool IsListening { get; private set; }

        public void SetListening(bool enabled)
        {
            IsListening = enabled;
            if (!enabled)
            {
                _queue.Clear();
            }
        }

        public void Push(ClipboardSnapshot snap)
        {
            SequenceNumber = snap.SequenceNumber;
            _current = snap;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void QueueSnapshotWithoutNotify(ClipboardSnapshot snap)
        {
            SequenceNumber = snap.SequenceNumber;
            _current = snap;
            if (!IsListening) return;
            _queue.Enqueue(snap);
        }

        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public void FailNextReadsWithNull(int count) => _nullReadsRemaining = count;

        public bool TryDequeueSnapshot(out ClipboardSnapshot? snapshot)
        {
            if (_queue.Count > 0)
            {
                snapshot = _queue.Dequeue();
                return true;
            }
            snapshot = null;
            return false;
        }

        public ClipboardSnapshot? TryRead()
        {
            if (_nullReadsRemaining > 0)
            {
                _nullReadsRemaining--;
                return null;
            }
            return _current;
        }

        public bool TryWrite(string text, byte[]? rtfBytes)
        {
            SequenceNumber++;
            _current = new ClipboardSnapshot(SequenceNumber, ContainsSelfCopy: true, ContainsFiles: false, IsPrivacyBlocked: false, text, rtfBytes);
            return true;
        }

        public void Dispose() { }
    }

    private sealed class FakePastePlatform : IPastePlatform
    {
        public bool CanSend { get; set; } = true;
        public int SendCalls { get; private set; }
        public int HintCalls { get; private set; }
        public string? LastPlainText { get; private set; }
        public byte[]? LastRtfBytes { get; private set; }

        public bool WriteClipboard(Note note, bool plain)
        {
            LastPlainText = note.FullText;
            LastRtfBytes = !plain && File.Exists(note.FilePath) && Path.GetExtension(note.FilePath).Equals(".rtf", StringComparison.OrdinalIgnoreCase)
                ? File.ReadAllBytes(note.FilePath)
                : null;
            return true;
        }

        public void TuckAway(IntPtr restoreTargetWindow) { }
        public bool CanSendInputTo(IntPtr targetWindow) => CanSend;
        public bool SendCtrlV() { SendCalls++; return true; }
        public void ShowHint() { HintCalls++; }
    }

    private sealed class FakeHotKeyBackend : IHotKeyBackend
    {
        private readonly bool _succeeds;
        public FakeHotKeyBackend(bool succeeds) => _succeeds = succeeds;
        public bool Register(Shortcut shortcut, Action onPressed) => _succeeds;
        public void Unregister() { }
        public void Dispose() { }
    }
}
