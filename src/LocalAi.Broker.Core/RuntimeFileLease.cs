using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalAi.Broker;

internal static class RuntimeFileLease
{
    public static SafeFileHandle Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var nativePath = fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? fullPath :
            fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
        // No data-access rights: even a writer using FileShare.None can coexist with this
        // metadata lease. Omitting delete sharing prevents replacement during validation.
        var handle = CreateFile(nativePath, 0, FileShare.ReadWrite, IntPtr.Zero,
            FileMode.Open, 0x00200000 /* FILE_FLAG_OPEN_REPARSE_POINT */, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) ThrowNativeError();
            if (!GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8)) ThrowNativeError();
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw RuntimeAcl.ReparseRefusal(path);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void ThrowNativeError() => throw Marshal.GetExceptionForHR(
        unchecked((int)(0x80070000u | (uint)Marshal.GetLastPInvokeError())))!;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileAttributeTagInformation information, uint size);
}
