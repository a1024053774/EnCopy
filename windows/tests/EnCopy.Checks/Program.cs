using System.Diagnostics;
using System.IO;
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
    }

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

        Check(LineKeyRouter.HandleKeyDown(Key.Enter, ModifierKeys.Shift, false, "", false, line, p => pastedPlain = p ? 1 : 0, () => { }, () => { }, () => { }), "Shift+Enter handled");
        Check(pastedPlain == 1, "Shift+Enter requests plain paste");

        string q = "hello";
        Check(LineKeyRouter.HandleKeyDown(Key.Escape, ModifierKeys.None, false, q, false, line, _ => { }, () => { }, () => dismissed++, () => { q = ""; cleared++; }), "First Esc handled");
        Check(cleared == 1 && dismissed == 0 && q == "", "First Esc clears query without dismissing");
        Check(LineKeyRouter.HandleKeyDown(Key.Escape, ModifierKeys.None, false, q, false, line, _ => { }, () => { }, () => dismissed++, () => cleared++), "Second Esc handled");
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

        var pinnedPath = line.BasketPicked!.FilePath;
        LineKeyRouter.HandleKeyDown(Key.P, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 1 && string.Equals(line.LineItems[0].FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase),
            "Ctrl+P in basket pins picked item onto left of knot");
        Check(!line.BasketGroups.SelectMany(g => g.Rows).Any(r => string.Equals(r.Note.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)),
            "Pinned item leaves the basket list");

        // Ctrl+Z 撤销从篮子刚收藏的条目：应撤回篮子并恢复 LineState.json，之后仍可再次 Ctrl+P 收藏上绳
        LineKeyRouter.HandleKeyDown(Key.Z, ModifierKeys.Control, false, "", false, line, _ => { }, () => { }, () => { }, () => { });
        Check(line.PinnedCount == 0 && line.BasketGroups.SelectMany(g => g.Rows).Any(r => string.Equals(r.Note.FilePath, pinnedPath, StringComparison.OrdinalIgnoreCase)),
            "Ctrl+Z after basket Ctrl+P unpins and returns item to basket");
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
        private ClipboardSnapshot? _current;
        public event EventHandler? Changed;
        public uint SequenceNumber { get; private set; }

        public void Push(ClipboardSnapshot snap)
        {
            SequenceNumber = snap.SequenceNumber;
            _current = snap;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public ClipboardSnapshot? TryRead() => _current;

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

        public bool WriteClipboardText(string text, byte[]? rtfBytes)
        {
            LastPlainText = text;
            LastRtfBytes = rtfBytes;
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
