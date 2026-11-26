using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
        string? previewDir = PreviewDirArg(args);

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
            ("PaperKindsAndMarkdown", TestPaperKindsAndMarkdown),
            ("RopeSlotsPinMoveToHeadAndOverflow", TestRopeSlotsPinMoveToHeadAndOverflow),
            ("TakeDownPutBackAndMultiSelect", TestTakeDownPutBackAndMultiSelect),
            ("BasketGroupsFilterAndNavigation", TestBasketGroupsFilterAndNavigation),
            ("ShortcutRecorderAndPersistence", TestShortcutRecorderAndPersistence),
            ("LinePanelRenderAndClickThrough", () => TestLinePanelRenderAndClickThrough(previewDir)),
            ("MotionSpringsAndReducedMotion", TestMotionSpringsAndReducedMotion),
            ("SixThemesAndTrayIcon", () => TestSixThemesAndTrayIcon(previewDir)),
            ("TopEdgeHoverAndFullScreenAvoidance", TestTopEdgeHoverAndFullScreenAvoidance),
            ("HeldZoomDragAndTakeDownNeighbours", TestHeldZoomDragAndTakeDownNeighbours),
            ("FilesImagesAppRulesAndRetentionContract", () => TestFilesImagesAppRulesAndRetentionContract(previewDir)),
            ("InstallScriptIsolated", TestInstallScriptIsolated),
        };

        int failed = 0;
        foreach (var (name, run) in results)
        {
            var caseDir = Path.Combine(fullCheckDir, name);
            Directory.CreateDirectory(caseDir);
            DataFolder.UseTestRoot(caseDir);
            Localization.SetLanguageOverride(null);
            ThemePalette.SetDarkOverride(null);
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

        backend.Push(new ClipboardSnapshot(1, ContainsSelfCopy: true, ContainsFiles: false, IsPrivacyBlocked: false, "self-secret", null));
        backend.Push(new ClipboardSnapshot(2, ContainsSelfCopy: false, ContainsFiles: false, IsPrivacyBlocked: true, "pw-secret", null));
        Check(history.Count == 0, "SelfCopy and privacy-marked copies are ignored");

        var sw = Stopwatch.StartNew();
        backend.Push(new ClipboardSnapshot(3, false, false, false, "First burst note alpha", null));
        Thread.Sleep(40);
        backend.Push(new ClipboardSnapshot(4, false, false, false, "Second burst note beta", null));
        Check(sw.ElapsedMilliseconds < 200 || history.Count == 2, "Burst timing stayed under test expectation");
        Check(history.Count == 2, $"Expected 2 burst items in history, got {history.Count}");
        Check(line.ShownItems.Count == 2, "Both burst items appear on line");
        Check(line.ShownItems[0].FullText == "Second burst note beta", "Newest burst item sits at head");

        var rtfBytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Bold} word}");
        backend.Push(new ClipboardSnapshot(5, false, false, false, "Bold word", rtfBytes));
        backend.Push(new ClipboardSnapshot(6, false, false, false, "https://github.com/a1024053774/EnCopy", null));
        Check(history.Count == 4, "Link and RTF note recorded");
        Check(line.ShownItems[0].Kind == ItemKind.Link && line.ShownItems[0].Title == "github.com", "Link parsed with site title");

        backend.Push(new ClipboardSnapshot(7, false, false, false, "First burst note alpha", null));
        Check(history.Count == 4, "Duplicate text does not create a 5th row");
        Check(line.ShownItems[0].FullText == "First burst note alpha", "Re-copied item moves back to head");

        using var historyReopened = new History();
        var lineReopened = new Line(historyReopened);
        Check(lineReopened.ShownItems.Count == 4, "Items persist across reopen");
        Check(lineReopened.ShownItems[0].FullText == "First burst note alpha", "Most recently used item remains first after reopen");

        backend.Push(new ClipboardSnapshot(8, false, false, false, "Same-ms item one", null));
        backend.Push(new ClipboardSnapshot(9, false, false, false, "Same-ms item two", null));
        backend.Push(new ClipboardSnapshot(10, false, false, false, "Same-ms item three", null));
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

        line.Query = "周会";
        Check(line.ShownItems.Count == 1 && line.ShownItems[0].Title.Contains("周会"), "2-char Chinese search finds match via instr");

        line.Query = "票";
        Check(line.ShownItems.Count == 1 && line.ShownItems[0].Title.Contains("2041"), "1-char Chinese search finds match");

        line.Query = "menu";
        Check(line.ShownItems.Count == 1 && line.ShownItems[0].FullText.Contains("Menú"), "Diacritic & case folded trigram search matches Menú");

        line.Query = "周会 进度表";
        Check(line.ShownItems.Count == 1, "Combined short + long words match expected note");

        line.Query = "周会 2041";
        Check(line.ShownItems.Count == 0, "Disjoint words return empty match");

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
        line.PickDefault();

        int pastedPlain = -1;
        int copied = 0;
        int dismissed = 0;
        int cleared = 0;

        foreach (var k in new[] { Key.Left, Key.Right, Key.Up, Key.Down, Key.Enter, Key.Escape, Key.Tab })
        {
            bool handled = LineKeyRouter.HandleKeyDown(k, ModifierKeys.None, isImeComposing: true, "pin", false, line,
                p => pastedPlain = p ? 1 : 0, () => copied++, () => dismissed++, () => cleared++);
            Check(!handled, $"IME composing must not swallow {k}");
        }
        Check(pastedPlain == -1 && copied == 0 && dismissed == 0 && cleared == 0, "No actions triggered during IME composition");

        Check(LineKeyRouter.HandleKeyDown(Key.Right, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { }), "Right handled");
        Check(line.Picked?.FullText == "older item", "Right picks older item");
        Check(LineKeyRouter.HandleKeyDown(Key.Left, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { }), "Left handled");
        Check(line.Picked?.FullText == "newer item", "Left picks newer item");

        Check(LineKeyRouter.HandleKeyDown(Key.C, ModifierKeys.Control, false, "", hasQuerySelection: false, line, _ => { }, () => copied++, () => { }, () => { }), "Ctrl+C without query selection copies picked card");
        Check(copied == 1, "Copy callback invoked once");
        Check(!LineKeyRouter.HandleKeyDown(Key.C, ModifierKeys.Control, false, "abc", hasQuerySelection: true, line, _ => { }, () => copied++, () => { }, () => { }), "Ctrl+C with query text selection stays with TextBox");

        // Ctrl+Z 与 QueryBox.CanUndo 交互：输入框仍有可撤销打字时让位给 TextBox，无输入撤回时触发绳子 PutBack()
        line.PickCard(line.ShownItems[0]);
        Check(line.TakeDownPicked() && line.CanPutBack, "TakeDown prepares undo step for Ctrl+Z test");
        Check(!LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "typed", false, line, _ => { }, () => { }, () => { }, () => { }, canUndoQuery: true),
            "Ctrl+Z yields to QueryBox when canUndoQuery is true");
        Check(line.CanPutBack, "PutBack remains pending while QueryBox handles Ctrl+Z");
        Check(LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "typed", false, line, _ => { }, () => { }, () => { }, () => { }, canUndoQuery: false),
            "Ctrl+Z triggers PutBack when canUndoQuery is false even with non-empty query");
        Check(!line.CanPutBack, "PutBack executed when QueryBox has nothing to undo");

        Check(LineKeyRouter.HandleKeyDown(Key.Enter, ModifierKeys.Shift, false, "", false, line, p => pastedPlain = p ? 1 : 0, () => { }, () => { }, () => { }), "Shift+Enter handled");
        Check(pastedPlain == 1, "Shift+Enter requests plain paste");

        string q = "hello";
        Check(LineKeyRouter.HandleKeyDown(Key.Escape, ModifierKeys.None, false, q, false, line, _ => { }, () => { }, () => dismissed++, () => { q = ""; cleared++; }), "First Esc handled");
        Check(cleared == 1 && dismissed == 0 && q == "", "First Esc clears query without dismissing");
        Check(LineKeyRouter.HandleKeyDown(Key.Escape, ModifierKeys.None, false, q, false, line, _ => { }, () => { }, () => dismissed++, () => cleared++), "Second Esc handled");
        Check(dismissed == 1, "Second Esc dismisses panel");

        // CONTEXT.md / T-070：搜索框里有文字且光标不在末尾时，← → 归输入框移动光标，不切卡片或篮子分组
        line.PickDefault();
        var beforeCaretPick = line.Picked?.FullText;
        Check(!LineKeyRouter.HandleKeyDown(Key.Left, ModifierKeys.None, false, "hello", false, line, _ => { }, () => { }, () => { }, () => { }, caretAtEnd: false),
            "Left yields to QueryBox when caret is inside query");
        Check(!LineKeyRouter.HandleKeyDown(Key.Right, ModifierKeys.None, false, "hello", false, line, _ => { }, () => { }, () => { }, () => { }, caretAtEnd: false),
            "Right yields to QueryBox when caret is inside query");
        Check(line.Picked?.FullText == beforeCaretPick, "Picked card unchanged while caret moves inside query");
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

        var normalPlatform = new FakePastePlatform { CanSend = true };
        var paster = new Paster(line, normalPlatform);
        Check(paster.PastePicked(new IntPtr(100), plain: false), "Normal paste succeeds");
        Check(normalPlatform.LastPlainText == "Styled" && normalPlatform.LastRtfBytes is not null, "Normal paste keeps RTF bytes");
        Check(normalPlatform.SendCalls == 1 && normalPlatform.HintCalls == 0, "Normal window receives SendInput without PasteHint");

        Check(paster.PastePicked(new IntPtr(100), plain: true), "Plain paste succeeds");
        Check(normalPlatform.LastPlainText == "Styled" && normalPlatform.LastRtfBytes is null, "Plain paste strips RTF");

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

        var primaryStrip = ScreenPlacement.TopLineStrip(500, 300, monitors, 300);
        Check(Math.Abs(primaryStrip.Width - 2048.0) < 0.01 && Math.Abs(primaryStrip.Top) < 0.01, "Primary clothesline strip spans full DIP width at top edge");
        Check(ScreenPlacement.ComputeVisibleCapacity(primaryStrip.Width) == 10, $"Expected 10 cards fit on 2048 DIP screen (got {ScreenPlacement.ComputeVisibleCapacity(primaryStrip.Width)})");

        var secondaryStrip = ScreenPlacement.TopLineStrip(-1000, 200, monitors, 300);
        Check(Math.Abs(secondaryStrip.Left - (-1920.0)) < 0.01 && Math.Abs(secondaryStrip.Width - 1920.0) < 0.01, "Secondary strip lands on secondary monitor top edge");
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
        Check(Localization.Format("Basket {0}", 4) == "Basket 4", "English basket format matches");

        Localization.SetLanguageOverride("zh-Hans");
        Check(Localization.L("Copied. Press Ctrl+V to paste.") == "已复制，按 Ctrl+V 粘贴", "Simplified Chinese copy matches");
        Check(Localization.Format("Basket {0}", 4) == "篮子 4 件", "Chinese basket format matches");
        Check(Localization.Format("{0} of {1}", 3, 10) == "3 / 10", "Chinese count format matches");
    }

    private static void TestSettingsAndNoteParsing()
    {
        var store = new SettingsStore();
        var saved = new AppSettings { RecordingPaused = true, TextEnabled = true, LinksEnabled = false, KeepOnLine = 50 };
        store.Save(saved);
        var loaded = store.Load();
        Check(loaded.RecordingPaused && loaded.TextEnabled && !loaded.LinksEnabled && loaded.NormalizedKeepOnLine == 50,
            "SettingsStore round-trips settings atomically including KeepOnLine");

        var codeNote = Note.Parse("Notes/Code.txt", "Notes/Code.txt", "  var x = 1;\n  return x + 2;", DateTime.Now);
        Check(codeNote.Kind == ItemKind.Code && codeNote.Paper == PaperKind.Tractor && codeNote.IsCode,
            "Indented semicolon block classifies as Code/Tractor");

        var linkNote = Note.Parse("Notes/Link.txt", "Notes/Link.txt", "https://www.example.com/docs/intro?ref=123", DateTime.Now);
        Check(linkNote.Kind == ItemKind.Link && linkNote.Paper == PaperKind.Tag && linkNote.Title == "example.com" && linkNote.LinkPath == "/docs/intro"
            && linkNote.FullText == "https://www.example.com/docs/intro?ref=123", "Link parses to Tag paper with host and path");

        var rtfBytes = Encoding.ASCII.GetBytes(@"{\rtf1\ansi {\b Hello} EnCopy}");
        var extracted = Note.ExtractTextFromRtf(rtfBytes);
        Check(extracted == "Hello EnCopy", $"RTF extracts plain text cleanly (got '{extracted}')");
    }

    private static void TestPaperKindsAndMarkdown()
    {
        Localization.SetLanguageOverride("zh-Hans");

        var mdText = "# 周五采购清单\n- [x] **牛奶** 与 `面包`\n- [ ] 水果 [超市](https://example.com)";
        Check(Markdown.LooksLikeMarkdown(mdText), "Markdown heuristic detects heading + checklist + inline emphasis");

        var mdPath = Path.Combine(DataFolder.NotesFolder, "md-sample.txt");
        Directory.CreateDirectory(DataFolder.NotesFolder);
        File.WriteAllText(mdPath, mdText, new UTF8Encoding(false));
        var mdNote = Note.Load(mdPath, "Notes/md-sample.txt", DateTime.Now);
        Check(mdNote is not null && mdNote.IsMarkdown && mdNote.Paper == PaperKind.Receipt && mdNote.Title == "周五采购清单",
            "Markdown heading stripped into receipt Title while keeping FullText verbatim");
        Check(mdNote!.FullText == mdText, "Raw Markdown preserved for copy/paste");

        var inlines = Markdown.BuildInlines(mdNote.Body, 9.0, Brushes.Black, Brushes.Gray, Brushes.Brown);
        Check(inlines.Count >= 4, "Markdown.BuildInlines renders formatted runs for checklists and inline code");

        // 5 种纸张 + 视频区分 6 色 TagColor
        var pngPath = Path.Combine(DataFolder.NotesFolder, "shot.png");
        WriteSamplePng(pngPath, 160, 100);
        var mp4Path = Path.Combine(DataFolder.NotesFolder, "clip.mp4");
        File.WriteAllBytes(mp4Path, [0, 1, 2]);
        var filesListPath = Path.Combine(DataFolder.FilesFolder, "stack.filelist");
        Directory.CreateDirectory(DataFolder.FilesFolder);
        File.WriteAllLines(filesListPath, [pngPath, mp4Path], new UTF8Encoding(false));

        var imgNote = Note.Load(pngPath, "Notes/shot.png", DateTime.Now);
        var vidNote = Note.Load(mp4Path, "Notes/clip.mp4", DateTime.Now);
        var fileNote = Note.Load(filesListPath, "Files/stack.filelist", DateTime.Now);
        Check(imgNote?.Kind == ItemKind.Image && imgNote.Paper == PaperKind.Polaroid, "PNG renders as Polaroid image");
        Check(vidNote?.Kind == ItemKind.Video && vidNote.Paper == PaperKind.Polaroid, "MP4 renders as Polaroid video");
        Check(fileNote?.Kind == ItemKind.File && fileNote.Paper == PaperKind.Folder && fileNote.Title == "2 个文件",
            $"Multi-file .filelist renders as Folder with count (got '{fileNote?.Title}')");

        var distinctColors = new[]
        {
            ThemePalette.TagColor(ItemKind.Text),
            ThemePalette.TagColor(ItemKind.Code),
            ThemePalette.TagColor(ItemKind.Link),
            ThemePalette.TagColor(ItemKind.Image),
            ThemePalette.TagColor(ItemKind.Video),
            ThemePalette.TagColor(ItemKind.File)
        }.Distinct().Count();
        Check(distinctColors == 6, "All 6 ItemKind badges have distinct colors");
    }

    private static void TestRopeSlotsPinMoveToHeadAndOverflow()
    {
        var backend = new FakeClipboardBackend();
        using var history = new History();
        var line = new Line(history);
        line.SetVisibleCapacity(4);

        using var watcher = new ClipboardWatcher(backend, history, new AppSettings(), line.AddNew, (p, ts) => line.Use(p, ts));
        watcher.Start();

        var labels = new[] { "A-one", "B-two", "C-three", "D-four", "E-five", "F-six" };
        uint seq = 1;
        foreach (var label in labels)
        {
            backend.Push(new ClipboardSnapshot(seq++, false, false, false, label, null));
            Thread.Sleep(15);
        }

        // 容量 4：最新 F, E, D, C 挂在绳上；最老 A, B 落入篮子且历史记录不丢
        Check(line.LineItems.Count == 4, $"Expected 4 items on line, got {line.LineItems.Count}");
        Check(line.BasketCount == 2 && history.Count == 6, $"Expected 2 in basket out of 6 total (basket={line.BasketCount}, hist={history.Count})");
        Check(line.LineItems[0].FullText == "F-six" && line.LineItems[3].FullText == "C-three", "Loose cards hang newest-to-oldest right of knot");

        // 选中 D-four 按 Ctrl+P 收藏到绳结左（index 0）
        line.PickCard(line.LineItems[2]);
        Check(line.Picked?.FullText == "D-four", "Picked D-four before pinning");
        line.TogglePinPicked();
        Check(line.PinnedCount == 1 && line.LineItems[0].FullText == "D-four" && line.LineItems[0].Pinned,
            "Pinned item moves left of knot at index 0");

        // 新增一条 G-seven：D-four 牢牢留在 index 0，G-seven 挂到 index 1，最老非收藏 C-three 被挤入篮子
        backend.Push(new ClipboardSnapshot(seq++, false, false, false, "G-seven", null));
        Check(line.PinnedCount == 1 && line.LineItems[0].FullText == "D-four", "Pinned item never leaves left of knot");
        Check(line.LineItems[1].FullText == "G-seven" && line.LineItems[3].FullText == "E-five",
            "New item takes first loose slot after knot; oldest loose C-three overflowed to basket");
        Check(line.BasketCount == 3, "Basket now holds 3 items (A, B, C)");

        // 单击/再次复制/贴回 E-five -> 非收藏回到绳头（index 1），收藏卡 D-four 原地不动
        backend.Push(new ClipboardSnapshot(seq++, false, false, false, "E-five", null));
        Check(line.LineItems[0].FullText == "D-four", "Pinned stays at index 0");
        Check(line.LineItems[1].FullText == "E-five" && line.LineItems[2].FullText == "G-seven", "Re-copied loose item moves right after knot");

        // 再次复制篮子里的 A-one -> 召回绳头（index 1），最老非收藏 F-six 落回篮子
        backend.Push(new ClipboardSnapshot(seq++, false, false, false, "A-one", null));
        Check(line.LineItems[1].FullText == "A-one" && line.BasketCount == 3, "Re-copying basket item brings it to head of line");

        // 重开 History + Line 验证 LineState.json 持久化收藏与顺序
        using var reopenedHistory = new History();
        var reopenedLine = new Line(reopenedHistory);
        reopenedLine.SetVisibleCapacity(4);
        Check(reopenedLine.PinnedCount == 1 && reopenedLine.LineItems[0].FullText == "D-four" && reopenedLine.LineItems[1].FullText == "A-one",
            "Reopened line restores pinned position left of knot and loose order from LineState.json");

        // TiltFor 角度必须在 [-2.3, 2.3] 度内（防止 uint 下溢倒置）
        for (int i = 0; i < 30; i++)
        {
            double tilt = Line.TiltFor($"C:/notes/sample-card-{i}.txt");
            Check(Math.Abs(tilt) <= 2.3, $"Card tilt must stay near horizontal (got {tilt})");
        }

        // KeepOnLine=25 滚动后，Tab 筛选或搜索缩窄列表时必须归零/夹紧 ScrollOffset
        reopenedLine.SetKeepOnLine(25);
        for (int k = 0; k < 4; k++)
        {
            var extraPath = Path.Combine(DataFolder.NotesFolder, $"extra-scroll-{k}.txt");
            File.WriteAllText(extraPath, $"Extra scroll note {k}", new UTF8Encoding(false));
            reopenedHistory.Record(extraPath, $"Extra scroll note {k}", DateTime.UtcNow.AddSeconds(k));
            reopenedLine.AddNew(extraPath);
        }
        reopenedLine.ScrollBy(ScreenPlacement.CardSpacing * 3);
        Check(reopenedLine.ScrollOffset > 0, "ScrollBy advances ScrollOffset when items exceed VisibleCapacity");
        reopenedLine.CycleKindFilter(2); // Link
        Check(reopenedLine.ScrollOffset == 0 && reopenedLine.ScrollOffset <= reopenedLine.MaxScrollOffset(),
            $"Filtering must clamp ScrollOffset (got {reopenedLine.ScrollOffset}, max={reopenedLine.MaxScrollOffset()})");
        reopenedLine.KindFilter = KindFilter.All;

        // CardClickCoordinator 竞态回归：双击 slot>=1 的卡片时不得被 Click1 提前挪回绳头而误贴邻居
        reopenedLine.ReloadFromHistory();
        Note slot1Target = reopenedLine.ShownItems[1];
        string? doubleClickedText = null;
        int singleClickCalls = 0;
        bool timerArmed = false;
        var coord = new CardClickCoordinator(
            onSingleClick: (n, _) => { singleClickCalls++; reopenedLine.Use(n.FilePath); },
            onDoubleClick: n => doubleClickedText = n.FullText,
            scheduleTimer: () => timerArmed = true,
            cancelTimer: () => timerArmed = false);
        coord.HandleClick(slot1Target, clickCount: 1, extendSelection: false);
        Check(timerArmed && singleClickCalls == 0, "Click1 arms timer without immediately reordering line");
        coord.HandleClick(slot1Target, clickCount: 2, extendSelection: false);
        Check(!timerArmed && singleClickCalls == 0 && doubleClickedText == slot1Target.FullText,
            $"Click2 cancels single-click timer and double-clicks exact target '{slot1Target.FullText}' (got '{doubleClickedText}')");
    }

    private static void TestTakeDownPutBackAndMultiSelect()
    {
        var backend = new FakeClipboardBackend();
        using var history = new History();
        var line = new Line(history);
        line.SetVisibleCapacity(5);
        using var watcher = new ClipboardWatcher(backend, history, new AppSettings(), line.AddNew, (p, ts) => line.Use(p, ts));
        watcher.Start();

        backend.Push(new ClipboardSnapshot(1, false, false, false, "Slot-3-Alpha", null));
        Thread.Sleep(12);
        backend.Push(new ClipboardSnapshot(2, false, false, false, "Slot-2-Beta", null));
        Thread.Sleep(12);
        backend.Push(new ClipboardSnapshot(3, false, false, false, "Slot-1-Gamma", null));
        Thread.Sleep(12);
        backend.Push(new ClipboardSnapshot(4, false, false, false, "Slot-0-Delta", null));

        // 1. Shift+Right 多选：连续框选 Delta (index 0) 与 Gamma (index 1) 并按顺序合并粘贴
        line.PickCard(line.ShownItems[0]);
        LineKeyRouter.HandleKeyDown(Key.Right, ModifierKeys.Shift, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        var selected = line.GetSelectedNotesInLineOrder();
        Check(selected.Count == 2 && selected[0].FullText == "Slot-0-Delta" && selected[1].FullText == "Slot-1-Gamma",
            "Shift+Right multi-selects left-to-right cards");

        var fakePaste = new FakePastePlatform { CanSend = true };
        var paster = new Paster(line, fakePaste);
        Check(paster.PastePicked(new IntPtr(100), plain: false), "Multi-select paste succeeds");
        Check(fakePaste.LastPlainText == "Slot-0-Delta\n\nSlot-1-Gamma",
            $"Multi-select joins selected cards in line order (got '{fakePaste.LastPlainText}')");

        // 2. 多选连续框选 [1, 2] 并 TakeDown + Ctrl+Z 放回：必须严格恢复原始 [1, 2] 顺序，不能倒序
        line.PickCard(line.ShownItems[1]);
        LineKeyRouter.HandleKeyDown(Key.Right, ModifierKeys.Shift, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        string orig1 = line.LineItems[1].FullText;
        string orig2 = line.LineItems[2].FullText;
        Check(LineKeyRouter.HandleKeyDown(Key.Back, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { }),
            "Ctrl+Backspace takes down multi-selection");
        Check(line.LineItems.Count == 2 && line.CanPutBack, "Both selected cards removed from line");
        line.Query = orig2;
        Check(line.MatchCount == 0, "Taken down item no longer shows in search");
        line.Query = string.Empty;

        Check(LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { }),
            "Ctrl+Z puts multi-selected items back");
        Check(!line.CanPutBack && line.LineItems.Count == 4 && line.LineItems[1].FullText == orig1 && line.LineItems[2].FullText == orig2,
            $"PutBack must preserve exact original order ([1]='{line.LineItems[1].FullText}', [2]='{line.LineItems[2].FullText}')");

        // 3. Ctrl+P 收藏后 Ctrl+Z 撤销收藏
        line.PickCard(line.LineItems[2]);
        LineKeyRouter.HandleKeyDown(Key.P, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 1 && line.LineItems[0].FullText == orig2, "Pinned card moves left of knot");
        LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 0 && line.LineItems[2].FullText == orig2, "Ctrl+Z reverts pin back to original loose index");

        // 4. 多选 TakeDown 时第 2 件文件被外部进程独占锁定抛出 IOException：必须全批回滚，SQLite 与文件都不丢失
        line.PickCard(line.LineItems[1]);
        LineKeyRouter.HandleKeyDown(Key.Right, ModifierKeys.Shift, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        string lockedPath = line.LineItems[2].FilePath;
        using (File.Open(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Check(!line.TakeDownPicked(), "TakeDownPicked aborts cleanly when a selected file is locked");
        }
        Check(line.LineItems.Count == 4 && history.Count == 4 && File.Exists(line.LineItems[1].FilePath) && File.Exists(lockedPath),
            "IOException rollback keeps all files, SQLite rows, and line items intact");
    }

    private static void TestBasketGroupsFilterAndNavigation()
    {
        Localization.SetLanguageOverride("zh-Hans");
        using var history = new History();
        Directory.CreateDirectory(DataFolder.NotesFolder);
        Directory.CreateDirectory(DataFolder.FilesFolder);

        var now = DateTime.Now;
        void RecordNote(string name, string content, DateTime localStamp)
        {
            var p = Path.Combine(DataFolder.NotesFolder, name);
            File.WriteAllText(p, content, new UTF8Encoding(false));
            history.Record(p, content, localStamp.ToUniversalTime(), appName: "Notepad");
        }

        // 7 条历史：3 条在今天（占满容量 3 的绳子），其余 4 条按天/按月落入篮子
        RecordNote("04-month.txt", "https://docs.microsoft.com/dotnet", now.Date.AddDays(-45).AddHours(10));
        RecordNote("03-week.txt", "  int sum = 0;\n  return sum;", now.Date.AddDays(-3).AddHours(14));
        RecordNote("02-yesterday.txt", "昨天会议记录\n确认上线清单", now.Date.AddDays(-1).AddHours(16));
        RecordNote("01-today-1.txt", "今天最早一条文本", now.Date.AddHours(8));
        RecordNote("00b-today-mid.txt", "今天第二条文本", now.Date.AddHours(9));
        RecordNote("00a-today-head.txt", "最新挂在绳头", now.Date.AddHours(10));

        var filePath = Path.Combine(DataFolder.FilesFolder, "report.filelist");
        var dummyTarget = Path.Combine(DataFolder.NotesFolder, "01-today-1.txt");
        File.WriteAllLines(filePath, [dummyTarget], new UTF8Encoding(false));
        history.Record(filePath, "01-today-1.txt", now.Date.AddDays(-2).AddHours(11).ToUniversalTime());

        var line = new Line(history);
        line.SetVisibleCapacity(3); // 绳上留 3 条，其余 4 条进篮子

        Check(line.LineItems.Count == 3 && line.BasketCount == 4,
            $"Expected 3 on line and 4 in basket (line={line.LineItems.Count}, basket={line.BasketCount})");

        // Tab 循环筛选（全部 -> 文字 -> 链接 -> 图片 -> 文件 -> 全部）
        line.CycleKindFilter(1);
        Check(line.KindFilter == KindFilter.Text, "First Tab switches to Text");
        line.CycleKindFilter(1);
        Check(line.KindFilter == KindFilter.Link, "Second Tab switches to Link");
        line.CycleKindFilter(-2);
        Check(line.KindFilter == KindFilter.All, "Shift+Tab x2 returns to All");

        // 按 ↓ 展开篮子列表
        LineKeyRouter.HandleKeyDown(Key.Down, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.BasketOpen && line.BasketMatchCount == 4, "Down arrow opens basket with 4 items");
        Check(line.BasketKindCounts[KindFilter.Text] == 2, "Pre-filter chips count Text+Code together (2 items)");
        Check(line.BasketKindCounts[KindFilter.Link] == 1 && line.BasketKindCounts[KindFilter.File] == 1,
            "Chips accurately count links and files before KindFilter narrows rows");

        // 模态守卫回归：篮子空筛选（BasketPicked == null）时绝不穿透选中/贴出背后绳子上的卡
        line.Query = "NoSuchNeedle987";
        Check(line.BasketOpen && line.BasketPicked is null, "Empty basket search leaves BasketPicked null");
        Check(line.GetSelectedNotesInLineOrder().Count == 0, "Modal basket returns empty selection instead of falling through to rope");
        var guardPaste = new FakePastePlatform { CanSend = true };
        var guardPaster = new Paster(line, guardPaste);
        Check(!guardPaster.PastePicked(new IntPtr(100), false) && guardPaste.SendCalls == 0,
            "Enter with empty basket must not paste behind-the-scenes rope card");
        line.Query = string.Empty;

        // 分组顺序：昨天 -> 本周 -> 更早月份（Today 三条此时全部在绳上）
        Check(line.BasketGroups.Count == 3, $"Expected 3 date groups in basket (got {line.BasketGroups.Count})");
        Check(line.BasketGroups[0].Title == "昨天" && line.BasketGroups[1].Title == "本周",
            $"Expected 昨天 & 本周 headers (got '{line.BasketGroups[0].Title}', '{line.BasketGroups[1].Title}')");

        // 篮子里按 → 跳到「本周」第一行（report.filelist），按 Ctrl+P 收藏到绳结左，按 ← 回第一组，在首行按 ↑ 回到绳上
        var firstPicked = line.BasketPicked;
        LineKeyRouter.HandleKeyDown(Key.Right, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.BasketPicked is not null && line.BasketPicked.Kind == ItemKind.File && line.BasketPicked.FilePath != firstPicked?.FilePath,
            "Right arrow jumps to next basket group (report.filelist)");

        var displacedBeforePin = line.LineItems[2].FilePath;
        var pinnedPath = line.BasketPicked!.FilePath;
        LineKeyRouter.HandleKeyDown(Key.P, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 1 && string.Equals(line.LineItems[0].FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase),
            "Ctrl+P in basket pins picked item onto left of knot");
        Check(!line.BasketGroups.SelectMany(g => g.Rows).Any(r => string.Equals(r.Note.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)),
            "Pinned item leaves the basket list");

        // Ctrl+Z 撤销从篮子刚收藏的条目：应撤回篮子、补回被挤下绳的卡片并恢复 LineState.json，之后仍可再次 Ctrl+P 收藏上绳
        LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 0 && line.BasketGroups.SelectMany(g => g.Rows).Any(r => string.Equals(r.Note.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)),
            "Ctrl+Z after basket Ctrl+P unpins and returns item to basket");
        Check(line.LineItems.Count == 3 && string.Equals(line.LineItems[2].FilePath, displacedBeforePin, StringComparison.OrdinalIgnoreCase) && line.BasketCount == 4,
            $"Ctrl+Z after basket Ctrl+P on full line must restore displaced card onto line (count={line.LineItems.Count}, basket={line.BasketCount})");

        // 若被挤掉的卡在撤销前已被重新 Use() 拉回绳上，Ctrl+Z 取消收藏时仍需从篮子补齐满绳且不把刚取消收藏的那件拉成普通卡
        line.BasketPicked = line.BasketGroups.SelectMany(g => g.Rows).First(r => string.Equals(r.Note.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)).Note;
        LineKeyRouter.HandleKeyDown(Key.P, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        line.Use(displacedBeforePin, DateTime.UtcNow.AddSeconds(1));
        LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 0 && line.LineItems.Count == 3 &&
              !line.LineItems.Any(n => string.Equals(n.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)),
            $"Ctrl+Z after intervening Use() must backfill full line from basket without leaving {line.LineItems.Count} cards");
        line.BasketPicked = line.BasketGroups.SelectMany(g => g.Rows).First(r => string.Equals(r.Note.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)).Note;
        LineKeyRouter.HandleKeyDown(Key.P, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 1 && string.Equals(line.LineItems[0].FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase),
            "Re-pinning restored basket item succeeds cleanly");

        LineKeyRouter.HandleKeyDown(Key.Left, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        var topGroupFirstRow = line.BasketGroups[0].Rows[0].Note;
        Check(string.Equals(line.BasketPicked?.FilePath, topGroupFirstRow.FilePath, StringComparison.OrdinalIgnoreCase),
            "Left arrow jumps to first basket group row");

        // 在第 0 行按 ↑ 关闭篮子，回到绳子最前一条
        while (!string.Equals(line.BasketPicked?.FilePath, topGroupFirstRow.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            LineKeyRouter.HandleKeyDown(Key.Up, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        }
        LineKeyRouter.HandleKeyDown(Key.Up, ModifierKeys.None, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(!line.BasketOpen && line.Picked is not null, "Up arrow on first basket row closes basket and returns pick to line");

        // 调高 KeepOnLine 使绳不满且未挤掉任何卡时，从篮子 Ctrl+P 再 Ctrl+Z 绝不能把无关篮子条目拉上绳（P2 回归）
        line.SetKeepOnLine(25);
        int prePinCount = line.LineItems.Count;
        int prePinBasket = line.BasketCount;
        line.OpenBasket();
        LineKeyRouter.HandleKeyDown(Key.P, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.LineItems.Count == prePinCount + 1, "Zero-displacement basket pin adds pinned card to line");
        LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.LineItems.Count == prePinCount && line.BasketCount == prePinBasket,
            $"Zero-displacement Ctrl+Z must return exact count {prePinCount} without pulling basket items (got {line.LineItems.Count})");

        // 交叉调度不变量（[Pin -> AddNew -> Undo] 与 [Pin1 -> Pin2 -> Undo -> Undo]）：撤销篮子收藏不得用更老卡顶掉后挤下的更新卡
        using var schedHistory = new History(Path.Combine(DataFolder.Root, "schedule.sqlite"));
        string AddSchedNote(string fileName, string text, DateTime utcStamp)
        {
            var p = Path.Combine(DataFolder.NotesFolder, fileName);
            File.WriteAllText(p, text, new UTF8Encoding(false));
            schedHistory.Record(p, text, utcStamp);
            return p;
        }

        var utcNow = DateTime.UtcNow;
        AddSchedNote("sched-Y.txt", "sched-Y", utcNow.AddMinutes(-180));
        var schedP2 = AddSchedNote("sched-P2.txt", "sched-P2", utcNow.AddMinutes(-120));
        var schedP1 = AddSchedNote("sched-P1.txt", "sched-P1", utcNow.AddMinutes(-60));
        var schedZ = AddSchedNote("sched-Z.txt", "sched-Z", utcNow.AddMinutes(-30));
        AddSchedNote("sched-B.txt", "sched-B", utcNow.AddMinutes(-20));
        AddSchedNote("sched-A.txt", "sched-A", utcNow.AddMinutes(-10));

        var schedLine = new Line(schedHistory, lineStatePath: Path.Combine(DataFolder.Root, "sched-LineState.json"));
        schedLine.SetVisibleCapacity(3);
        Check(schedLine.LineItems.Count == 3 && schedLine.LineItems[2].FullText == "sched-Z", "Schedule setup holds [A, B, Z]");

        // 1) [Pin -> AddNew -> Undo]: Pin(P1) 挤下 Z -> AddNew(N) 挤下更晚的 B -> PutBack() 应优先补回更新的 B 而非更老的 Z
        schedLine.TogglePinByPath(schedP1);
        var schedN = AddSchedNote("sched-N.txt", "sched-N", utcNow.AddMinutes(-5));
        schedLine.AddNew(schedN);
        Check(schedLine.LineItems.Select(n => n.FullText).SequenceEqual(["sched-P1", "sched-N", "sched-A"]),
            "After Pin(P1) + AddNew(N), line holds [P1*, N, A]");
        Check(schedLine.PutBack(), "PutBack undoes Pin(P1)");
        Check(schedLine.PinnedCount == 0 && schedLine.LineItems.Count == 3 &&
              schedLine.LineItems.Select(n => n.FullText).SequenceEqual(["sched-N", "sched-A", "sched-B"]),
            $"[Pin -> AddNew -> Undo] must preserve loose-card recency [N, A, B] (got [{string.Join(", ", schedLine.LineItems.Select(n => n.FullText))}])");

        // 2) [Pin1 -> Pin2 -> Undo -> Undo]: 连续收藏两件后再两次 PutBack，应逐格恢复 A、B
        schedLine.TogglePinByPath(schedP1);
        schedLine.TogglePinByPath(schedP2);
        Check(schedLine.LineItems.Select(n => n.FullText).SequenceEqual(["sched-P1", "sched-P2", "sched-N"]),
            "After two basket pins, line holds [P1*, P2*, N]");
        Check(schedLine.PutBack() && schedLine.LineItems.Select(n => n.FullText).SequenceEqual(["sched-P1", "sched-N", "sched-A"]),
            "First PutBack after double pin restores A cleanly");
        Check(schedLine.PutBack() && schedLine.PinnedCount == 0 &&
              schedLine.LineItems.Select(n => n.FullText).SequenceEqual(["sched-N", "sched-A", "sched-B"]),
            "Second PutBack after double pin restores B cleanly");
    }

    private static void TestShortcutRecorderAndPersistence()
    {
        Check(Shortcut.TryFromKeyInput(Key.C, Key.None, ModifierKeys.None, out _) == ShortcutInputResult.InvalidNeedsModifier,
            "Bare letter key must be rejected");
        Check(Shortcut.TryFromKeyInput(Key.C, Key.None, ModifierKeys.Shift, out _) == ShortcutInputResult.InvalidNeedsModifier,
            "Shift-only letter combo must be rejected");
        Check(Shortcut.TryFromKeyInput(Key.LeftCtrl, Key.None, ModifierKeys.Control, out _) == ShortcutInputResult.ModifierOnly,
            "Modifier-only press waits for main key");
        Check(Shortcut.TryFromKeyInput(Key.Escape, Key.None, ModifierKeys.None, out _) == ShortcutInputResult.Cancel,
            "Plain Escape cancels recorder");
        Check(Shortcut.TryFromKeyInput(Key.Delete, Key.None, ModifierKeys.None, out _) == ShortcutInputResult.Clear,
            "Plain Delete removes shortcut");

        var parsed = Shortcut.TryFromKeyInput(Key.Space, Key.None, ModifierKeys.Control | ModifierKeys.Shift, out var custom);
        Check(parsed == ShortcutInputResult.Valid && (custom.Display == "Ctrl+Shift+空格" || custom.Display == "Ctrl+Shift+Space"),
            $"Expected valid Ctrl+Shift+Space (got {parsed}, '{custom.Display}')");

        // Alt 组合键与中文 IME 活跃状态下 ImeProcessedKey 都能正确解析
        var parsedAlt = Shortcut.TryFromKeyInput(Key.System, Key.C, ModifierKeys.Alt, out var altC);
        Check(parsedAlt == ShortcutInputResult.Valid && altC.Display == "Alt+C" && altC.VirtualKey == Shortcut.VkC,
            $"Expected valid Alt+C via Key.System (got {parsedAlt}, '{altC.Display}')");

        var parsedIme = Shortcut.TryFromKeyInput(Key.ImeProcessed, Key.None, ModifierKeys.Control | ModifierKeys.Alt, out var imeCombo, Key.K);
        Check(parsedIme == ShortcutInputResult.Valid && imeCombo.Display == "Ctrl+Alt+K",
            $"Expected valid Ctrl+Alt+K via ImeProcessedKey (got {parsedIme}, '{imeCombo.Display}')");

        // 取消或关闭录制窗口不得触动 commit 回调或重写 Settings.json
        int commitCalls = 0;
        var cancelWindow = new ShortcutRecorderWindow(Shortcut.Standard, _ => true, _ => commitCalls++);
        cancelWindow.CloseSilently();
        Check(commitCalls == 0, "Closing recorder without confirming must not persist ShortcutCustomized");

        var store = new SettingsStore();
        var s1 = new AppSettings();
        s1.SetActiveShortcut(custom);
        store.Save(s1);
        var loaded1 = store.Load();
        Check(loaded1.GetActiveShortcut() is { } restored && restored.VirtualKey == custom.VirtualKey && restored.Modifiers == custom.Modifiers,
            "Custom shortcut persists across SettingsStore reload");

        loaded1.SetActiveShortcut(null);
        store.Save(loaded1);
        var loadedNone = store.Load();
        Check(loadedNone.GetActiveShortcut() is null, "Removed shortcut persists as None rather than snapping back to Alt+C");
    }

    private static void TestLinePanelRenderAndClickThrough(string? previewDir)
    {
        Localization.SetLanguageOverride("zh-Hans");
        Directory.CreateDirectory(DataFolder.NotesFolder);
        Directory.CreateDirectory(DataFolder.FilesFolder);

        using var history = new History();
        var now = DateTime.Now;

        void AddFile(string name, string content, DateTime stamp)
        {
            var p = Path.Combine(DataFolder.NotesFolder, name);
            File.WriteAllText(p, content, new UTF8Encoding(false));
            history.Record(p, content, stamp.ToUniversalTime(), appName: "Notepad");
        }

        // 篮子条目（旧）
        AddFile("08-old-link.txt", "https://learn.microsoft.com/en-us/dotnet/desktop/wpf/", now.AddDays(-40));
        AddFile("07-old-code.txt", "  var builder = Host.CreateBuilder();\n  builder.Run();", now.AddDays(-2));
        AddFile("06-old-text.txt", "昨天的报销单已审批通过，请归档。", now.AddDays(-1));

        // 绳上 5 种纸张 + 1 件收藏
        AddFile("05-pin-note.txt", "# 常用提示词\n**请保持简短**，附真实路径 `windows/`", now.AddMinutes(-20));
        AddFile("04-code.txt", "  public sealed class Rope\n  {\n      public int Pegs => 6;\n  }", now.AddMinutes(-12));
        AddFile("03-link.txt", "https://github.com/a1024053774/EnCopy/pulls", now.AddMinutes(-8));

        var imgPath = Path.Combine(DataFolder.NotesFolder, "preview.png");
        WriteSamplePng(imgPath, 240, 140);
        history.Record(imgPath, "preview screenshot", now.AddMinutes(-5).ToUniversalTime());

        var fileStackPath = Path.Combine(DataFolder.FilesFolder, "bundle.filelist");
        File.WriteAllLines(fileStackPath, [imgPath, Path.Combine(DataFolder.NotesFolder, "04-code.txt")], new UTF8Encoding(false));
        history.Record(fileStackPath, "preview.png\n04-code.txt", now.AddMinutes(-3).ToUniversalTime());

        AddFile("01-receipt.txt", "# 发布检查\n- [x] **check.ps1** 全绿\n- [ ] 桌面走查", now.AddMinutes(-1));

        var line = new Line(history);
        line.SetVisibleCapacity(6);
        line.PickCard(line.LineItems[0]); // 选中最右下的收藏候选，Pin 到绳结左侧
        var pinTarget = line.LineItems.First(n => n.FullText.Contains("常用提示词"));
        line.TogglePinByPath(pinTarget.FilePath);
        line.PickDefault();

        var panel = new LinePanel(
            line,
            onPasteNotes: (_, _, _) => true,
            onCopyNotes: _ => true,
            onVisibilityChanged: () => { },
            onRequestChangeShortcut: () => { });

        try
        {
            panel.SetConflictNotice(Localization.Format("Shortcut {0} is in use by another app.", Shortcut.Standard.Display));

            // 1. 浅色顶部绳子 + 5 种纸 + 绳结 + 篮子把手渲染 + 穿透性验证（height 344 容纳冲突横幅 + 提示条）
            panel.RenderOffscreen(1280, 344, darkOverride: false);
            var root = (FrameworkElement)panel.Content;
            var lightBmp = RenderVisualToBitmap(root, 1280, 344);

            byte[] blankCardBandPx = SampleBgra(lightBmp, 20, 110);
            byte[] blankSearchSidePx = SampleBgra(lightBmp, 150, 245);
            byte[] searchBoxPx = SampleBgra(lightBmp, 640, 232);
            Check(blankCardBandPx[3] == 0 && VisualTreeHelper.HitTest(root, new Point(20, 110)) is null,
                $"Blank area around rope cards must be transparent click-through (alpha={blankCardBandPx[3]})");
            Check(blankSearchSidePx[3] == 0 && VisualTreeHelper.HitTest(root, new Point(150, 245)) is null,
                $"Blank area beside SearchShell must be transparent click-through (alpha={blankSearchSidePx[3]})");
            Check(searchBoxPx[3] > 200 && VisualTreeHelper.HitTest(root, new Point(640, 232)) is not null,
                $"Interactive SearchShell must be opaque and hit-testable (alpha={searchBoxPx[3]})");

            if (!string.IsNullOrWhiteSpace(previewDir))
            {
                SavePng(lightBmp, Path.Combine(previewDir, "00-offscreen-line-light.png"));
            }

            // 2. 深色模式下展开篮子列表（验证纸面保持浅色、外框与列表随深色切换）
            line.OpenBasket();
            panel.RenderOffscreen(1280, 680, darkOverride: true);
            var darkBmp = RenderVisualToBitmap(root, 1280, 680);
            if (!string.IsNullOrWhiteSpace(previewDir))
            {
                SavePng(darkBmp, Path.Combine(previewDir, "00-offscreen-basket-dark.png"));
            }

            // 3. 快捷键录制窗口离屏渲染
            var recorder = new ShortcutRecorderWindow(Shortcut.Standard, _ => true, _ => { });
            try
            {
                var recRoot = (FrameworkElement)recorder.Content;
                recRoot.Measure(new Size(390, 240));
                recRoot.Arrange(new Rect(0, 0, 390, Math.Max(140, recRoot.DesiredSize.Height)));
                recRoot.UpdateLayout();
                var recBmp = RenderVisualToBitmap(recRoot, 390, (int)Math.Ceiling(recRoot.ActualHeight));
                if (!string.IsNullOrWhiteSpace(previewDir))
                {
                    SavePng(recBmp, Path.Combine(previewDir, "00-offscreen-shortcut-recorder.png"));
                }
            }
            finally
            {
                recorder.CloseSilently();
            }
        }
        finally
        {
            panel.Close();
        }
    }

    private static void TestMotionSpringsAndReducedMotion()
    {
        try
        {
            Motion.SetReducedOverride(false);
            double tReceipt = SpringValue.MeasureSettleTime(Motion.WeightFor(PaperKind.Receipt));
            double tTag = SpringValue.MeasureSettleTime(Motion.WeightFor(PaperKind.Tag));
            double tPolaroid = SpringValue.MeasureSettleTime(Motion.WeightFor(PaperKind.Polaroid));
            double tTractor = SpringValue.MeasureSettleTime(Motion.WeightFor(PaperKind.Tractor));
            double tFolder = SpringValue.MeasureSettleTime(Motion.WeightFor(PaperKind.Folder));
            Check(tFolder > 0.1 && tReceipt > tTag && tTag > tPolaroid && tPolaroid > tTractor && tTractor > tFolder,
                $"Paper weights must settle from lightest (longest) to heaviest (fastest): receipt={tReceipt:F3}s, tag={tTag:F3}s, polaroid={tPolaroid:F3}s, tractor={tTractor:F3}s, folder={tFolder:F3}s");

            using var engine = new MotionEngine();
            Check(!engine.IsActive && !engine.IsRenderingSubscribed, "MotionEngine starts idle without rendering subscription");
            engine.BeginReveal(bySummon: true, panelHeight: 344.0);
            engine.TriggerNewCard("card-a", PaperKind.Receipt);
            engine.TriggerHop("card-a");
            Check(engine.IsActive && engine.IsRenderingSubscribed, "MotionEngine activates and subscribes to CompositionTarget.Rendering");
            Check(engine.PanelSlideY < -10.0 && engine.RopeSagScale < 0.5 && engine.GetPegScale("card-a") > 1.1 && engine.GetCardHop("card-a") < -1.0,
                "Initial spring displacements applied for panel, rope tautness, peg clamp, and hop");

            for (int frame = 0; frame < 600 && engine.IsActive; frame++)
            {
                engine.StepManual(1.0 / 120.0);
            }
            Check(!engine.IsActive && !engine.IsRenderingSubscribed, "MotionEngine stops ticking automatically once all springs settle");
            Check(Math.Abs(engine.PanelSlideY) < 0.01 && Math.Abs(engine.RopeSagScale - 1.0) < 0.01 && Math.Abs(engine.GetPegScale("card-a") - 1.0) < 0.01,
                "Settled values reach exact targets");

            var now = DateTime.UtcNow;
            Check(!Motion.CanBreeze(now, now.AddSeconds(-2.0)), "No breeze within 4s of keyboard activity");
            Check(Motion.CanBreeze(now, now.AddSeconds(-5.0)), "Breeze allowed after 4s quiet");

            var (sink0, grav0) = Motion.FallProgress(0.0);
            var (sinkMid, gravMid) = Motion.FallProgress(0.10);
            var (sinkEnd, gravEnd) = Motion.FallProgress(1.0);
            Check(sink0 == 0 && grav0 == 0 && Math.Abs(sinkMid - 5.0) < 0.05 && gravMid == 0 && Math.Abs(gravEnd - 1.0) < 0.01,
                $"FallProgress sinks 5px in first 10% then accelerates under gravity (mid={sinkMid},{gravMid}; end={sinkEnd},{gravEnd})");

            // 减弱动态效果开启：仅保留淡入淡出，无位移、无摇摆、无微风
            Motion.SetReducedOverride(true);
            Check(Motion.IsReduced, "Reduced motion override reflected");
            Check(!Motion.CanBreeze(now, now.AddSeconds(-20.0)), "Reduced motion disables breeze");
            using var reducedEngine = new MotionEngine();
            reducedEngine.BeginReveal(bySummon: true, panelHeight: 344.0);
            Check(Math.Abs(reducedEngine.PanelSlideY) < 0.001 && reducedEngine.PanelOpacity < 1.0,
                "Reduced motion fades opacity without sliding panel");
            for (int f = 0; f < 30 && reducedEngine.IsActive; f++)
            {
                reducedEngine.StepManual(1.0 / 120.0);
            }
            Check(!reducedEngine.IsActive && Math.Abs(reducedEngine.PanelOpacity - 1.0) < 0.01, "Reduced motion fade completes cleanly");
        }
        finally
        {
            Motion.SetReducedOverride(null);
        }
    }

    private static void TestSixThemesAndTrayIcon(string? previewDir)
    {
        using var history = new History();
        var line = new Line(history);
        var notesDir = DataFolder.NotesFolder;
        Directory.CreateDirectory(notesDir);

        var pText = Path.Combine(notesDir, "Text theme.txt");
        File.WriteAllText(pText, "主题外观小票\n温暖纸张与六套主题切换", new UTF8Encoding(false));
        history.Record(pText, "主题外观小票\n温暖纸张与六套主题切换", DateTime.UtcNow.AddSeconds(-40), "Notepad");

        var pCode = Path.Combine(notesDir, "Text code.txt");
        var codeStr = "public static int Add(int a, int b)\n{\n    return a + b;\n}";
        File.WriteAllText(pCode, codeStr, new UTF8Encoding(false));
        history.Record(pCode, codeStr, DateTime.UtcNow.AddSeconds(-30), "Code");

        var pLink = Path.Combine(notesDir, "Link site.txt");
        File.WriteAllText(pLink, "https://github.com/a1024053774/EnCopy", new UTF8Encoding(false));
        history.Record(pLink, "https://github.com/a1024053774/EnCopy", DateTime.UtcNow.AddSeconds(-20), "Edge");

        var pImg = Path.Combine(DataFolder.Root, "Clipboard theme.png");
        WriteSamplePng(pImg, 160, 100);
        history.Record(pImg, "Clipboard theme", DateTime.UtcNow.AddSeconds(-10), "Photos");

        for (int i = 0; i < 9; i++)
        {
            var extra = Path.Combine(notesDir, $"Text extra-{i}.txt");
            File.WriteAllText(extra, $"Basket overflow item {i}", new UTF8Encoding(false));
            history.Record(extra, $"Basket overflow item {i}", DateTime.UtcNow.AddSeconds(-100 - i));
        }

        line.ReloadFromHistory();
        line.TogglePinByPath(pText);

        var panel = new LinePanel(line, (_, _, _) => true, _ => true, () => { }, () => { });
        try
        {
            var root = (FrameworkElement)panel.Content;
            var renderedBuffers = new List<(LineTheme Theme, byte[] Pixels)>();

            foreach (var theme in Enum.GetValues<LineTheme>())
            {
                line.Theme = theme;
                line.CloseBasket();
                panel.RenderOffscreen(1280, 344, darkOverride: theme == LineTheme.Neon);
                var bmp = RenderVisualToBitmap(root, 1280, 344);
                var blankPx = SampleBgra(bmp, 24, 90);
                Check(blankPx[3] == 0, $"Theme {theme} keeps blank area alpha==0 for click-through (got {blankPx[3]})");

                var allPixels = new byte[1280 * 344 * 4];
                bmp.CopyPixels(allPixels, 1280 * 4, 0);
                renderedBuffers.Add((theme, allPixels));

                if (!string.IsNullOrWhiteSpace(previewDir))
                {
                    SavePng(bmp, Path.Combine(previewDir, $"00-offscreen-theme-{theme.ToString().ToLowerInvariant()}.png"));
                }

                line.OpenBasket();
                panel.RenderOffscreen(1280, 680, darkOverride: theme == LineTheme.Neon);
            }

            // 审查回归 1（P1）：动画帧推进期间不得销毁重建 LineCanvas 卡片实例，且原位变换切实更新
            line.Theme = LineTheme.Paper;
            line.CloseBasket();
            panel.RenderOffscreen(1280, 344);
            var canvas = (Canvas)root.FindName("LineCanvas")!;
            var firstCardBefore = canvas.Children.OfType<FrameworkElement>().First(fe => fe.Tag is string);
            panel.MotionForTest.TriggerHop(line.ShownItems[0].FilePath);
            panel.MotionForTest.StepManual(1.0 / 120.0);
            var firstCardAfter = canvas.Children.OfType<FrameworkElement>().First(fe => fe.Tag is string);
            Check(ReferenceEquals(firstCardBefore, firstCardAfter),
                "Advancing MotionEngine frames must preserve existing card FrameworkElement instances in LineCanvas");
            var cardTx = (TransformGroup)firstCardAfter.RenderTransform;
            var hopTx = cardTx.Children.OfType<TranslateTransform>().First();
            Check(hopTx.Y < -1.0, $"In-place hop transform updates card Y without rebuilding visual tree (hopY={hopTx.Y:F2})");

            for (int i = 0; i < renderedBuffers.Count; i++)
            {
                for (int j = i + 1; j < renderedBuffers.Count; j++)
                {
                    Check(!renderedBuffers[i].Pixels.AsSpan().SequenceEqual(renderedBuffers[j].Pixels),
                        $"Theme {renderedBuffers[i].Theme} and {renderedBuffers[j].Theme} must render distinct visuals");
                }
            }
        }
        finally
        {
            panel.Close();
        }

        var activeTwo = TrayIcon.RenderIconRgbaForTest(paused: false, itemCount: 2, swayAngle: 0f);
        var pausedTwo = TrayIcon.RenderIconRgbaForTest(paused: true, itemCount: 2, swayAngle: 0f);
        var activeEmpty = TrayIcon.RenderIconRgbaForTest(paused: false, itemCount: 0, swayAngle: 0f);
        var activeSway = TrayIcon.RenderIconRgbaForTest(paused: false, itemCount: 2, swayAngle: 8f);
        Check(!activeTwo.SequenceEqual(pausedTwo), "Tray icon differs between active and paused states");
        Check(!activeTwo.SequenceEqual(activeEmpty), "Tray icon differs between empty line and hanging cards");
        Check(!activeTwo.SequenceEqual(activeSway), "Tray icon pegs visibly sway when angle=8 deg");
    }

    private static void TestTopEdgeHoverAndFullScreenAvoidance()
    {
        var primary = new DisplayMonitor(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 30, 1920, 1040), 1.0, true);
        Check(TopEdgeHoverTracker.IsInTopCenterBand(960, 0, primary), "y=0 center is inside top hover band");
        Check(TopEdgeHoverTracker.IsInTopCenterBand(960, 2, primary), "y=2 center is inside top hover band");
        Check(!TopEdgeHoverTracker.IsInTopCenterBand(960, 3, primary), "y=3 is outside top hover band");
        Check(!TopEdgeHoverTracker.IsInTopCenterBand(200, 1, primary), "Left 1/3 browser tab zone does not trigger hover reveal");
        Check(!TopEdgeHoverTracker.IsInTopCenterBand(1700, 1, primary), "Right 1/3 window close button zone does not trigger hover reveal");

        var tracker = new TopEdgeHoverTracker();
        var t0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        Check(tracker.Update(960, 1, primary, mouseButtonDown: false, isRevealed: false, isPinnedBySummon: false, keepOpenForMenuOrDrag: false, panelBottomPhysicalY: 374, t0) == HoverAction.None,
            "Entering top edge starts dwell timer without immediate reveal");
        Check(tracker.Update(960, 1, primary, false, false, false, false, 374, t0.AddMilliseconds(200)) == HoverAction.None,
            "200ms dwell < 250ms threshold does not reveal yet");
        Check(tracker.Update(960, 1, primary, false, false, false, false, 374, t0.AddMilliseconds(260)) == HoverAction.RevealByHover,
            "260ms dwell >= 250ms triggers RevealByHover");

        // 鼠标按下点击顶栏时抑制触发，离开顶栏前不误滑出
        tracker.Reset();
        Check(tracker.Update(960, 1, primary, mouseButtonDown: true, false, false, false, 374, t0) == HoverAction.None, "Click at top suppresses reveal");
        Check(tracker.Update(960, 1, primary, mouseButtonDown: false, false, false, false, 374, t0.AddMilliseconds(400)) == HoverAction.None, "Remains suppressed until cursor leaves top bar");
        Check(tracker.Update(960, 100, primary, false, false, false, false, 374, t0.AddMilliseconds(450)) == HoverAction.None, "Leaving top bar clears suppression");

        // 离开已滑下面板后延时收起，但右键菜单或拖拽期间保持不收起
        Check(tracker.Update(960, 500, primary, false, isRevealed: true, isPinnedBySummon: false, keepOpenForMenuOrDrag: true, 374, t0.AddSeconds(1)) == HoverAction.None,
            "Menu/drag keeps hover-revealed panel open");
        Check(tracker.Update(960, 500, primary, false, isRevealed: true, isPinnedBySummon: false, keepOpenForMenuOrDrag: false, 374, t0.AddSeconds(2)) == HoverAction.None,
            "Leaving panel starts retract grace timer");
        Check(tracker.Update(960, 500, primary, false, isRevealed: true, isPinnedBySummon: false, keepOpenForMenuOrDrag: false, 374, t0.AddSeconds(2).AddMilliseconds(480)) == HoverAction.Retract,
            "Leaving panel >= 450ms retracts hover-revealed panel");

        // 审查回归 2（P1）：双屏下指针横向移到副屏顶部（x=-500, y=100）也必须触发主屏悬停面板延时收起
        tracker.Reset();
        Check(tracker.Update(-500, 100, primary, false, isRevealed: true, isPinnedBySummon: false, keepOpenForMenuOrDrag: false, 374, t0.AddSeconds(3)) == HoverAction.None,
            "Moving horizontally off monitor starts retract timer");
        Check(tracker.Update(-500, 100, primary, false, isRevealed: true, isPinnedBySummon: false, keepOpenForMenuOrDrag: false, 374, t0.AddSeconds(3).AddMilliseconds(480)) == HoverAction.Retract,
            "Cursor on adjacent monitor retracts hover-revealed panel after grace");

        // 全屏检测与 Alt+C 覆盖
        Check(FullScreenDetector.IsWindowCoveringMonitor(new PixelRect(0, 0, 1920, 1080), "Chrome_WidgetWin_1", primary),
            "Borderless fullscreen app window detected");
        Check(!FullScreenDetector.IsWindowCoveringMonitor(new PixelRect(0, 0, 1920, 1080), "Progman", primary),
            "Desktop Progman window ignored by fullscreen check");
        Check(!FullScreenDetector.IsWindowCoveringMonitor(new PixelRect(0, 30, 1920, 1040), "Notepad", primary),
            "Maximized work-area window is not fullscreen");
        Check(!FullScreenDetector.IsWindowCoveringMonitor(new PixelRect(0, 0, 1920, 1080), "Notepad", primary, isTitledMaximized: true),
            "Titled maximized window with auto-hidden taskbar is not misclassified as fullscreen");
        Check(FullScreenDetector.IsNotificationStateFullScreen(3) && FullScreenDetector.IsNotificationStateFullScreen(4) && !FullScreenDetector.IsNotificationStateFullScreen(5),
            "QUNS fullscreen states 3 and 4 recognized");
        Check(FullScreenDetector.ShouldSuppressAutoReveal(isMonitorFullScreen: true, showOverFullScreen: false, openedOnPurpose: false),
            "Fullscreen suppresses automatic hover/peek by default");
        Check(!FullScreenDetector.ShouldSuppressAutoReveal(isMonitorFullScreen: true, showOverFullScreen: false, openedOnPurpose: true),
            "Explicit Alt+C summon overrides fullscreen suppression");
        Check(!FullScreenDetector.ShouldSuppressAutoReveal(isMonitorFullScreen: true, showOverFullScreen: true, openedOnPurpose: false),
            "ShowOverFullScreen setting allows reveal over fullscreen");
    }

    private static void TestHeldZoomDragAndTakeDownNeighbours()
    {
        var zoom = new HoverZoomState();
        var t0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        Check(zoom.Update("a.png", t0) is null, "Hover zoom waits before opening");
        Check(zoom.Update("a.png", t0.AddMilliseconds(360)) == "a.png", "Hover zoom opens after 350ms");
        Check(zoom.Update(null, t0.AddMilliseconds(450)) == "a.png", "100ms gap between cards keeps zoom within 150ms grace");
        Check(zoom.Update(null, t0.AddMilliseconds(620)) is null, "Leaving cards >= 150ms hides zoom");

        double noteScale = 1.0;
        var startRect = new Rect(200, 150, 300, 220);
        var anchor = new Point(260, 194);
        double fxBefore = (anchor.X - startRect.Left) / startRect.Width;
        double fyBefore = (anchor.Y - startRect.Top) / startRect.Height;
        var zoomed = HeldGeometry.ZoomAroundAnchor(startRect, new Size(300, 220), isTextNote: true, ref noteScale, 1.5, anchor);
        double fxAfter = (anchor.X - zoomed.Left) / zoomed.Width;
        double fyAfter = (anchor.Y - zoomed.Top) / zoomed.Height;
        Check(zoomed.Width > 400 && Math.Abs(fxBefore - fxAfter) < 0.001 && Math.Abs(fyBefore - fyAfter) < 0.001,
            "Held zoom scales window while keeping point under cursor fixed");

        using var history = new History();
        var line = new Line(history);
        var notesDir = DataFolder.NotesFolder;
        Directory.CreateDirectory(notesDir);
        var paths = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var p = Path.Combine(notesDir, $"Text item-{i}.txt");
            File.WriteAllText(p, $"Neighbour item {i}", new UTF8Encoding(false));
            history.Record(p, $"Neighbour item {i}", DateTime.UtcNow.AddSeconds(-50 + i * 10));
            paths.Add(p);
        }
        line.ReloadFromHistory();
        line.TogglePinByPath(paths[0]);
        // Shown order: [Pinned(item-0), item-4, item-3, item-2, item-1]
        var pivot = line.ShownItems[2].FilePath; // item-3
        var leftNeighbours = line.Neighbours(pivot, toTheLeft: true);
        var rightNeighbours = line.Neighbours(pivot, toTheLeft: false);
        Check(leftNeighbours.Count == 1 && leftNeighbours[0].FullText == "Neighbour item 4",
            $"Left neighbours must exclude pinned items and include loose left cards (count={leftNeighbours.Count})");
        Check(rightNeighbours.Count == 2 && rightNeighbours[0].FullText == "Neighbour item 2" && rightNeighbours[1].FullText == "Neighbour item 1",
            $"Right neighbours must list loose right cards in order (count={rightNeighbours.Count})");

        Check(line.TakeDownNeighbours(pivot, toTheLeft: false), "TakeDownNeighbours removes right cards");
        Check(line.ShownItems.Count == 3, "2 right cards removed from line");
        Check(line.PutBack() && line.ShownItems.Count == 5 && line.ShownItems[3].FullText == "Neighbour item 2" && line.ShownItems[4].FullText == "Neighbour item 1",
            "Ctrl+Z PutBack restores taken-down neighbour cards in exact order");

        var desktopDir = Path.Combine(DataFolder.Root, "DesktopTest");
        var saved1 = line.SaveToDesktop([line.ShownItems[1]], desktopDir);
        var saved2 = line.SaveToDesktop([line.ShownItems[1]], desktopDir);
        Check(saved1.Count == 1 && File.Exists(saved1[0]) && saved2.Count == 1 && File.Exists(saved2[0]) && !string.Equals(saved1[0], saved2[0], StringComparison.OrdinalIgnoreCase),
            "SaveToDesktop copies card to Desktop with collision-free naming and preserves original note");

        using var heldMgr = new HeldWindowManager();
        var holdTarget = line.ShownItems[1];
        Check(heldMgr.Hold(holdTarget, line, _ => true, showWindow: false), "HeldWindowManager holds note");
        Check(line.HeldPaths.Contains(Path.GetFullPath(holdTarget.FilePath)), "Line tracks held card path");
        var backRect = heldMgr.HangBack(holdTarget.FilePath);
        Check(backRect is not null && !line.HeldPaths.Contains(Path.GetFullPath(holdTarget.FilePath)), "HangBack clears held state and returns departure frame");

        // 审查回归 5（P2）：被 Hold 的卡片若通过 TakeDownNotes 取下，必须同步关闭 HeldWindowManager 中的残留项
        Check(heldMgr.Hold(holdTarget, line, _ => true, showWindow: false) && heldMgr.HasOpenWindows, "Hold re-opens target");
        Check(line.TakeDownNotes([holdTarget]), "TakeDownNotes removes held card");
        Check(!heldMgr.HasOpenWindows, "Taking down a held card closes its HeldWindowManager entry");
        Check(line.PutBack(), "Restore card after held take-down test");

        // 审查回归 3（P1）：单条纯文本共享只传文本，不附带内部 .txt 临时路径
        var textSharePayload = WindowsShareService.BuildPayload([line.ShownItems[1]]);
        Check(textSharePayload.PlainText == line.ShownItems[1].FullText && textSharePayload.FilePaths.Count == 0,
            $"Single text note share must omit internal .txt file path (count={textSharePayload.FilePaths.Count})");

        // 审查回归 6（P3）：悬停放大窗顶部必须位于搜索框底边之下，绝不遮挡 SearchShell
        double zoomTop = HoverZoomController.ComputeZoomWindowTopDip(workTopDip: 30, panelContentBottomDip: 298, workHeightDip: 1010, zoomWindowHeightDip: 260);
        Check(zoomTop >= 306, $"HoverZoomController top ({zoomTop}) must sit below SearchShell bottom (298)");

        // 审查回归 4（P1）：私有图片可移动/入回收站，而 .files/.filelist 清单与外部文件不可入回收站；IFileOperation 记真实回收站路径
        var privImg = Path.Combine(DataFolder.Root, "Clipboard movable.png");
        WriteSamplePng(privImg, 80, 60);
        var manifestFake = Path.Combine(DataFolder.FilesFolder, "Copied 1.filelist");
        Directory.CreateDirectory(DataFolder.FilesFolder);
        File.WriteAllText(manifestFake, privImg, new UTF8Encoding(false));
        Check(DataFolder.IsMovablePrivateItem(privImg), "Private copied image is movable/trashable");
        Check(!DataFolder.IsMovablePrivateItem(manifestFake), "Internal filelist manifest is never trashable");
        Check(!DataFolder.IsMovablePrivateItem(Path.Combine(Path.GetTempPath(), "outside-encopy.txt")), "External file outside DataFolder is never trashable");

        history.Record(privImg, "Clipboard movable", DateTime.UtcNow);
        line.AddNew(privImg);
        var imgNote = line.ShownItems.First(n => string.Equals(n.FilePath, privImg, StringComparison.OrdinalIgnoreCase));

        static bool DiscardedIsEmpty() =>
            !Directory.Exists(DataFolder.DiscardedFolder) ||
            !Directory.EnumerateFileSystemEntries(DataFolder.DiscardedFolder).Any();

        // 复审回归 P2：移入系统回收站失败时绝不能静默退化到私有 DiscardedFolder
        RecycleBinInterop.SetSimulateFailureForChecks(true);
        try
        {
            Check(!line.TakeDownNotes([imgNote], sendToRecycleBin: true), "Failed Recycle Bin operation must return false instead of silently stashing");
            Check(File.Exists(privImg) && DiscardedIsEmpty(), "Failed Recycle Bin operation leaves file in place without DiscardedFolder fallback");
        }
        finally
        {
            RecycleBinInterop.SetSimulateFailureForChecks(false);
        }

        Check(line.TakeDownNotes([imgNote], sendToRecycleBin: true), "TakeDownNotes sends private image to system Recycle Bin");
        Check(!File.Exists(privImg) && DiscardedIsEmpty(), "Recycled private image moved to system Recycle Bin without DiscardedFolder copy");
        Check(line.PutBack() && File.Exists(privImg), "Ctrl+Z PutBack restores recycled image back to DataFolder");

        // 复审回归 P1：外部拖拽落入桌面回收站（Explorer 已将文件移入 $Recycle.Bin）后对账必须生成 Ctrl+Z 撤销记录
        var dragImgNote = line.ShownItems.First(n => string.Equals(n.FilePath, privImg, StringComparison.OrdinalIgnoreCase));
        Check(RecycleBinInterop.MoveFileToRecycleBin(privImg, out var explorerRecycledPath) && !string.IsNullOrEmpty(explorerRecycledPath),
            "Explorer drop moves private image into $Recycle.Bin");
        line.PruneMissingFromLine();
        Check(!line.ShownItems.Any(n => string.Equals(n.FilePath, privImg, StringComparison.OrdinalIgnoreCase)),
            "Dragged-to-Recycle-Bin card leaves the line");
        Check(line.PutBack() && File.Exists(privImg) && line.ShownItems.Any(n => string.Equals(n.FilePath, privImg, StringComparison.OrdinalIgnoreCase)),
            "Ctrl+Z PutBack restores card dragged into Recycle Bin");

        // 审查回归 5b（P2）：损坏图片执行 Hold 不抛异常
        var corruptImg = Path.Combine(DataFolder.Root, "Corrupt.png");
        File.WriteAllBytes(corruptImg, [0x00, 0x01, 0x02, 0x03]);
        var corruptNote = Note.Load(corruptImg, "Corrupt.png", DateTime.Now)!;
        Check(heldMgr.Hold(corruptNote, line, _ => true, showWindow: false), "Corrupt image Hold degrades safely without throwing");
        heldMgr.HangBack(corruptImg);

        var linkNote = Note.Parse(Path.Combine(notesDir, "Link s.txt"), "Notes/Link s.txt", "https://example.com/docs", DateTime.Now);
        var sharePayload = WindowsShareService.BuildPayload([linkNote]);
        Check(sharePayload.WebLink == new Uri("https://example.com/docs"), "Share payload extracts Uri for link cards");
    }

    private static void TestFilesImagesAppRulesAndRetentionContract(string? previewDir)
    {
        Localization.SetLanguageOverride("zh-Hans");
        var extDir = Path.Combine(Path.GetTempPath(), "encopy-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extDir);
        try
        {
            var f1 = Path.Combine(extDir, "alpha.txt");
            var f2 = Path.Combine(extDir, "beta.pdf");
            var f3 = Path.Combine(extDir, "gamma.csv");
            File.WriteAllText(f1, "Alpha content");
            File.WriteAllText(f2, "Beta content");
            File.WriteAllText(f3, "Gamma content");

            var samplePngPath = Path.Combine(extDir, "sample-clip.png");
            WriteSamplePng(samplePngPath, 160, 120);
            var pngBytes = File.ReadAllBytes(samplePngPath);

            using var history = new History();
            var settingsStore = new SettingsStore();
            var settings = new AppSettings
            {
                ImagesEnabled = true,
                FilesEnabled = true,
                TextEnabled = true,
                LinksEnabled = true
            };
            var line = new Line(history);
            line.SetVisibleCapacity(3);

            var backend = new FakeClipboardBackend();
            var watcher = new ClipboardWatcher(
                backend,
                history,
                settings,
                onNew: p => line.AddNew(p),
                onAgain: (p, ts) => line.Use(p, ts));
            watcher.Start();

            // 1. 复制图片入库与字节哈希去重回绳头
            uint seq = 1;
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, null, null, null, pngBytes, @"C:\Windows\System32\mspaint.exe"));
            Check(line.LineItems.Count == 1 && line.LineItems[0].Kind == ItemKind.Image, "Copied PNG hangs on the line as Polaroid image");
            var savedClipPng = line.LineItems[0].FilePath;
            Check(DataFolder.Contains(savedClipPng) && File.Exists(savedClipPng), "Copied PNG stored inside DataFolder\\Screenshots");

            // 再复制一条普通文本把图片挤到第二格，随后重复制同一张 PNG 验证去重且挪回绳头
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Intervening text note", null, null, null, @"C:\Windows\System32\notepad.exe"));
            long countBeforeReCopy = history.Count;
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, null, null, null, pngBytes, @"C:\Windows\System32\mspaint.exe"));
            Check(history.Count == countBeforeReCopy && string.Equals(line.LineItems[0].FilePath, savedClipPng, StringComparison.OrdinalIgnoreCase),
                "Re-copying identical PNG deduplicates without creating a second file and moves card back to rope head");

            // 2. 资源管理器单/多文件 .files 清单、路径集合无序去重、篮子 Ctrl+↓ 展开栈单挑粘贴与「已移走」灰化
            backend.Push(new ClipboardSnapshot(++seq, false, true, false, null, null, [f1, f2, f3], null, @"C:\Windows\explorer.exe"));
            var stackNote = line.LineItems[0];
            Check(stackNote.Kind == ItemKind.File && FileList.IsManifestPath(stackNote.FilePath) && stackNote.Title == "3 个文件",
                $"Expected 3-file .files manifest card titled '3 个文件', got '{stackNote.Title}'");
            Check(!DataFolder.Contains(f1) && File.Exists(f1), "Original Explorer files remain untouched outside DataFolder");

            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Push stack note 1", null));
            long countBeforeReverseFiles = history.Count;
            backend.Push(new ClipboardSnapshot(++seq, false, true, false, null, null, [f3, f1, f2], null, @"C:\Windows\explorer.exe"));
            Check(history.Count == countBeforeReverseFiles && string.Equals(line.LineItems[0].FilePath, stackNote.FilePath, StringComparison.OrdinalIgnoreCase),
                "Copying the same files in different order deduplicates .files manifest and moves back to rope head");

            // 连续复制 3 条普通文本把 3-文件清单挤进篮子
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Overflow push A", null));
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Overflow push B", null));
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Overflow push C", null));
            line.OpenBasket();
            var inBasketStack = line.BasketGroups.SelectMany(g => g.Rows).First(r => string.Equals(r.Note.FilePath, stackNote.FilePath, StringComparison.OrdinalIgnoreCase)).Note;
            line.SelectBasketRow(inBasketStack);

            bool stackOpened = LineKeyRouter.HandleKeyDown(
                Key.Down,
                ModifierKeys.Control,
                isImeComposing: false,
                currentQuery: string.Empty,
                hasQuerySelection: false,
                line,
                paste: _ => { },
                copyPicked: () => { },
                dismiss: () => { },
                clearQuery: () => { });
            Check(stackOpened && line.BasketExpandedPath is not null, "Ctrl+Down expands multi-file stack in basket");

            line.VerticalArrow(1); // 第 1 个子文件 alpha.txt
            line.VerticalArrow(1); // 第 2 个子文件 beta.pdf
            Check(string.Equals(line.BasketSubPickedFile, Path.GetFullPath(f2), StringComparison.OrdinalIgnoreCase),
                $"Downx2 inside expanded stack selects second file beta.pdf, got '{line.BasketSubPickedFile}'");

            var fakePaste = new FakePastePlatform();
            var paster = new Paster(line, fakePaste);
            Check(paster.PastePicked(new IntPtr(1001)), "Pasting single sub-picked file from expanded stack succeeds");
            Check(fakePaste.LastWrittenFiles is { Count: 1 } wf && string.Equals(wf[0], Path.GetFullPath(f2), StringComparison.OrdinalIgnoreCase),
                "Single-file stack paste writes only beta.pdf to clipboard FileDropList");
            line.CloseBasket();

            // 验证图片粘贴写剪贴板
            Check(paster.WriteToClipboard(Note.Load(savedClipPng, "Screenshots/" + Path.GetFileName(savedClipPng), DateTime.Now)!),
                "Image card writes bitmap/PNG to clipboard");
            Check(string.Equals(fakePaste.LastWrittenImagePath, savedClipPng, StringComparison.OrdinalIgnoreCase),
                "WriteClipboardImage received exact private PNG path");

            // 删除全部外部原文件后验证清单标为「已移走」且拒绝贴出坏路径
            File.Delete(f1);
            File.Delete(f2);
            File.Delete(f3);
            var movedNote = Note.Load(stackNote.FilePath, "Files/" + Path.GetFileName(stackNote.FilePath), DateTime.Now)!;
            Check(movedNote.IsMovedAway, "Deleting all target files marks .files manifest Note.IsMovedAway = true");
            Check(!paster.WriteToClipboard(movedNote), "Moved-away file manifest refuses to write broken paths to clipboard");

            // 3. 来源 App 三档（预设密码管理器不记 / 自定义不记 / 一小时后清）与 4–8 位验证码识别及 10 分钟直接过期
            long countBeforeSkip = history.Count;
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "vault-secret-999", null, null, null, @"C:\Program Files\Bitwarden\Bitwarden.exe"));
            Check(history.Count == countBeforeSkip, "Preset password manager (Bitwarden) is skipped out of the box");

            SourceApp.SetRule(settings, @"C:\Windows\System32\notepad.exe", AppRule.Skip);
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Skipped notepad text", null, null, null, @"C:\Windows\System32\notepad.exe"));
            Check(history.Count == countBeforeSkip, "App configured as Don't record (Skip) leaves zero trace in History");

            SourceApp.SetRule(settings, @"C:\Windows\System32\notepad.exe", AppRule.Hourly);
            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "Hourly temporary note", null, null, null, @"C:\Windows\System32\notepad.exe"));
            var hourlyPath = line.LineItems[0].FilePath;
            Check(history.GetTtl(hourlyPath) == 3600.0, "Hourly app rule assigns 3600s TTL");
            SourceApp.SetRule(settings, @"C:\Windows\System32\notepad.exe", AppRule.Normal);

            Check(ClipboardWatcher.IsVerificationCode("您的登录验证码是 482910，5分钟内有效", @"C:\Program Files\WeChat\WeChat.exe"), "Detects Chinese verification code");
            Check(ClipboardWatcher.IsVerificationCode("Your security code: 739201", @"C:\Program Files\Google\Chrome\Application\chrome.exe"), "Detects English verification code");
            Check(ClipboardWatcher.IsVerificationCode("826415", @"C:\Program Files\WindowsApps\olk.exe"), "Detects bare 6-digit code from Outlook");
            Check(!ClipboardWatcher.IsVerificationCode("HTTP status code 404", null), "Excludes HTTP status code");
            Check(!ClipboardWatcher.IsVerificationCode("My postal code 10001", null), "Excludes postal code");
            Check(!ClipboardWatcher.IsVerificationCode("See ticket T-1024 code review", null), "Excludes ticket identifier T-1024");

            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "登录验证码 482910", null, null, null, @"C:\Windows\System32\notepad.exe"));
            var codePath1 = line.LineItems[0].FilePath;
            Check(history.GetTtl(codePath1) == 600.0, "Verification code gets 600s (10-min) TTL");

            backend.Push(new ClipboardSnapshot(++seq, false, false, false, "登录验证码 918273", null, null, null, @"C:\Windows\System32\notepad.exe"));
            var codePathPinned = line.LineItems[0].FilePath;
            line.TogglePinByPath(codePathPinned);
            Check(history.GetTtl(codePathPinned) is null, "Pinning a verification code clears its TTL so it never expires");

            int clearedBatchCountBeforeTtl = line.Clearing.Batches().Count;
            int expiredRemoved = line.ClearExpired(DateTime.UtcNow.AddMinutes(11));
            Check(expiredRemoved >= 1 && !File.Exists(codePath1) && !history.Contains(codePath1),
                "Unpinned verification code is deleted directly after 10 minutes");
            Check(File.Exists(codePathPinned) && history.Contains(codePathPinned),
                "Pinned verification code survives past 10 minutes");
            Check(line.Clearing.Batches().Count == clearedBatchCountBeforeTtl,
                "Expired verification code does NOT enter Recently Cleared");

            // 1 小时后按 App 一小时规则的条目同样直接删除不进 Recently Cleared
            line.ClearExpired(DateTime.UtcNow.AddMinutes(65));
            Check(!File.Exists(hourlyPath) && line.Clearing.Batches().Count == clearedBatchCountBeforeTtl,
                "Hourly app item deletes directly after 60m without entering Recently Cleared");

            // 4. 保留规则：原图超上限或满 30 天缩成 <=640px 缩略图并保留原图识别词；文字超 1 年与失效清单进「最近清掉」
            var bigImgPath = Path.Combine(DataFolder.ScreenshotsFolder, "BigOriginal.png");
            WritePatternPng(bigImgPath, 1400, 980);
            long origByteLen = new FileInfo(bigImgPath).Length;
            history.RecordImage(bigImgPath, ocrWords: "报销凭证号 INV998877", pngBytesForHash: File.ReadAllBytes(bigImgPath), usedAt: DateTime.UtcNow.AddDays(-5), appName: @"C:\Windows\System32\mspaint.exe");

            var oldNotePath = Path.Combine(DataFolder.NotesFolder, "OldYearNote.txt");
            File.WriteAllText(oldNotePath, "Note from two years ago", new UTF8Encoding(false));
            history.Record(oldNotePath, "Note from two years ago", usedAt: DateTime.UtcNow.AddDays(-400), appName: @"C:\Windows\System32\notepad.exe");

            var retPlan = line.RunRetention(DateTime.UtcNow, capBytes: Math.Max(1024, origByteLen / 3));
            Check(retPlan.ShrunkPaths.Any(p => string.Equals(p, bigImgPath, StringComparison.OrdinalIgnoreCase)),
                "Retention shrinks private image when total unshrunk originals exceed OriginalsCapBytes");
            Check(history.IsShrunk(bigImgPath), "Shrunk image marked shrunk=1 in History");

            using (var shrunkStream = File.OpenRead(bigImgPath))
            {
                var dec = BitmapDecoder.Create(shrunkStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                Check(dec.Frames[0].PixelWidth == Retention.ThumbnailMaxPixels && new FileInfo(bigImgPath).Length < origByteLen,
                    $"Shrunk image scaled to {Retention.ThumbnailMaxPixels}px long edge ({dec.Frames[0].PixelWidth}x{dec.Frames[0].PixelHeight}) and smaller on disk");
            }
            var ocrFound = history.Search(SearchText.Words("INV998877"), [], limit: 20);
            Check(ocrFound.Basket.Any(p => string.Equals(p, bigImgPath, StringComparison.OrdinalIgnoreCase)),
                "Shrinking original image strictly preserves its indexed OCR search words");

            Check(!File.Exists(oldNotePath) && !File.Exists(stackNote.FilePath),
                "365-day expired note and missing-target .files manifest moved into Recently Cleared");
            var expiredBatch = line.Clearing.Batches().FirstOrDefault(b => b.Reason == "expired");
            Check(expiredBatch is not null && expiredBatch.Entries.Count >= 2, "Recently Cleared records expired batch");

            // 5. 手动清理面板（预览件数/字节估算与实际一致、Ctrl+Shift+Backspace 快捷键、7 天整批放回）
            bool cleanupShortcutInvoked = false;
            Check(LineKeyRouter.HandleKeyDown(
                Key.Back,
                ModifierKeys.Control | ModifierKeys.Shift,
                isImeComposing: false,
                currentQuery: string.Empty,
                hasQuerySelection: false,
                line,
                paste: _ => { },
                copyPicked: () => { },
                dismiss: () => { },
                clearQuery: () => { },
                openCleanup: () => cleanupShortcutInvoked = true) && cleanupShortcutInvoked,
                "Ctrl+Shift+Backspace triggers Clean Up History callback");

            var cleanupWin = new CleanupWindow(line);
            cleanupWin.SetFilterForTest(new CleanupFilter(Kind: KindFilter.Image, OlderThanDays: 1.0, App: "mspaint", QueryText: "INV998877", IncludePinnedAndOnLine: false));
            var preview = cleanupWin.CurrentPreviewForTest;
            long expectedBytes = preview.CandidatePaths.Where(File.Exists).Sum(p => new FileInfo(p).Length);
            Check(preview.CandidatePaths.Count == 1 && preview.EstimatedFreedBytes == expectedBytes && expectedBytes > 0,
                "Cleanup preview count and estimated freed bytes match candidate files on disk");

            var manualBatch = cleanupWin.ExecuteCurrentCleanup();
            Check(manualBatch is not null && manualBatch.Entries.Count == preview.CandidatePaths.Count && !File.Exists(bigImgPath),
                "Cleanup execution moves exact previewed candidates into Recently Cleared");

            var restoredPaths = line.PutBackClearedBatch(manualBatch!.Id);
            Check(restoredPaths.Count == 1 && File.Exists(bigImgPath) && history.IsShrunk(bigImgPath),
                "PutBack restores cleared image file and shrunk flag");
            Check(history.Search(SearchText.Words("INV998877"), [], limit: 10).Basket.Count == 1,
                "PutBack restores image OCR words and original metadata");

            // 6. 五页主题化设置窗口持久化与离屏渲染落盘
            var settingsWin = new SettingsWindow(
                settings,
                line,
                new SettingsCallbacks(
                    SaveAndApplySettings: () => settingsStore.Save(settings),
                    ChangeShortcut: after => after(),
                    OpenCleanup: () => { },
                    OpenScreenshotsFolder: () => { },
                    OpenDataFolder: () => { }),
                frontApp: @"C:\Windows\System32\notepad.exe");

            var outDir = previewDir ?? Path.Combine(FindRepoRoot(), "evidence", "T-061");
            Directory.CreateDirectory(outDir);
            for (int tab = 0; tab < 5; tab++)
            {
                settingsWin.SelectTab(tab);
                settingsWin.RenderOffscreen(610, 640);
                SaveVisualPng(settingsWin, 610, 640, Path.Combine(outDir, $"00-offscreen-settings-tab{tab}.png"));
            }

            settings.SetActiveTheme(LineTheme.Neon);
            line.Theme = LineTheme.Neon;
            settingsStore.Save(settings);
            settingsWin.SelectTab(3);
            settingsWin.RenderOffscreen(610, 640);
            SaveVisualPng(settingsWin, 610, 640, Path.Combine(outDir, "00-offscreen-settings-neon.png"));

            cleanupWin.RenderOffscreen(530, 650);
            SaveVisualPng(cleanupWin, 530, 650, Path.Combine(outDir, "00-offscreen-cleanup.png"));

            var reloadedSettings = settingsStore.Load();
            Check(reloadedSettings.GetActiveTheme() == LineTheme.Neon && reloadedSettings.ImagesEnabled,
                "SettingsWindow changes persist across SettingsStore reload");

            settingsWin.Close();
            cleanupWin.Close();
        }
        finally
        {
            Directory.Delete(extDir, recursive: true);
        }
    }

    private static void WritePatternPng(string path, int width, int height)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint h = (uint)(x * 374761393u + y * 668265263u);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                int o = y * stride + x * 4;
                pixels[o] = (byte)(h & 0xFF);
                pixels[o + 1] = (byte)((h >> 8) & 0xFF);
                pixels[o + 2] = (byte)((h >> 16) & 0xFF);
                pixels[o + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void SaveVisualPng(Window win, int width, int height, string outputPath)
    {
        if (win.Content is not FrameworkElement root) return;
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outputPath);
        enc.Save(fs);
    }

    private static void TestInstallScriptIsolated()
    {
        var repoRoot = FindRepoRoot();
        var installScript = Path.Combine(repoRoot, "windows", "scripts", "install.ps1");
        Check(File.Exists(installScript), $"install.ps1 exists at {installScript}");

        var targetDir = Path.Combine(DataFolder.Root, "Programs", "EnCopy");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-ExecutionPolicy Bypass -File \"{installScript}\" -TargetDir \"{targetDir}\" -NoLaunch",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powershell for install.ps1 check");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Check(proc.ExitCode == 0, $"install.ps1 exited with {proc.ExitCode}: {stderr}\n{stdout}");
        Check(File.Exists(Path.Combine(targetDir, "EnCopy.exe")), "Daily EnCopy.exe installed into isolated target dir");
        Check(!Directory.Exists(Path.Combine(repoRoot, "windows", "build", "EnCopy")),
            "install.ps1 cleans temporary windows/build/EnCopy after copying");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "windows", "scripts", "install.ps1")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root from check test base directory.");
    }

    private static RenderTargetBitmap RenderVisualToBitmap(UIElement root, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        return bitmap;
    }

    private static byte[] SampleBgra(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return px;
    }

    private static void SavePng(BitmapSource bmp, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void WriteSamplePng(string path, int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * stride + x * 4;
                pixels[idx + 0] = (byte)(80 + x * 120 / width);
                pixels[idx + 1] = (byte)(120 + y * 100 / height);
                pixels[idx + 2] = 210;
                pixels[idx + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        SavePng(bmp, path);
    }

    private static string? PreviewDirArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--render-preview-dir", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
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

        public bool TryWriteFiles(IReadOnlyList<string> paths)
        {
            SequenceNumber++;
            _current = new ClipboardSnapshot(SequenceNumber, ContainsSelfCopy: true, ContainsFiles: true, IsPrivacyBlocked: false, null, null, paths);
            return true;
        }

        public bool TryWriteImage(string imagePath)
        {
            SequenceNumber++;
            _current = new ClipboardSnapshot(SequenceNumber, ContainsSelfCopy: true, ContainsFiles: false, IsPrivacyBlocked: false, null, null);
            return File.Exists(imagePath);
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
        public IReadOnlyList<string>? LastWrittenFiles { get; private set; }
        public string? LastWrittenImagePath { get; private set; }

        public bool WriteClipboardText(string text, byte[]? rtfBytes)
        {
            LastPlainText = text;
            LastRtfBytes = rtfBytes;
            return true;
        }

        public bool WriteClipboardFiles(IReadOnlyList<string> paths)
        {
            LastWrittenFiles = paths;
            return paths.Count > 0;
        }

        public bool WriteClipboardImage(string imagePath)
        {
            LastWrittenImagePath = imagePath;
            return File.Exists(imagePath);
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
