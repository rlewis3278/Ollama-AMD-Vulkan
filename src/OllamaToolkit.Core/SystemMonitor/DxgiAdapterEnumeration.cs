using System.Runtime.InteropServices;

namespace OllamaToolkit.Core.SystemMonitor;

internal sealed record DxgiAdapterInfo(
    uint AdapterLuidHigh,
    uint AdapterLuidLow,
    string Description);

internal static class DxgiAdapterEnumeration
{
    private static readonly Guid Factory1Guid = new("770AAE78-F26F-4DA6-A2DA-09689EFF0867");

    public static IReadOnlyList<DxgiAdapterInfo> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<DxgiAdapterInfo>();
        }

        var adapters = new List<DxgiAdapterInfo>();
        try
        {
            var factoryGuid = Factory1Guid; var hr = CreateDXGIFactory1(ref factoryGuid, out var factory);
            if (hr < 0 || factory is null)
            {
                return adapters;
            }

            for (uint i = 0; ; i++)
            {
                hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == unchecked((int)0x887A0002))
                {
                    break;
                }

                if (hr < 0 || adapter is null)
                {
                    break;
                }

                adapter.GetDesc1(out var desc);
                adapters.Add(new DxgiAdapterInfo(
                    desc.AdapterLuidHigh,
                    desc.AdapterLuidLow,
                    desc.Description.TrimEnd('\0')));
            }
        }
        catch
        {
            // DXGI unavailable.
        }

        return adapters;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public uint AdapterLuidLow;
        public uint AdapterLuidHigh;
        public uint Flags;
    }

    [ComImport]
    [Guid("7B7166EC-21C7-44AE-B21A-4169DBD01A66")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory
    {
        [PreserveSig]
        int EnumAdapters(uint Adapter, out IntPtr ppAdapter);

        void MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
        IntPtr GetWindowAssociation();

        [PreserveSig]
        int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);

        [PreserveSig]
        int CreateSoftwareAdapter(IntPtr Module, out IntPtr ppAdapter);
    }

    [ComImport]
    [Guid("770AAE78-F26F-4DA6-A2DA-09689EFF0867")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1 : IDXGIFactory
    {
        [PreserveSig]
        new int EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
    }

    [ComImport]
    [Guid("2411E7E1-12AC-4CCF-B14C-DA8397000594")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        [PreserveSig]
        int EnumOutputs(uint Output, out IntPtr ppOutput);

        [PreserveSig]
        int GetDesc(out DXGI_ADAPTER_DESC pDesc);

        [PreserveSig]
        int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public uint AdapterLuidLow;
        public uint AdapterLuidHigh;
    }

    [ComImport]
    [Guid("29038F61-3839-4626-91FD-86EED6F3340B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1 : IDXGIAdapter
    {
        void GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }
}

