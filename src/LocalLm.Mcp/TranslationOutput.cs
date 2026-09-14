using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LocalLm.Mcp;

internal static class TranslationOutput
{
    public static bool IsSource(string source, string destination)
    {
        if (string.Equals(source, destination, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }
        if (!File.Exists(source) || !File.Exists(destination))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            // Opening the files resolves reparse points and directory aliases as well as
            // hard links. Names alone cannot establish whether overwriting loses the source.
            using var input = File.OpenHandle(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var output = File.OpenHandle(destination, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return Identity(input) == Identity(output);
        }

        return string.Equals(
            new FileInfo(source).ResolveLinkTarget(true)?.FullName ?? source,
            new FileInfo(destination).ResolveLinkTarget(true)?.FullName ?? destination,
            StringComparison.Ordinal);
    }

    private static (uint Volume, uint High, uint Low) Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return (information.VolumeSerialNumber, information.FileIndexHigh, information.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
