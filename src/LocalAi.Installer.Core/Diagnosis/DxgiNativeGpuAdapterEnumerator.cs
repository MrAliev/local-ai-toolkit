using System.Runtime.InteropServices;

namespace LocalAi.Installer.Core.Diagnosis;

public sealed class DxgiNativeGpuAdapterEnumerator : INativeGpuAdapterEnumerator
{
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint D3dFeatureLevel11 = 0xB000;
    private const uint D3d12FeatureArchitecture1 = 16;
    private static readonly Guid IdxgiFactory1 = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
    private static readonly Guid Id3d12Device = new("189819F1-1DB6-4B57-BE54-1821339B85F7");

    public NativeGpuEnumeration Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NativeGpuEnumeration(
                ObservationState.Unsupported,
                [],
                "DXGI is only available on Windows.");
        }

        IntPtr factory = IntPtr.Zero;
        try
        {
            var factoryId = IdxgiFactory1;
            var result = CreateDXGIFactory1(ref factoryId, out factory);
            Marshal.ThrowExceptionForHR(result);
            return EnumerateAdapters(factory);
        }
        catch (Exception exception) when (
            exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new NativeGpuEnumeration(
                ObservationState.Failed,
                [],
                $"DXGI adapter enumeration failed: {exception.Message}");
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    private static NativeGpuEnumeration EnumerateAdapters(IntPtr factory)
    {
        var adapters = new List<NativeGpuAdapterDescriptor>();
        var enumerate = GetVTableDelegate<EnumAdapters1Delegate>(factory, 12);
        for (uint index = 0; ; index++)
        {
            var result = enumerate(factory, index, out var adapter);
            if (result == DxgiErrorNotFound)
            {
                break;
            }

            Marshal.ThrowExceptionForHR(result);
            try
            {
                var getDescription = GetVTableDelegate<GetDesc1Delegate>(adapter, 10);
                Marshal.ThrowExceptionForHR(getDescription(adapter, out var description));
                var isSoftware = (description.Flags & DxgiAdapterFlagSoftware) != 0;
                adapters.Add(new NativeGpuAdapterDescriptor(
                    $"LUID:{description.AdapterLuid.HighPart:X8}{description.AdapterLuid.LowPart:X8}",
                    description.Description.TrimEnd('\0'),
                    description.DedicatedVideoMemory,
                    isSoftware)
                {
                    SharedSystemMemoryBytes = description.SharedSystemMemory,
                    IsUma = !isSoftware && IsUnifiedMemoryAdapter(adapter),
                });
            }
            finally
            {
                Marshal.Release(adapter);
            }
        }

        return new NativeGpuEnumeration(
            ObservationState.Available,
            adapters,
            null);
    }

    private static bool IsUnifiedMemoryAdapter(IntPtr adapter)
    {
        IntPtr device = IntPtr.Zero;
        try
        {
            var deviceId = Id3d12Device;
            if (D3D12CreateDevice(adapter, D3dFeatureLevel11, ref deviceId, out device) < 0 || device == IntPtr.Zero)
            {
                return false;
            }

            // The architecture feature describes one physical node. For a linked adapter,
            // node zero cannot establish the architecture of the whole adapter.
            var getNodeCount = GetVTableDelegate<GetNodeCountDelegate>(device, 7);
            if (getNodeCount(device) != 1)
            {
                return false;
            }

            var checkFeature = GetVTableDelegate<CheckFeatureSupportDelegate>(device, 13);
            var architecture = new D3d12Architecture1 { NodeIndex = 0 };
            return checkFeature(device, D3d12FeatureArchitecture1, ref architecture,
                (uint)Marshal.SizeOf<D3d12Architecture1>()) >= 0 && architecture.Uma != 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // DXGI's dedicated-memory report remains useful without the D3D12 runtime.
            return false;
        }
        finally
        {
            if (device != IntPtr.Zero)
            {
                Marshal.Release(device);
            }
        }
    }

    private static T GetVTableDelegate<T>(IntPtr instance, int methodIndex)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        var method = Marshal.ReadIntPtr(vtable, methodIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        ref Guid riid,
        out IntPtr factory);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3D12CreateDevice(
        IntPtr adapter,
        uint minimumFeatureLevel,
        ref Guid riid,
        out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Delegate(
        IntPtr factory,
        uint adapter,
        out IntPtr adapterPointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Delegate(
        IntPtr adapter,
        out DxgiAdapterDescription1 description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetNodeCountDelegate(IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFeatureSupportDelegate(
        IntPtr device,
        uint feature,
        ref D3d12Architecture1 featureData,
        uint featureDataSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d12Architecture1
    {
        public uint NodeIndex;
        public int TileBasedRenderer;
        public int Uma;
        public int CacheCoherentUma;
        public int IsolatedMmu;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDescription1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }
}
