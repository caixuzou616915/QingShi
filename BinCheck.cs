using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

internal static class BinCheck
{
    internal static string UniqueName()
    {
        return "轻拾验收-" + Guid.NewGuid().ToString("N") + ".jpg";
    }

    internal static void Restore(string path, byte[] expected)
    {
        string name = Path.GetFileName(path);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
        dynamic bin = shell.NameSpace(10); // ssfBITBUCKET
        var clock = Stopwatch.StartNew();
        bool requested = false;
        while (clock.Elapsed.TotalSeconds < 12 && !File.Exists(path))
        {
            if (!requested)
            {
                dynamic items = bin.Items();
                foreach (dynamic item in items)
                {
                    string fileName = Convert.ToString(item.ExtendedProperty("System.FileName"));
                    if (!String.Equals(fileName, name, StringComparison.Ordinal)) continue;
                    string origin = Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom"));
                    if (!String.Equals(origin, Path.GetDirectoryName(path), StringComparison.OrdinalIgnoreCase)) continue;
                    // Restore this one Shell item into its original directory, without any UI.
                    shell.NameSpace(origin).MoveHere(item, 4 | 16 | 1024);
                    requested = true;
                    break;
                }
            }
            Thread.Sleep(100);
        }
        if (!requested || !File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expected))
            throw new IOException("未能还原本测试的唯一文件：" + path);
    }

}
