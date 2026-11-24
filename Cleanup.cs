using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace DuplicateMediaApp
{
    internal sealed class CleanPlan
    {
        internal readonly List<List<Entry>> Groups = new List<List<Entry>>();
        internal int Count;
        internal long Bytes;

        internal static CleanPlan Build(IEnumerable<Entry> rows)
        {
            var plan = new CleanPlan();
            var invalid = new List<int>();
            foreach (var g in rows.GroupBy(x => x.Group))
            {
                var marked = g.Where(x => x.Delete).ToList();
                if (marked.Count == 0) continue;
                if (marked.Count == g.Count()) invalid.Add(g.Key);
                plan.Groups.Add(g.ToList()); plan.Count += marked.Count; plan.Bytes += marked.Sum(x => x.Bytes);
            }
            if (invalid.Count != 0)
                throw new InvalidOperationException("以下组全部被勾选：" + String.Join("、", invalid) + "。\r\n请为每组至少留下一份未勾选的文件。没有执行清理。");
            if (plan.Count == 0) throw new InvalidOperationException("请先勾选要移入回收站的副本。");
            return plan;
        }
    }

    internal sealed class CleanResult
    {
        internal readonly HashSet<Entry> Recycled = new HashSet<Entry>();
        internal int Failed;
        internal string LastFailure;
        internal int Done { get { return Recycled.Count + Failed; } }
    }

    internal static class Cleaner
    {
        internal delegate void RecycleAction(string path, Action validate, CancellationToken token);

        internal static void Run(CleanPlan plan, CleanResult result, RunLog log, Action<Pulse> report, CancellationToken token, RecycleAction recycle)
        {
            var reader = new MediaReader();
            foreach (var group in plan.Groups)
            {
                token.ThrowIfCancellationRequested();
                FileLease keeper = null;
                Entry keep = null;
                try
                {
                    foreach (var item in group.Where(x => !x.Delete))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            // Deny both writes and renames until every attempt in this group is finished.
                            keeper = Verify(item, false, reader, report, plan, result, token);
                            keep = item;
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (!MediaPath.FileProblem(ex)) throw;
                            log.Skip("复核保留副本", item.Path, RunLog.Reason(ex));
                        }
                    }
                    foreach (var item in group.Where(x => x.Delete))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            if (keeper == null || item.Hash != keep.Hash || item.Bytes != keep.Bytes)
                                throw new IOException("第 " + item.Group + " 组没有可确认的保留副本，未执行回收。");
                            if (item.Volume == keep.Volume && item.FileId == keep.FileId)
                                throw new IOException("与保留副本指向同一个文件标识（硬链接或路径别名），未执行回收。");
                            using (var target = Verify(item, true, reader, report, plan, result, token))
                            {
                                report(new Pulse(Phase.Recycle, item.Path, result.Done, plan.Count, result.Recycled.Count, result.Failed));
                                token.ThrowIfCancellationRequested();
                                recycle(item.Path, delegate {
                                    keeper.CheckLocation(); target.CheckLocation();
                                    // The Shell consumes a path. Reopen it and match the scan identity
                                    // after Shell parsing, while the verified target still denies writes.
                                    using (var named = new FileLease(item, true, false)) named.CheckLocation();
                                }, token);
                                result.Recycled.Add(item); // Do not lose a confirmed result if a later attempt is cancelled.
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!MediaPath.FileProblem(ex)) throw;
                            result.Failed++; result.LastFailure = RunLog.Reason(ex);
                            log.Skip("清理组 " + item.Group, item.Path, result.LastFailure);
                        }
                        if (result.Recycled.Contains(item)) log.Note("已回收\t" + item.Path + "\t保留\t" + keep.Path);
                        report(new Pulse(Phase.Recycle, item.Path, result.Done, plan.Count, result.Recycled.Count, result.Failed));
                    }
                }
                finally { if (keeper != null) keeper.Dispose(); }
            }
        }

        static FileLease Verify(Entry item, bool target, MediaReader reader, Action<Pulse> report, CleanPlan plan, CleanResult result, CancellationToken token)
        {
            Phase phase = target ? Phase.Verify : Phase.Keep;
            long done = 0;
            report(new Pulse(phase, item.Path, result.Done, plan.Count, result.Recycled.Count, result.Failed, 0, item.Bytes));
            MediaPath.CheckDirectory(Path.GetDirectoryName(item.Path));
            var file = new FileLease(item, target, false);
            try
            {
                file.CheckLocation();
                string hash = reader.Digest(file, false, token, delegate(int n) {
                    done += n; report(new Pulse(phase, item.Path, result.Done, plan.Count, result.Recycled.Count, result.Failed, done, item.Bytes));
                });
                if (!String.Equals(hash, item.Hash, StringComparison.Ordinal)) throw new IOException("完整 SHA-256 与扫描记录不同。");
                file.CheckLocation();
                return file;
            }
            catch { file.Dispose(); throw; }
        }
    }

    internal static class CsvReport
    {
        static string Quote(string s) { return "\"" + s.Replace("\"", "\"\"") + "\""; }
        static string DisplayName(string s)
        {
            string t = s.TrimStart();
            return Quote(t.Length != 0 && "=+-@".IndexOf(t[0]) >= 0 ? "'" + s : s);
        }
        internal static void Save(string path, IEnumerable<Entry> rows)
        {
            if (!String.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
                throw new IOException("只允许写入新的 .csv 文件。");
            using (var s = new FileStream(MediaPath.Extended(Path.GetFullPath(path)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(s, new UTF8Encoding(true)))
            {
                w.WriteLine("组号,已勾选,建议保留,类型,文件名,完整路径,大小（字节）,修改时间（本地）,SHA-256");
                foreach (var r in rows)
                    w.WriteLine(String.Join(",", r.Group.ToString(CultureInfo.InvariantCulture), r.Delete ? "是" : "否", r.Suggested ? "是" : "否",
                        Quote(r.Type), DisplayName(r.Name), Quote(r.Path), r.Bytes.ToString(CultureInfo.InvariantCulture), Quote(r.Modified), r.Hash));
            }
        }
    }
}
