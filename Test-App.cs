using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DuplicateMediaApp;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]

internal static class TestApp
{
    static string root;
    static readonly Action<Pulse> Quiet = delegate { };
    static readonly byte[] Payload = Encoding.UTF8.GetBytes("轻拾 · 此文件仅用于回收及还原验收。");
    static readonly CancellationToken Never = CancellationToken.None;

    static void Assert(bool ok, string why) { if (!ok) throw new Exception(why); }
    static void Pass(string text) { Console.WriteLine("PASS " + text); }
    static string Dir(string name) { return Directory.CreateDirectory(Path.Combine(root, name)).FullName; }
    static string Put(string dir, string name, byte[] data)
    {
        string path = Path.Combine(dir, name); File.WriteAllBytes(MediaPath.Extended(path), data); return path;
    }
    static ScanResult Scan(string path)
    {
        using (var log = new RunLog("scan", root)) return Scanner.Run(path, log, Quiet, Never);
    }
    static void ScanCases()
    {
        var dir = Dir("扫描 中文");
        string deep = dir;
        while (deep.Length < 285) deep = Path.Combine(deep, "含空格 子文件夹");
        Directory.CreateDirectory(MediaPath.Extended(deep));
        var data = new byte[2 * 1024 * 1024]; new Random(15).NextBytes(data);
        string a = Put(dir, "A.MP4", data), b = Put(deep, "副本,甲.WeBp", data);
        Put(dir, "忽略.txt", data); Put(dir, "仅此尺寸.NEF", new byte[3]);
        string deny = Put(dir, "无读取权.CR3", data), locked = Put(dir, "被占用.HEIC", data);
        byte[] other = (byte[])data.Clone(); other[64 * 1024] ^= 0xff; Put(dir, "未采样处不同.MOV", other);
        other[0] ^= 0xff; Put(dir, "首段不同.TS", other);
        Put(dir, "空一.DNG", new byte[0]); Put(dir, "空二.TIFF", new byte[0]);
        DateTime before = File.GetLastWriteTimeUtc(a);
        FileSecurity acl = File.GetAccessControl(deny), blocked = File.GetAccessControl(deny);
        blocked.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.ReadData, AccessControlType.Deny));
        ScanResult r; string logPath;
        var ticks = new List<long>();
        try
        {
            File.SetAccessControl(deny, blocked);
            using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var log = new RunLog("acl", root))
            {
                logPath = log.FileName;
                r = Scanner.Run("\"" + dir + "\"", log, delegate(Pulse p) {
                    if (p.Phase == Phase.Full && p.Path == a) ticks.Add(p.Done);
                }, Never);
            }
        }
        finally { File.SetAccessControl(deny, acl); }
        string details = File.ReadAllText(logPath);
        Assert(r.Skipped == 2 && details.Contains(deny) && details.Contains("Win32=5") && details.Contains(locked) && details.Contains("Win32=32"), details);
        Assert(r.Files == 9 && r.Candidates == 8 && r.Finalists == 5 && r.ReadBytes == 3L * data.Length, r.Metrics());
        Assert(r.Groups == 2 && r.Rows.Count == 4 && r.Rows.Count(x => x.Delete) == 2, r.Metrics());
        string expected;
        using (var sha = SHA256.Create()) expected = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
        Assert(r.Rows.Any(x => x.Path == b && x.Hash == expected) && r.Rows.Any(x => x.Path == a && x.Hash == expected), "长路径 / 完整 SHA-256");
        Assert(Scan(deep).Files == 1, "直接以长路径为扫描根目录");
        Assert(r.Rows.Where(x => x.Bytes == 0).All(x => x.Hash.StartsWith("E3B0C44298FC1C14") && x.Hash.Length == 64), "空文件");
        Assert(ticks.Count >= 4 && ticks.Last() - ticks.First() > data.Length / 2, "单文件内部没有字节进度");
        Assert(File.GetLastWriteTimeUtc(a) == before && File.ReadAllBytes(a).SequenceEqual(data), "扫描修改了源文件");
        Pass("同内容异名、跨类型、中文/空格/大于 260 字符路径、空文件、抽样碰撞、完整 SHA、真实 ACL 拒绝与独占锁");

        string csv = Path.Combine(root, "清单.csv"); CsvReport.Save(csv, r.Rows);
        byte[] saved = File.ReadAllBytes(csv);
        Assert(saved.Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) && File.ReadAllLines(csv).Length == 5, "UTF-8 BOM / 行数");
        string row = File.ReadAllLines(csv).Single(x => x.Contains(b));
        Assert(row.Contains("\"副本,甲.WeBp\"") && row.EndsWith(expected) && row.StartsWith(r.Rows.Single(x => x.Path == b).Group + ","), row);
        bool refused = false; try { CsvReport.Save(csv, r.Rows); } catch (IOException) { refused = true; }
        Assert(refused && saved.SequenceEqual(File.ReadAllBytes(csv)), "导出不覆盖现有文件");
        Pass("CSV BOM、完整路径、含逗号文件名、组号/勾选/完整哈希，不覆盖已有文件");
        using (var cts = new CancellationTokenSource())
        using (var log = new RunLog("取消", root))
        {
            bool cancelled = false;
            try { Scanner.Run(dir, log, delegate(Pulse p) { if (p.Phase == Phase.Full && p.Done > 0 && p.Done < data.Length) cts.Cancel(); }, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && File.Exists(a) && File.Exists(MediaPath.Extended(b)), "核对过程取消");
        }
        Pass("单个视频读取中途的字节进度与取消，媒体仍在原位");
    }

    static CleanResult Clean(List<Entry> rows, Cleaner.RecycleAction recycle)
    {
        var r = new CleanResult();
        using (var log = new RunLog("clean", root)) Cleaner.Run(CleanPlan.Build(rows), r, log, Quiet, Never, recycle);
        return r;
    }
    static void CleanCases()
    {
        var dir = Dir("清理保护"); string keep = Put(dir, "A.JPG", Payload);
        string target = Put(dir, BinCheck.UniqueName(), Payload); var rows = Scan(dir).Rows;
        Assert(rows.Count == 2, "预期识别一对"); rows.ForEach(x => x.Delete = true);
        bool refused = false;
        try { CleanPlan.Build(rows); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("1"); }
        Assert(refused, "整组勾选未拦截 / 未报告组号");
        rows.Single(x => x.Path == keep).Delete = false;
        Cleaner.RecycleAction mustNotRun = delegate { throw new Exception("不应调用系统回收"); };
        DateTime t = File.GetLastWriteTimeUtc(target); var changed = (byte[])Payload.Clone(); changed[0]++;
        File.WriteAllBytes(target, changed); File.SetLastWriteTimeUtc(target, t);
        var cr = Clean(rows, mustNotRun);
        Assert(cr.Failed == 1 && cr.LastFailure.Contains("SHA-256") && File.ReadAllBytes(target).SequenceEqual(changed), "同长度/时间修改未被 SHA 拦截");
        File.WriteAllBytes(target, Payload); rows = Scan(dir).Rows;
        string moved = Path.Combine(dir, "保留已移走.tmp"); File.Move(keep, moved);
        cr = Clean(rows, mustNotRun);
        Assert(cr.Failed == 1 && cr.LastFailure.Contains("没有可确认") && File.Exists(target), "保留副本移走");
        File.Move(moved, keep); t = File.GetLastWriteTimeUtc(keep);
        File.WriteAllBytes(keep, changed); File.SetLastWriteTimeUtc(keep, t);
        Assert(Clean(rows, mustNotRun).Failed == 1 && File.Exists(target), "保留副本同长度和时间被改写");
        File.WriteAllBytes(keep, Payload); rows = Scan(dir).Rows;
        Pass("整组勾选、勾选项与保留项同长度同时间改写、保留副本移走均拒绝回收");

        bool noBin = false;
        try { ShellBin.RequireLocalVolume(@"\\test.invalid\share\file.jpg"); } catch (IOException) { noBin = true; }
        Assert(noBin, "应在访问 UNC 前拒绝回收");
        cr = Clean(rows, delegate { throw new IOException("测试：此卷回收站不可用"); });
        Assert(cr.Failed == 1 && cr.Recycled.Count == 0 && File.Exists(target), "回收站不可用时仍须保留文件");
        using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            cr = Clean(rows, ShellBin.Recycle);
        Assert(cr.Failed == 1 && cr.Recycled.Count == 0 && File.Exists(target) && File.Exists(keep), "Shell 拒绝回收时不能回退删除");
        Pass("拒绝 UNC、注入不可用回收站、真实文件禁止重命名时的 Shell 回收失败；源文件保留");

        string second = Put(dir, BinCheck.UniqueName(), Payload);
        rows = Scan(dir).Rows;
        using (var cts = new CancellationTokenSource())
        using (var log = new RunLog("取消清理", root))
        {
            var result = new CleanResult(); bool cancel = false;
            var plan = CleanPlan.Build(rows);
            try
            {
                Cleaner.Run(plan, result, log, delegate(Pulse p) { if (p.Ok == 1) cts.Cancel(); }, cts.Token,
                    delegate(string path, Action check, CancellationToken token) {
                        bool locked = false; try { File.Move(keep, moved); } catch (IOException) { locked = true; }
                        Assert(locked, "清理期间保留副本未锁住");
                        ShellBin.Recycle(path, check, token);
                    });
            }
            catch (OperationCanceledException) { cancel = true; }
            Assert(cancel && result.Recycled.Count == 1 && result.Failed == 0 && File.Exists(keep) && (File.Exists(target) ^ File.Exists(second)), "取消后执行了额外回收");
            BinCheck.Restore(result.Recycled.Single().Path, Payload);
        }
        Pass("真实回收成功一项后取消：保留副本受锁保护，未处理项未变；从 Windows 回收站还原并逐字节比对");
    }

    static T ControlOf<T>(Control form, string name) where T : Control { return (T)form.Controls.Find(name, true).Single(); }
    delegate bool VisitWindow(IntPtr window, IntPtr data);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, VisitWindow visit, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string caption);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    static string WindowText(IntPtr h) { var b = new StringBuilder(2048); GetWindowText(h, b, b.Capacity); return b.ToString(); }
    static async Task<string> CleanDialog(MainForm f, string title, int button)
    {
        string message = null; int ticks = 0; string observed = "";
        using (var timer = new System.Windows.Forms.Timer { Interval = 60 })
        {
            timer.Tick += delegate {
                IntPtr h = IntPtr.Zero; ticks++;
                EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr window, IntPtr unused) {
                    string caption = WindowText(window);
                    if (window == f.Handle || caption.Length == 0) return true;
                    observed = caption;
                    if (caption != title) return true;
                    h = window; return false;
                }, IntPtr.Zero);
                if (h == IntPtr.Zero) return;
                var b = new StringBuilder();
                for (var s = FindWindowEx(h, IntPtr.Zero, "Static", null); s != IntPtr.Zero; s = FindWindowEx(h, s, "Static", null)) b.Append(WindowText(s));
                message = b.ToString(); PostMessage(h, 0x0111, (IntPtr)button, IntPtr.Zero); timer.Stop();
            };
            timer.Start(); await f.CleanAsync();
        }
        Assert(message != null, "未捕获预期确认框：" + title + " timer=" + ticks + " observed=" + observed + " status=" + ControlOf<Label>(f, "status").Text);
        return message;
    }
    static async Task UiCases(MainForm form)
    {
        string dir = Dir("界面"); string keep = Put(dir, "A.JPG", Payload);
        string target = Put(dir, BinCheck.UniqueName(), Payload), changed = Put(dir, BinCheck.UniqueName(), Payload);
        var input = ControlOf<TextBox>(form, "folder"); var grid = ControlOf<DataGridView>(form, "results");
        var status = ControlOf<Label>(form, "status");
        var original = form.Size;
        using (var shellIcon = System.Drawing.Icon.ExtractAssociatedIcon(typeof(MainForm).Assembly.Location))
        using (var shellImage = shellIcon.ToBitmap())
        using (var windowIcon = new System.Drawing.Icon(form.Icon, shellImage.Size))
        using (var windowImage = windowIcon.ToBitmap())
        {
            int different = 0;
            for (int y = 0; y < shellImage.Height; y++)
                for (int x = 0; x < shellImage.Width; x++)
                {
                    var a = shellImage.GetPixel(x, y); var b = windowImage.GetPixel(x, y);
                    if ((a.A > 0 || b.A > 0) && a.ToArgb() != b.ToArgb()) different++;
                }
            Assert(different < shellImage.Width * shellImage.Height / 20, "资源管理器和窗体未使用相同图标");
        }
        foreach (int width in new int[] { form.MinimumSize.Width, original.Width })
        {
            form.Width = width; form.PerformLayout();
            foreach (var button in Descendants(form).OfType<Button>())
                Assert(button.Parent.ClientRectangle.Contains(button.Bounds) && button.Width >= button.PreferredSize.Width,
                    "按钮被截断：" + button.Text + " bounds=" + button.Bounds + " parent=" + button.Parent.ClientRectangle);
            Assert(grid.Height > grid.RowTemplate.Height && input.Width > 50, "窗口缩小后表格或输入框不可用");
            var clean = Descendants(form).OfType<Button>().Single(b => b.Text == "清理到回收站");
            var export = Descendants(form).OfType<Button>().Single(b => b.Text == "导出 CSV");
            if (grid.Width > export.Parent.Controls.Cast<Control>().Sum(b => b.Width + b.Margin.Horizontal) + clean.Width + 40)
                Assert(Math.Abs(clean.PointToScreen(System.Drawing.Point.Empty).Y - export.PointToScreen(System.Drawing.Point.Empty).Y) <= 3,
                    "有足够横向空间时，常用工具和清理按钮应处在同一行");
        }
        form.Size = original;
        Pass("窗口与 EXE 图标一致；宽窗工具栏与清理按钮对齐，窄窗操作不越界");
        Assert(!input.ReadOnly, "路径不可手动编辑"); input.Text = dir; await form.ScanAsync();
        Assert(form.LastScan != null && grid.RowCount == 3 && grid.VirtualMode, "结果未绑定：" + status.Text);
        Assert((string)grid[5, 0].Value == keep && (string)grid[2, 0].Value == "建议保留", "建议不正确");
        grid[0, 0].Value = true;
        Assert(form.LastScan.Rows.All(x => x.Delete), "虚拟表格没有提交整组勾选");
        string msg = await CleanDialog(form, "拒绝清理", 1);
        Assert(msg.Contains("1") && File.Exists(keep), "未指出组号");
        grid[0, 0].Value = false;
        msg = await CleanDialog(form, "确认清理到回收站", 7); // IDNO
        Assert(msg.Contains("2 个") && msg.Contains((2 * Payload.Length) + " 字节") && File.Exists(target) && grid.RowCount == 3, "确认框数量/大小/取消");
        File.AppendAllText(changed, "变更");
        await CleanDialog(form, "确认清理到回收站", 6); // IDYES, private test items only
        var paths = Enumerable.Range(0, grid.RowCount).Select(i => (string)grid[5, i].Value).ToList();
        Assert(paths.Count == 2 && paths.Contains(changed) && paths.Contains(keep) && !File.Exists(target) && status.Text.Contains("成功 1，失败 1"), status.Text);
        Assert((bool)grid[0, paths.IndexOf(changed)].Value, "失败项应保留原勾选");
        BinCheck.Restore(target, Payload);
        Pass("真实窗体：手动输入、虚拟清单、整组拒绝框、数量/大小确认、拒绝执行、混合成功/失败仅剔除成功行");
    }
    static IEnumerable<Control> Descendants(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (Control grand in Descendants(child)) yield return grand;
        }
    }
    static void LayoutStress()
    {
        foreach (float scale in new float[] { 1.25f, 1.5f, 2f })
        using (var f = new MainForm { ShowInTaskbar = false, Opacity = 0 })
        {
            f.Show();
            var controls = Descendants(f).Concat(new Control[] { f }).ToArray();
            var original = controls.Select(c => c.Font).ToArray();
            var fonts = original.Distinct().ToDictionary(font => font, font => new System.Drawing.Font(font.FontFamily, font.Size * scale, font.Style));
            try
            {
                f.Scale(new System.Drawing.SizeF(scale, scale));
                for (int i = 0; i < controls.Length; i++) controls[i].Font = fonts[original[i]];
                f.ClientSize = new System.Drawing.Size((int)(780 * scale), (int)(540 * scale));
                f.PerformLayout();
                foreach (var b in controls.OfType<Button>())
                {
                    Assert(b.Parent.ClientRectangle.Contains(b.Bounds) && b.Width >= b.PreferredSize.Width && b.Height >= b.PreferredSize.Height,
                        "布局压力 " + scale + "，按钮越界：" + b.Text);
                    var inForm = f.RectangleToClient(b.RectangleToScreen(b.ClientRectangle));
                    Assert(f.ClientRectangle.Contains(inForm), "布局压力下按钮超出窗口：" + b.Text);
                }
            }
            finally { f.Close(); foreach (var font in fonts.Values) font.Dispose(); }
        }
        Pass("125%/150%/200% 字体与布局尺寸压力：按钮在窗内且达到首选尺寸（未修改系统 DPI）");
    }

    static string Generate40k()
    {
        string dir = Dir("四万媒体");
        var sw = Stopwatch.StartNew(); byte[] small = new byte[1024], block = new byte[1024 * 1024];
        new Random(2026).NextBytes(block);
        for (int batch = 0; batch < 200; batch++)
        {
            string sub = Directory.CreateDirectory(Path.Combine(dir, "相册 " + batch.ToString("D3"))).FullName;
            for (int j = 0; j < 100; j++)
            {
                int id = batch * 100 + j; string stem = id.ToString("D5");
                if (id == 19999)
                {
                    string big = Path.Combine(sub, stem + "A.MP4");
                    using (var s = File.Create(big)) for (int chunk = 0; chunk < 1024; chunk++) s.Write(block, 0, block.Length);
                    File.Copy(big, Path.Combine(sub, stem + "B.MOV"));
                }
                else
                {
                    Buffer.BlockCopy(BitConverter.GetBytes(id), 0, small, 0, 4);
                    Put(sub, stem + "A.JPG", small); Put(sub, stem + "B.HEIC", small);
                }
            }
        }
        Console.WriteLine("FIXTURE 200 folders, 19999 x (2 x 1 KiB) + 1 x (2 x 1 GiB); generation={0:F3}s (excluded)", sw.Elapsed.TotalSeconds);
        return dir;
    }
    static async Task Benchmark(MainForm form, string dir)
    {
        var input = ControlOf<TextBox>(form, "folder"); var grid = ControlOf<DataGridView>(form, "results");
        var status = ControlOf<Label>(form, "status"); var detail = ControlOf<TextBox>(form, "detail");
        var intervals = new List<double>(); var videoStatuses = new Dictionary<string, HashSet<string>>();
        EventHandler trackVideo = delegate {
            if (!status.Text.Contains("完整核对") || !(detail.Text.EndsWith(".MP4") || detail.Text.EndsWith(".MOV"))) return;
            HashSet<string> seen; if (!videoStatuses.TryGetValue(detail.Text, out seen)) videoStatuses.Add(detail.Text, seen = new HashSet<string>());
            seen.Add(status.Text);
        };
        input.Text = dir;
        var clock = Stopwatch.StartNew(); double last = 0; status.TextChanged += trackVideo;
        using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
        {
            timer.Tick += delegate {
                double now = clock.Elapsed.TotalMilliseconds; intervals.Add(now - last); last = now;
            };
            timer.Start(); await form.ScanAsync(); grid.Update();
            intervals.Add(clock.Elapsed.TotalMilliseconds - last); timer.Stop();
        }
        status.TextChanged -= trackVideo; clock.Stop();
        var r = form.LastScan;
        Assert(r != null && r.Files == 40000 && r.Groups == 20000 && r.Rows.Count == 40000 && r.Skipped == 0 && grid.RowCount == 40000, "4 万文件结果错误：" + status.Text);
        Assert(r.ReadBytes == 19999L * 2 * 1024 + 2L * 1024 * 1024 * 1024 && r.ReadBytes == r.TotalBytes, r.Metrics());
        grid.FirstDisplayedScrollingRowIndex = 39980;
        Assert(grid[5, 39999].Value != null && r.Rows.Count(x => x.Delete) == 20000, "虚拟列表末行/默认勾选");
        var times = intervals.OrderBy(x => x).ToArray();
        int updates = videoStatuses.Count == 0 ? 0 : videoStatuses.Values.Max(x => x.Count);
        Console.WriteLine("SCAN " + r.Metrics());
        Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "UI scan+bind={0:F3}s timer=50ms intervals={1} p95={2:F1}ms max={3:F1}ms distinctUpdatesOnOne1GiBFile={4}",
            clock.Elapsed.TotalSeconds, times.Length, times[(int)Math.Ceiling(times.Length * .95) - 1], times.Last(), updates));
        Assert(times.Length > 10 && updates >= 2, "没有测到窗口心跳或同一大文件的中间字节进度");
        using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            timer.Tick += delegate { timer.Stop(); form.RequestCancel(); };
            timer.Start(); await form.ScanAsync();
        }
        Assert(status.Text.Contains("已取消") && grid.RowCount == 0 && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() == 40000, "界面取消必须弃置部分结果且保留所有文件");
        Pass("4 万文件实际界面扫描及绑定、单个大视频字节进度、滚动末行、界面取消后仍为 4 万文件");
    }

    static void Desktop(Func<MainForm, Task> test)
    {
        Exception failure = null;
        using (var form = new MainForm { ShowInTaskbar = false, Opacity = 0 })
        {
            form.Shown += async delegate {
                try { await Task.Yield(); await test(form); }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) throw new Exception("桌面验收失败", failure);
    }

    [STAThread]
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        root = Path.Combine(Path.GetTempPath(), "QingShi-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Console.WriteLine("ENV OS={0} process={1} CLR={2} timestamp={3:O}", Environment.OSVersion.Version, Environment.Is64BitProcess ? "x64" : "x86", Environment.Version, DateTime.Now);
            ScanCases(); CleanCases(); Desktop(UiCases); LayoutStress();
            if (args.Contains("--40k")) { string dir = Generate40k(); Desktop(f => Benchmark(f, dir)); }
            Console.WriteLine("PASS all requested cases");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(MediaPath.Extended(root), true); }
    }
}
