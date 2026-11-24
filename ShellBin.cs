using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DuplicateMediaApp
{
    internal static class ShellBin
    {
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out ITransferSource source);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        // Only expose the vtable as far as RecycleItem. The separate RemoveItem
        // (permanent deletion) method is deliberately not imported.
        [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ITransferSource
        {
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetProperties(IntPtr properties);
            void OpenItem(IShellItem item, uint flags, ref Guid iid, out IntPtr result);
            void MoveItem(IShellItem item, IShellItem parent, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IShellItem result);
            [PreserveSig] int RecycleItem(IShellItem item, IShellItem recycleFolder, uint flags, out IShellItem recycled);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderItem(ref Guid folder, uint flags, IntPtr token, ref Guid iid, out IShellItem item);

        static void Check(int hr, string step)
        {
            if (hr == 0) return;
            string why;
            switch (unchecked((uint)hr))
            {
                case 0x80270036: why = "此位置禁用了回收"; break;
                case 0x80270037: why = "文件超过回收站容量"; break;
                case 0x80270038: why = "回收站不支持此路径长度"; break;
                case 0x8027003A: why = "找不到可用回收站"; break;
                default: var ex = Marshal.GetExceptionForHR(hr); why = ex == null ? "系统未确认操作" : ex.Message; break;
            }
            throw new IOException(step + "：" + why + " (HRESULT=0x" + hr.ToString("X8") + ")");
        }

        internal static void RequireLocalVolume(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("网络路径不能确认本机回收站，已跳过。");
            DriveType type = new DriveInfo(Path.GetPathRoot(path)).DriveType;
            if (type != DriveType.Fixed && type != DriveType.Removable)
                throw new IOException("此卷不是可使用本机回收站的磁盘。");
        }

        internal static void Recycle(string path, Action recheck, CancellationToken token)
        {
            RequireLocalVolume(path);
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("回收站操作需要 STA 线程。");
            Guid shellItem = typeof(IShellItem).GUID, transferSource = typeof(ITransferSource).GUID;
            Guid transfer = new Guid("D5E346A1-F753-4932-B403-4574800E2498");
            Guid recycleFolder = new Guid("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            IShellItem item = null, parent = null, bin = null, result = null;
            ITransferSource source = null;
            try
            {
                Check(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItem, out item), "解析源文件失败");
                Check(item.GetParent(out parent), "无法获取父目录");
                Check(parent.BindToHandler(IntPtr.Zero, ref transfer, ref transferSource, out source), "此目录不支持系统回收接口");
                Check(SHGetKnownFolderItem(ref recycleFolder, 0, IntPtr.Zero, ref shellItem, out bin), "无法打开回收站");
                string shellPath;
                Check(item.GetDisplayName(0x80058000, out shellPath), "无法确认系统路径"); // SIGDN_FILESYSPATH
                if (!String.Equals(path, shellPath, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("系统解析的路径与清单不一致，已跳过。");
                recheck();
                token.ThrowIfCancellationRequested();
                int hr = source.RecycleItem(item, bin, 0, out result);
                if (hr < 0) Check(hr, "不能移入回收站");
                // The filesystem transfer handler also returns DONT_PROCESS_CHILDREN
                // for a successfully recycled leaf. Never accept a missing receipt.
                if ((hr != 0 && hr != 0x00270008) || result == null)
                    throw new IOException("Windows 未确认生成回收站条目，未计为成功。HRESULT=0x" + hr.ToString("X8") + "，收到条目=" + (result != null));
            }
            finally
            {
                Release(result); Release(bin); Release(source); Release(parent); Release(item);
            }
        }

        static void Release(object value)
        {
            if (value != null) Marshal.ReleaseComObject(value);
        }
    }
}
