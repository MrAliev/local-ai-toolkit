using System.Runtime.InteropServices;
using LocalAi.Installer.Core.Models;

namespace LocalAi.Installer.Core.Diagnosis;

public static class PhysicalMemoryProbe
{
    public static PhysicalMemorySnapshot Observe()
    {
        if (!OperatingSystem.IsWindows()) return new(ObservationState.Unsupported, null, null);
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0 || status.AvailablePhysical > status.TotalPhysical)
            return new(ObservationState.Failed, null, null);
        return new(ObservationState.Available, status.TotalPhysical, status.AvailablePhysical);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
