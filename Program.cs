using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: InternalsVisibleTo("QingShi.Tests")]
[assembly: AssemblyTitle("轻拾 · 照片与视频去重")]
[assembly: AssemblyProduct("轻拾")]
[assembly: AssemblyVersion("1.1.0.0")]

namespace DuplicateMediaApp
{
    internal sealed class MainForm : Form
    {
        static readonly Color Navy = Color.FromArgb(32, 51, 68), Ink = Color.FromArgb(36, 54, 69), Muted = Color.FromArgb(83, 102, 117),
            Mint = Color.FromArgb(13, 109, 96), Line = Color.FromArgb(211, 221, 228), Canvas = Color.FromArgb(240, 244, 247);
        readonly Icon appIcon = new Icon(typeof(MainForm), "QingShi.ico");
        readonly Bitmap brandImage;
        readonly TextBox folder = new TextBox { Name = "folder", MaxLength = 32767, BorderStyle = BorderStyle.FixedSingle, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        readonly Button browse = ButtonOf("选择文件夹"), scan = ButtonOf("开始扫描", 1), cancel = ButtonOf("取消任务");
        readonly Button clean = ButtonOf("清理到回收站", 2), export = ButtonOf("导出 CSV"), suggest = ButtonOf("按建议勾选"),
            clear = ButtonOf("取消所有勾选"), copy = ButtonOf("复制路径"), locate = ButtonOf("在资源管理器中定位");
        readonly Label stats = TextOf("", false), status = TextOf("就绪：选择文件夹或粘贴完整路径后开始扫描。", false);
        readonly TextBox detail = new TextBox { ReadOnly = true, TabStop = false, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, BackColor = Color.White, ForeColor = Muted, Name = "detail" };
        readonly LinkLabel logLink = new LinkLabel { Text = "查看本次日志", AutoSize = true, Anchor = AnchorStyles.Right, LinkColor = Mint, LinkBehavior = LinkBehavior.HoverUnderline };
        readonly DataGridView grid = new DataGridView { Name = "results", Dock = DockStyle.Fill, VirtualMode = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false, AutoGenerateColumns = false, MultiSelect = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
        readonly ProgressBar bar = new ProgressBar { Dock = DockStyle.Fill, Height = 10, MarqueeAnimationSpeed = 25 };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 100 };
        readonly object gate = new object();
        Pulse latest;
        bool dirty, busy;
        CancellationTokenSource cancellation;
        List<Entry> rows = new List<Entry>();
        int marked, groups;
        long markedBytes;
        string logPath;
        internal ScanResult LastScan { get; private set; }

        internal MainForm()
        {
            Text = "轻拾 · 照片与视频去重";
            Icon = appIcon;
            using (var logo = new Icon(appIcon, 64, 64)) brandImage = logo.ToBitmap();
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Canvas; ForeColor = Ink;
            MinimumSize = new Size(680, 480); ClientSize = new Size(1160, 720);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(i == 2 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 2, BackColor = Navy,
                Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 0, 0, 8) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var mark = new PictureBox { Image = brandImage, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) };
            var title = TextOf("轻拾 · 照片与视频去重", false);
            title.Font = new Font(Font.FontFamily, 15, FontStyle.Bold); title.ForeColor = Color.White; title.Margin = Padding.Empty;
            var caption = TextOf("只读扫描  ·  完整内容核对  ·  确认后清理", false); caption.ForeColor = Color.FromArgb(193, 208, 219); caption.Margin = Padding.Empty;
            var badge = TextOf("本地工作区", false); badge.ForeColor = Color.FromArgb(196, 236, 227); badge.BackColor = Color.FromArgb(43, 72, 83); badge.Padding = new Padding(9, 4, 9, 4);
            header.Controls.Add(mark, 0, 0); header.SetRowSpan(mark, 2); header.Controls.Add(title, 1, 0); header.Controls.Add(caption, 1, 1);
            header.Controls.Add(badge, 2, 0); header.SetRowSpan(badge, 2); layout.Controls.Add(header, 0, 0);
            var input = Card(5);
            input.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 2; i < 5; i++) input.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folder.AccessibleName = "扫描文件夹完整路径"; folder.Margin = new Padding(6, 0, 7, 0);
            input.Controls.Add(TextOf("扫描文件夹", true), 0, 0); input.Controls.Add(folder, 1, 0);
            input.Controls.Add(browse, 2, 0); input.Controls.Add(scan, 3, 0); input.Controls.Add(cancel, 4, 0);
            layout.Controls.Add(input, 0, 1);
            var results = Card(2); results.AutoSize = false; results.Padding = new Padding(1);
            results.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); results.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            results.RowStyles.Add(new RowStyle(SizeType.AutoSize)); results.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); results.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var summary = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(10, 4, 8, 4) };
            var listTitle = TextOf("重复文件清单", true); listTitle.Margin = new Padding(2, 4, 18, 4);
            stats.Name = "statistics"; stats.ForeColor = Muted; summary.Controls.Add(listTitle); summary.Controls.Add(stats);
            results.Controls.Add(summary, 0, 0); results.SetColumnSpan(summary, 2);
            MakeGrid(); results.Controls.Add(grid, 0, 1); results.SetColumnSpan(grid, 2);
            var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(6, 4, 0, 4) };
            tools.Controls.AddRange(new Control[] { suggest, clear, copy, locate, export });
            clean.Anchor = AnchorStyles.Right; clean.Margin = new Padding(3, 4, 9, 4);
            results.Controls.Add(tools, 0, 2); results.Controls.Add(clean, 1, 2); layout.Controls.Add(results, 0, 2);
            var foot = Card(2); foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.Margin = Padding.Empty;
            status.Name = "status"; status.AutoSize = false; status.AutoEllipsis = true; status.Dock = DockStyle.Fill; status.Height = Font.Height + 8;
            foot.Controls.Add(status, 0, 0); foot.Controls.Add(logLink, 1, 0);
            foot.Controls.Add(bar, 0, 1); foot.SetColumnSpan(bar, 2);
            detail.Text = "回收后可在 Windows 回收站中还原；移入回收站不会立即释放磁盘空间。";
            foot.Controls.Add(detail, 0, 2); foot.SetColumnSpan(detail, 2);
            layout.Controls.Add(foot, 0, 3); Controls.Add(layout);
            browse.Click += delegate {
                using (var picker = new FolderBrowserDialog { ShowNewFolderButton = false, Description = "选择要只读扫描的文件夹" })
                    if (picker.ShowDialog(this) == DialogResult.OK) folder.Text = picker.SelectedPath;
            };
            folder.TextChanged += delegate {
                if (!busy && (rows.Count > 0 || LastScan != null)) { LastScan = null; ReplaceRows(new List<Entry>()); status.Text = "扫描位置已更改，请重新扫描。"; }
                Buttons();
            };
            folder.KeyDown += async delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter && scan.Enabled) { e.SuppressKeyPress = true; await ScanAsync(); } };
            scan.Click += async delegate { await ScanAsync(); };
            cancel.Click += delegate { RequestCancel(); };
            clean.Click += async delegate { await CleanAsync(); };
            export.Click += async delegate { await ExportAsync(); };
            suggest.Click += delegate { SetMarks(true); }; clear.Click += delegate { SetMarks(false); };
            copy.Click += delegate {
                var item = Current(); if (item == null) return;
                try { Clipboard.SetText(item.Path, TextDataFormat.UnicodeText); status.Text = "已复制完整路径。"; }
                catch (Exception ex) { Notify("复制失败", ex); }
            };
            locate.Click += async delegate {
                var item = Current(); if (item == null) return;
                try
                {
                    await Task.Run(delegate { File.GetAttributes(MediaPath.Extended(item.Path)); });
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + item.Path + "\"") { UseShellExecute = false });
                }
                catch (Exception ex) { Notify("无法定位", ex); }
            };
            logLink.LinkClicked += delegate {
                try { Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + logPath + "\""); }
                catch (Exception ex) { Notify("无法打开日志", ex); }
            };
            timer.Tick += delegate { PaintProgress(); };
            FormClosing += delegate(object s, FormClosingEventArgs e) {
                if (!busy) return;
                e.Cancel = true; RequestCancel();
            };
            Shown += delegate { var area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height) { Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height)); CenterToScreen(); }
            };
            Summary(); Buttons();
        }

        static Label TextOf(string text, bool bold)
        {
            var l = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 4, 5, 4) };
            if (bold) l.Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold);
            return l;
        }
        static Button ButtonOf(string text, int kind = 0)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
                Padding = new Padding(8, 3, 8, 3), Margin = new Padding(3), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            Color fill = kind == 1 ? Mint : kind == 2 ? Color.FromArgb(255, 246, 241) : Color.White;
            b.BackColor = fill;
            b.ForeColor = kind == 1 ? Color.White : kind == 2 ? Color.FromArgb(143, 61, 37) : Ink;
            b.FlatAppearance.BorderColor = kind == 1 ? Mint : kind == 2 ? Color.FromArgb(213, 165, 147) : Line;
            b.FlatAppearance.MouseOverBackColor = kind == 1 ? Color.FromArgb(9, 91, 80) : kind == 2 ? Color.FromArgb(251, 231, 221) : Canvas;
            b.EnabledChanged += delegate { b.BackColor = b.Enabled ? fill : Color.FromArgb(244, 247, 249); };
            return b;
        }
        static TableLayoutPanel Card(int columns)
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = columns, Padding = new Padding(10, 7, 10, 7),
                BackColor = Color.White, Margin = new Padding(0, 0, 0, 8) };
            p.Paint += delegate(object s, PaintEventArgs e) { ControlPaint.DrawBorder(e.Graphics, p.ClientRectangle, Line, ButtonBorderStyle.Solid); };
            return p;
        }
        void MakeGrid()
        {
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None; grid.Margin = Padding.Empty;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(227, 233, 238);
            grid.EnableHeadersVisualStyles = false; grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(231, 238, 243); grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 5, 3, 5);
            grid.DefaultCellStyle.Padding = new Padding(5, 0, 5, 0);
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(216, 236, 231); grid.DefaultCellStyle.SelectionForeColor = Ink;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 249, 251);
            grid.RowTemplate.Height = Math.Max(30, Font.Height + 10);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "清理", Width = 52, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns[0].DefaultCellStyle.Padding = Padding.Empty;
            string[] labels = { "组号", "建议", "类型", "文件名", "完整路径", "大小", "修改时间" };
            int[] widths = { 65, 84, 60, 165, 300, 95, 165 };
            for (int i = 0; i < labels.Length; i++)
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = labels[i], Width = widths[i], ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; grid.Columns[5].MinimumWidth = 225;
            grid.Columns[2].DefaultCellStyle.ForeColor = Mint;
            grid.Columns[6].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns[6].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Paint += delegate(object sender, PaintEventArgs e) {
                if (grid.RowCount != 0) return;
                var body = grid.ClientRectangle; body.Y += grid.ColumnHeadersHeight; body.Height -= grid.ColumnHeadersHeight;
                int line = grid.Font.Height + 6;
                if (body.Height < 3 * line) return;
                var box = new Rectangle(body.X, body.Top + (body.Height - 2 * line) / 2, body.Width, line);
                const TextFormatFlags align = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(e.Graphics, busy ? "正在生成清单" : LastScan != null ? "未发现重复文件" : "等待扫描", grid.ColumnHeadersDefaultCellStyle.Font, box, Ink, align);
                box.Y += line;
                TextRenderer.DrawText(e.Graphics, busy ? "完整核对后将在此列出结果。" : LastScan != null ? "可选择其他文件夹重新扫描。" : "选择文件夹并开始扫描，重复文件将在此按组显示。", grid.Font, box, Muted, align);
            };
            grid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e) {
                if (e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                var r = rows[e.RowIndex];
                switch (e.ColumnIndex)
                {
                    case 0: e.Value = r.Delete; break; case 1: e.Value = r.Group; break;
                    case 2: e.Value = r.Suggested ? "建议保留" : ""; break; case 3: e.Value = r.Type; break;
                    case 4: e.Value = r.Name; break; case 5: e.Value = r.Path; break;
                    case 6: e.Value = MediaPath.Size(r.Bytes); break; case 7: e.Value = r.Modified; break;
                }
            };
            grid.CellValuePushed += delegate(object sender, DataGridViewCellValueEventArgs e) {
                if (busy || e.ColumnIndex != 0 || e.RowIndex < 0 || e.RowIndex >= rows.Count) return;
                var r = rows[e.RowIndex]; bool next = Convert.ToBoolean(e.Value);
                if (r.Delete == next) return;
                r.Delete = next; marked += next ? 1 : -1; markedBytes += next ? r.Bytes : -r.Bytes; Summary(); Buttons();
            };
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e) {
                if (e.RowIndex >= 0 && e.RowIndex < rows.Count && (e.ColumnIndex == 4 || e.ColumnIndex == 5)) e.ToolTipText = rows[e.RowIndex].Path;
            };
        }
        Entry Current() { return grid.CurrentCell == null || grid.CurrentCell.RowIndex >= rows.Count ? null : rows[grid.CurrentCell.RowIndex]; }
        void SetMarks(bool advice)
        {
            grid.EndEdit();
            foreach (var r in rows) r.Delete = advice && !r.Suggested;
            Recount(); grid.Invalidate();
        }
        void ReplaceRows(List<Entry> next)
        {
            grid.EndEdit(); rows = next; groups = 0;
            foreach (var g in rows.GroupBy(x => x.Group))
            {
                if (g.Count() > 1) groups++;
                if (!g.Any(x => x.Suggested)) (g.FirstOrDefault(x => !x.Delete) ?? g.First()).Suggested = true;
            }
            grid.RowCount = rows.Count; Recount(); grid.Invalidate();
        }
        void Recount() { marked = 0; markedBytes = 0; foreach (var r in rows) if (r.Delete) { marked++; markedBytes += r.Bytes; } Summary(); Buttons(); }
        void Summary() { stats.Text = String.Format("重复组  {0:N0}    |    文件  {1:N0}    |    已勾选  {2:N0}    |    勾选大小  {3}", groups, rows.Count, marked, MediaPath.Size(markedBytes)); }
        void Buttons()
        {
            browse.Enabled = !busy; folder.ReadOnly = busy; scan.Enabled = !busy && !String.IsNullOrWhiteSpace(folder.Text);
            cancel.Enabled = busy && cancellation != null && !cancellation.IsCancellationRequested;
            suggest.Enabled = clear.Enabled = copy.Enabled = locate.Enabled = export.Enabled = !busy && rows.Count > 0;
            clean.Enabled = !busy && marked != 0; grid.ReadOnly = busy;
            if (!busy) grid.Columns[0].ReadOnly = false;
            logLink.Enabled = !busy && logPath != null;
        }
        void Notify(string title, Exception ex) { status.Text = title + "：" + ex.Message; MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        void BeginJob(string text, bool canCancel)
        {
            busy = true; cancellation = canCancel ? new CancellationTokenSource() : null;
            lock (gate) dirty = false;
            bar.Value = 0; status.Text = text; detail.Text = canCancel ? "可随时取消；系统文件操作可能需等待当前步骤结束。" : "";
            Buttons(); timer.Start();
        }
        void EndJob()
        {
            timer.Stop(); bar.Style = ProgressBarStyle.Continuous; busy = false;
            if (cancellation != null) { cancellation.Dispose(); cancellation = null; }
            Buttons(); grid.Invalidate();
        }
        internal void RequestCancel()
        {
            if (cancellation == null) { status.Text = "请等待当前步骤结束。"; return; }
            cancellation.Cancel(); cancel.Enabled = false;
            status.Text = "正在取消，系统文件操作可能需等待当前步骤结束；已回收的文件不会自动还原。";
        }
        void Report(Pulse p) { lock (gate) { latest = p; dirty = true; } }
        void PaintProgress()
        {
            Pulse p;
            lock (gate) { if (!dirty) return; p = latest; dirty = false; }
            if (cancellation != null && cancellation.IsCancellationRequested) return;
            bar.Style = p.Phase == Phase.Enumerate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            bar.Value = p.Total == 0 ? 0 : Math.Max(0, Math.Min(100, (int)(100.0 * p.Done / p.Total)));
            detail.Text = p.Path;
            switch (p.Phase)
            {
                case Phase.Enumerate: status.Text = String.Format("① 枚举文件   已发现 {0:N0} 个媒体文件", p.Done); break;
                case Phase.Sample: status.Text = String.Format("② 快速筛选   {0:N0} / {1:N0} 个候选", p.Done, p.Total); break;
                case Phase.Full: status.Text = String.Format("③ 完整核对   {0:N0} / {1:N0} 字节   ({2}%)", p.Done, p.Total, bar.Value); break;
                default:
                    string step = p.Phase == Phase.Keep ? "复核保留副本" : p.Phase == Phase.Verify ? "复核勾选文件" : "移入回收站";
                    status.Text = String.Format("{0}   已处理 {1}/{2}   成功 {3}   失败 {4}", step, p.Done, p.Total, p.Ok, p.Fail) +
                        (p.PartTotal > 0 ? "    " + MediaPath.Size(p.Part) + " / " + MediaPath.Size(p.PartTotal) : "");
                    break;
            }
        }
        internal async Task ScanAsync()
        {
            if (busy) return;
            string root = folder.Text; LastScan = null; logPath = null;
            BeginJob("① 枚举文件…", true); ReplaceRows(new List<Entry>());
            CancellationToken token = cancellation.Token;
            try
            {
                LastScan = await Task.Run(delegate {
                    using (var log = new RunLog("scan"))
                    {
                        logPath = log.FileName;
                        try { return Scanner.Run(root, log, Report, token); }
                        catch (Exception ex) { log.Note("扫描结束\t" + ex); throw; }
                    }
                });
                ReplaceRows(LastScan.Rows); bar.Value = 100;
                status.Text = String.Format("扫描完成：{0:N0} 个媒体文件，{1:N0} 组重复，跳过 {2:N0} 项。", LastScan.Files, LastScan.Groups, LastScan.Skipped);
                detail.Text = LastScan.Skipped > 0 ? "跳过原因见本次日志：" + logPath : "每组默认保留完整路径排序第一份。请核对勾选项后再清理。";
            }
            catch (OperationCanceledException) { status.Text = "扫描已取消；没有更改媒体文件。"; detail.Text = logPath; }
            catch (Exception ex) { status.Text = "扫描失败：" + ex.Message; detail.Text = (logPath ?? "未能建立日志") + "   " + ex.Message; }
            finally { EndJob(); }
        }
        internal static Task RunSta(Action work)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t = new Thread(delegate() { try { work(); done.SetResult(true); } catch (Exception ex) { done.SetException(ex); } });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.STA); t.Start(); return done.Task;
        }
        internal async Task CleanAsync()
        {
            if (busy) return;
            grid.EndEdit();
            CleanPlan plan;
            try { plan = CleanPlan.Build(rows); }
            catch (InvalidOperationException ex) { Notify("拒绝清理", ex); return; }
            string message = String.Format("将把 {0:N0} 个勾选文件（{1}，共 {2:N0} 字节）移入 Windows 回收站。\r\n\r\n每组至少保留一份并重新核对完整内容。不能回收的文件将跳过。\r\n取消只停止后续操作，已回收的文件不会自动还原。\r\n移入回收站不会立即释放磁盘空间。\r\n\r\n确认继续吗？", plan.Count, MediaPath.Size(plan.Bytes), plan.Bytes);
            if (MessageBox.Show(this, message, "确认清理到回收站", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            logPath = null;
            BeginJob("正在复核保留副本…", true);
            var result = new CleanResult(); var token = cancellation.Token;
            string finish = "清理完成";
            try
            {
                await RunSta(delegate {
                    using (var log = new RunLog("clean"))
                    {
                        logPath = log.FileName; log.Note("用户已确认\t文件数=" + plan.Count + "\t字节=" + plan.Bytes);
                        try { Cleaner.Run(plan, result, log, Report, token, ShellBin.Recycle); }
                        catch (Exception ex) { log.Note("清理中止\t" + ex); throw; }
                        finally { log.Note("成功=" + result.Recycled.Count + "\t失败=" + result.Failed + "\t未处理=" + (plan.Count - result.Done)); }
                    }
                });
            }
            catch (OperationCanceledException) { finish = "清理已取消"; }
            catch (Exception ex) { finish = "清理中断"; result.LastFailure = ex.Message; }
            finally
            {
                ReplaceRows(rows.Where(x => !result.Recycled.Contains(x)).ToList());
                bar.Value = (int)(100.0 * result.Done / plan.Count);
                status.Text = String.Format("{0}：成功 {1:N0}，失败 {2:N0}，未处理 {3:N0}。", finish, result.Recycled.Count, result.Failed, plan.Count - result.Done);
                detail.Text = result.LastFailure == null ? "已回收项可到 Windows 回收站还原；移入回收站不会立即释放空间。" : result.LastFailure + "   日志：" + (logPath ?? "未能建立日志");
                EndJob();
            }
        }
        async Task ExportAsync()
        {
            if (busy) return; grid.EndEdit();
            using (var d = new SaveFileDialog { Title = "导出清单（请使用新的文件名）", Filter = "UTF-8 CSV|*.csv", DefaultExt = "csv", AddExtension = true, OverwritePrompt = false, FileName = "重复媒体清单-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string path = d.FileName; BeginJob("正在导出 UTF-8 CSV…", false);
                try { await Task.Run(delegate { CsvReport.Save(path, rows); }); status.Text = "清单已导出。"; detail.Text = path; }
                catch (Exception ex) { Notify("导出失败（请选择新的 CSV 文件名）", ex); }
                finally { EndJob(); }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
            if (disposing) { brandImage.Dispose(); appIcon.Dispose(); }
        }
    }

    internal static class Program
    {
        static string StartupLog(string msg)
        {
            string record = DateTime.Now.ToString("O") + "  " + msg + Environment.NewLine;
            string path = Path.Combine(RunLog.DirectoryName, "startup.log");
            try { Directory.CreateDirectory(RunLog.DirectoryName); File.AppendAllText(path, record, new UTF8Encoding(true)); return path; }
            catch (Exception ex)
            {
                string fallback = Path.Combine(Path.GetTempPath(), "DuplicateMediaApp-startup-" + Process.GetCurrentProcess().Id + ".log");
                try { File.AppendAllText(fallback, record + ex.Message + Environment.NewLine, Encoding.UTF8); return fallback; }
                catch (Exception other) { return "启动日志无法写入：" + ex.Message + "；" + other.Message; }
            }
        }
        static void Error(Exception ex)
        {
            string path = StartupLog(ex.ToString());
            MessageBox.Show(ex.Message + "\r\n\r\n日志：" + path, "轻拾 · 启动或界面错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        [STAThread]
        static void Main()
        {
            try
            {
                StartupLog("启动；CLR=" + Environment.Version + "；进程=" + (Environment.Is64BitProcess ? "x64" : "x86"));
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Error(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { StartupLog(Convert.ToString(e.ExceptionObject)); };
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using (var f = new MainForm()) { f.Shown += delegate { StartupLog("主窗口已显示"); }; Application.Run(f); }
                StartupLog("正常退出");
            }
            catch (Exception ex) { Error(ex); }
        }
    }
}
