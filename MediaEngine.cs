using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DuplicateMediaApp
{
    internal sealed class Entry
    {
        internal string Path, Type, Hash;
        internal long Bytes, WriteTime;
        internal uint Volume;
        internal ulong FileId;
        internal bool HasIdentity, Delete, Suggested;
        internal int Group;
        internal string Name { get { return Path.Substring(Path.LastIndexOf('\\') + 1); } }
        internal string Modified { get { return DateTime.FromFileTimeUtc(WriteTime).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); } }
    }

    internal static class MediaPath
    {
        internal static string Extended(string path)
        {
            return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
        }
        internal static string Display(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
            return path;
        }
        internal static string Normalize(string input)
        {
            string p = Display(input.Trim().Trim('"').Replace('/', '\\'));
            bool unc = p.StartsWith(@"\\", StringComparison.Ordinal) && !p.StartsWith(@"\\.\", StringComparison.Ordinal);
            if ((!unc && !(p.Length >= 3 && Char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\')) ||
                p.IndexOfAny(new char[] { '*', '?', '"', '<', '>', '|' }) >= 0 || p.IndexOf(':', unc ? 0 : 2) >= 0)
                throw new ArgumentException("请输入完整的磁盘或 UNC 文件夹路径，例如 D:\\照片。");
            p = Path.GetFullPath(p);
            string root = Path.GetPathRoot(p);
            if (p.Length > root.Length) p = p.TrimEnd('\\');
            return p;
        }
        internal static bool ProtectedName(string name)
        {
            return name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);
        }
        internal static void CheckDirectory(string path)
        {
            if (path.Split('\\').Any(ProtectedName)) throw new IOException("不扫描或清理回收站及系统卷信息目录。");
            var dir = new DirectoryInfo(Extended(path));
            for (; dir != null; dir = dir.Parent)
            {
                var attr = File.GetAttributes(dir.FullName);
                if ((attr & FileAttributes.Directory) == 0) throw new IOException("路径不是文件夹：" + Display(dir.FullName));
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("不跟随链接或重解析目录：" + Display(dir.FullName));
            }
        }
        internal static bool FileProblem(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException ||
                ex is Win32Exception || ex is COMException || ex is NotSupportedException || ex is ArgumentException;
        }
        internal static string Size(long n)
        {
            if (n > 0 && n <= 10) return "< 0.01 KB";
            double unit = n >= 1073741824 ? 1073741824.0 : n >= 1048576 ? 1048576.0 : 1024.0;
            return (n / unit).ToString("0.##") + (unit > 1048576 ? " GB" : unit > 1024 ? " MB" : " KB");
        }
    }

    internal sealed class RunLog : IDisposable
    {
        internal static readonly string DirectoryName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuplicateMediaApp");
        internal readonly string FileName;
        readonly StreamWriter writer;
        internal int Skips;

        internal RunLog(string kind, string directory = null)
        {
            string dir = directory ?? DirectoryName;
            Directory.CreateDirectory(dir);
            FileName = Path.Combine(dir, kind + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
            writer = new StreamWriter(new FileStream(FileName, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(true));
        }
        internal void Note(string msg) { writer.WriteLine(DateTime.Now.ToString("O") + "\t" + msg); }
        internal void Skip(string stage, string path, string why)
        {
            Skips++;
            Note(stage + "\t跳过\t" + path + "\t" + why);
        }
        internal static string Reason(Exception ex)
        {
            var native = ex as Win32Exception;
            return ex.Message + (native == null ? " (0x" + ex.HResult.ToString("X8") + ")" : " (Win32=" + native.NativeErrorCode + ")");
        }
        public void Dispose() { writer.Dispose(); }
    }

    // File identity, length and last write time all come from the SAME open handle.
    internal sealed class FileLease : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Info
        {
            internal uint Attributes, CreateLo, CreateHi, AccessLo, AccessHi, WriteLo, WriteHi,
                Volume, SizeHi, SizeLo, Links, IdHi, IdLo;
            internal long Length { get { return (long)SizeHi << 32 | SizeLo; } }
            internal long Time { get { return (long)WriteHi << 32 | WriteLo; } }
            internal ulong Id { get { return (ulong)IdHi << 32 | IdLo; } }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder name, uint capacity, uint flags);

        internal readonly FileStream Stream;
        readonly Entry entry;

        internal FileLease(Entry item, bool allowRename, bool sample)
        {
            entry = item;
            uint flags = 0x00200000u | (sample ? 0x10000000u : 0x08000000u); // OPEN_REPARSE_POINT | access hint
            SafeFileHandle handle = CreateFileW(MediaPath.Extended(item.Path), 0x80000000, allowRename ? 5u : 1u, IntPtr.Zero, 3, flags, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            try
            {
                Stream = new FileStream(handle, FileAccess.Read, 4096);
                CheckStamp();
            }
            catch { if (Stream != null) Stream.Dispose(); else handle.Dispose(); throw; }
        }
        internal void CheckStamp()
        {
            Info i;
            if (!GetFileInformationByHandle(Stream.SafeFileHandle, out i)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((i.Attributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Offline)) != 0)
                throw new IOException("不是可复核的本地普通文件。");
            if (i.Length != entry.Bytes || i.Time != entry.WriteTime ||
                (entry.HasIdentity && (i.Id != entry.FileId || i.Volume != entry.Volume)))
                throw new IOException("大小、修改时间或文件标识已变更，请重新扫描。");
            entry.FileId = i.Id; entry.Volume = i.Volume; entry.HasIdentity = true;
        }
        internal void CheckLocation()
        {
            CheckStamp();
            if (entry.FileId == 0) throw new IOException("卷未提供可验证的文件标识。");
            var b = new StringBuilder(512);
            uint n = GetFinalPathNameByHandleW(Stream.SafeFileHandle, b, (uint)b.Capacity, 0);
            if (n >= b.Capacity)
            {
                b.EnsureCapacity(checked((int)n + 1));
                n = GetFinalPathNameByHandleW(Stream.SafeFileHandle, b, (uint)b.Capacity, 0);
            }
            if (n == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (n >= b.Capacity || !String.Equals(MediaPath.Display(b.ToString()), entry.Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("已打开文件的实际路径已改变或经过链接，已跳过。");
        }
        public void Dispose() { Stream.Dispose(); }
    }

    internal enum Phase { Enumerate, Sample, Full, Keep, Verify, Recycle }
    internal struct Pulse
    {
        internal Phase Phase;
        internal string Path;
        internal long Done, Total, Part, PartTotal;
        internal int Ok, Fail;
        internal Pulse(Phase phase, string path, long done, long total, int ok = 0, int fail = 0, long part = 0, long partTotal = 0)
        { Phase = phase; Path = path; Done = done; Total = total; Ok = ok; Fail = fail; Part = part; PartTotal = partTotal; }
    }

    internal sealed class MediaReader
    {
        const int ProbeBytes = 16 * 1024;
        readonly byte[] buffer = new byte[256 * 1024];
        static readonly byte[] Empty = new byte[0];

        internal string Digest(FileLease file, bool sample, CancellationToken token, Action<int> onRead)
        {
            FileStream stream = file.Stream;
            using (var sha = SHA256.Create())
            {
                long length = stream.Length;
                if (sample && length > 3L * ProbeBytes)
                {
                    foreach (long offset in new long[] { 0, (length - ProbeBytes) / 2, length - ProbeBytes })
                    {
                        stream.Position = offset;
                        Read(stream, ProbeBytes, sha, token, onRead);
                    }
                }
                else Read(stream, length, sha, token, onRead);
                token.ThrowIfCancellationRequested();
                sha.TransformFinalBlock(Empty, 0, 0);
                file.CheckStamp();
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
        void Read(Stream stream, long count, HashAlgorithm sha, CancellationToken token, Action<int> onRead)
        {
            while (count > 0)
            {
                token.ThrowIfCancellationRequested();
                int n = stream.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
                if (n == 0) throw new EndOfStreamException("读取未达到预期长度。");
                sha.TransformBlock(buffer, 0, n, buffer, 0);
                count -= n;
                if (onRead != null) onRead(n);
            }
        }
    }

    internal sealed class ScanResult
    {
        internal List<Entry> Rows = new List<Entry>();
        internal int Files, Candidates, Finalists, Groups, Skipped;
        internal long ReadBytes, TotalBytes;
        internal TimeSpan EnumTime, SampleTime, FullTime, TotalTime;
        internal string Metrics()
        {
            return String.Format(CultureInfo.InvariantCulture,
                "media={0} sizeCandidates={1} fullCandidates={2} groups={3} rows={4} skipped={5} readBytes={6}/{7} enum={8:F3}s sample={9:F3}s full={10:F3}s total={11:F3}s",
                Files, Candidates, Finalists, Groups, Rows.Count, Skipped, ReadBytes, TotalBytes,
                EnumTime.TotalSeconds, SampleTime.TotalSeconds, FullTime.TotalSeconds, TotalTime.TotalSeconds);
        }
    }

    internal static class Scanner
    {
        static readonly HashSet<string> Photos = new HashSet<string>(".jpg .jpeg .jpe .png .heic .heif .webp .tif .tiff .gif .bmp .dng .raw .cr2 .cr3 .nef .arw .orf .rw2 .raf .pef .srw".Split(' '), StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> Videos = new HashSet<string>(".mp4 .mov .avi .mkv .wmv .m4v .mts .m2ts .webm .ts .mpg .mpeg .3gp .3g2 .flv .vob".Split(' '), StringComparer.OrdinalIgnoreCase);

        internal static ScanResult Run(string input, RunLog log, Action<Pulse> report, CancellationToken token)
        {
            var clock = Stopwatch.StartNew();
            var r = new ScanResult();
            string root = MediaPath.Normalize(input);
            log.Note("只读扫描\t" + root);
            MediaPath.CheckDirectory(root);
            var entries = new List<Entry>();
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested();
                string dir = pending.Pop();
                report(new Pulse(Phase.Enumerate, dir, entries.Count, 0));
                try
                {
                    var info = new DirectoryInfo(MediaPath.Extended(dir));
                    if ((File.GetAttributes(info.FullName) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("不跟随重解析目录。");
                    foreach (var child in info.EnumerateFileSystemInfos())
                    {
                        token.ThrowIfCancellationRequested();
                        string path = Path.Combine(dir, child.Name);
                        try
                        {
                            var a = child.Attributes;
                            if ((a & FileAttributes.Directory) != 0)
                            {
                                if (MediaPath.ProtectedName(child.Name) || (a & FileAttributes.ReparsePoint) != 0)
                                    log.Skip("枚举", path, "保护目录或重解析点，不递归。");
                                else pending.Push(path);
                                continue;
                            }
                            string ext = child.Extension;
                            if (!Photos.Contains(ext) && !Videos.Contains(ext)) continue;
                            if ((a & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) throw new IOException("不跟随链接或下载离线占位文件。");
                            var f = (FileInfo)child;
                            entries.Add(new Entry { Path = path, Type = Videos.Contains(ext) ? "视频" : "照片", Bytes = f.Length, WriteTime = f.LastWriteTimeUtc.ToFileTimeUtc() });
                            report(new Pulse(Phase.Enumerate, path, entries.Count, 0));
                        }
                        catch (Exception ex) { if (!MediaPath.FileProblem(ex)) throw; log.Skip("枚举", path, RunLog.Reason(ex)); }
                    }
                }
                catch (Exception ex)
                {
                    if (!MediaPath.FileProblem(ex)) throw;
                    log.Skip("枚举", dir, RunLog.Reason(ex));
                    if (dir == root) throw;
                }
            }
            r.Files = entries.Count; r.EnumTime = clock.Elapsed;
            var candidates = entries.GroupBy(x => x.Bytes).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            r.Candidates = candidates.Count;
            var reader = new MediaReader();
            var sampled = new List<Entry>();
            report(new Pulse(Phase.Sample, root, 0, candidates.Count));
            for (int i = 0; i < candidates.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                Entry item = candidates[i];
                try
                {
                    using (var f = new FileLease(item, false, true)) item.Hash = reader.Digest(f, true, token, null);
                    sampled.Add(item);
                }
                catch (Exception ex) { if (!MediaPath.FileProblem(ex)) throw; log.Skip("快速筛选", item.Path, RunLog.Reason(ex)); }
                report(new Pulse(Phase.Sample, item.Path, i + 1, candidates.Count));
            }
            var finalists = sampled.GroupBy(x => new { x.Bytes, x.Hash }).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
            r.Finalists = finalists.Count; r.TotalBytes = finalists.Sum(x => x.Bytes);
            r.SampleTime = clock.Elapsed - r.EnumTime;
            report(new Pulse(Phase.Full, root, 0, r.TotalBytes));
            var verified = new List<Entry>();
            foreach (Entry item in finalists)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var f = new FileLease(item, false, false))
                        item.Hash = reader.Digest(f, false, token, delegate(int n) {
                            r.ReadBytes += n;
                            report(new Pulse(Phase.Full, item.Path, r.ReadBytes, r.TotalBytes));
                        });
                    verified.Add(item);
                }
                catch (Exception ex) { if (!MediaPath.FileProblem(ex)) throw; log.Skip("完整核对", item.Path, RunLog.Reason(ex)); }
            }
            var groups = verified.GroupBy(x => new { x.Bytes, x.Hash }).Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Key.Bytes).ThenBy(g => g.Key.Hash, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                token.ThrowIfCancellationRequested();
                bool first = true; r.Groups++;
                foreach (var item in group.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Path, StringComparer.Ordinal))
                {
                    item.Group = r.Groups; item.Suggested = first; item.Delete = !first; first = false; r.Rows.Add(item);
                }
            }
            token.ThrowIfCancellationRequested();
            r.TotalTime = clock.Elapsed; r.FullTime = r.TotalTime - r.EnumTime - r.SampleTime;
            r.Skipped = log.Skips; log.Note(r.Metrics());
            return r;
        }
    }
}
