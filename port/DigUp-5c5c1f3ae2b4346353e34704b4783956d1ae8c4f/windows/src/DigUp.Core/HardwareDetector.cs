// Hardware detection and backend resolution for AI embedding acceleration.
// Supports:
//  - CPU: Any Intel / AMD x64 or ARM64 processor (pure CPU, AVX/AVX2/AVX-512)
//  - Discrete GPUs: NVIDIA (CUDA / Vulkan), AMD Radeon (Vulkan), Intel Arc (Vulkan)
//  - Integrated GPUs: Intel Iris Xe / UHD (Vulkan), AMD Radeon APU (Vulkan)
// Uses native Windows DXGI enumeration (dxgi.dll) with zero external dependencies.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DigUp.Core;

public sealed record GpuDevice(
    int Index,
    string Name,
    string Vendor,
    uint VendorId,
    ulong VramMb,
    bool IsDiscrete,
    bool HasVulkan,
    bool HasCuda)
{
    public string DisplayName =>
        $"{Name} ({(IsDiscrete ? "Дискретная" : "Встроенная")}, {VramMb} МБ VRAM)";
}

public sealed record HardwareInfo(
    int CpuCores,
    string CpuArchitecture,
    IReadOnlyList<GpuDevice> Gpus,
    bool SystemVulkanAvailable,
    bool SystemCudaAvailable)
{
    public GpuDevice? DiscreteGpu => Gpus.FirstOrDefault(g => g.IsDiscrete);
    public GpuDevice? IntegratedGpu => Gpus.FirstOrDefault(g => !g.IsDiscrete);

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (DiscreteGpu != null)
                parts.Add($"Дискретная: {DiscreteGpu.Name} ({DiscreteGpu.VramMb} МБ)");
            if (IntegratedGpu != null)
                parts.Add($"Встроенная: {IntegratedGpu.Name}");
            parts.Add($"CPU: {CpuCores} потоков ({CpuArchitecture})");
            return string.Join(" | ", parts);
        }
    }
}

public static class HardwareDetector
{
    private static HardwareInfo? _cachedInfo;
    private static readonly object _lock = new();

    public static HardwareInfo Detect(bool forceRefresh = false)
    {
        lock (_lock)
        {
            if (_cachedInfo != null && !forceRefresh)
                return _cachedInfo;

            int cpuCores = Environment.ProcessorCount;
            string cpuArch = RuntimeInformation.ProcessArchitecture.ToString();

            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            bool hasVulkan = File.Exists(Path.Combine(systemDir, "vulkan-1.dll"));
            bool hasCuda = File.Exists(Path.Combine(systemDir, "nvcuda.dll"));

            var gpus = EnumerateGpus(hasVulkan, hasCuda);

            _cachedInfo = new HardwareInfo(cpuCores, cpuArch, gpus, hasVulkan, hasCuda);
            return _cachedInfo;
        }
    }

    public static (string Backend, int DeviceIndex, string Summary) ResolveBackend(string? requestedMode, HardwareInfo info)
    {
        string mode = (requestedMode ?? "auto").ToLowerInvariant().Trim();

        switch (mode)
        {
            case "cpu":
                return ("cpu", -1, $"CPU ({info.CpuCores} потоков)");

            case "gpu_ext":
                if (info.DiscreteGpu != null)
                {
                    var g = info.DiscreteGpu;
                    string b = g.HasCuda && g.VendorId == 0x10DE ? "cuda" : "vulkan";
                    return (b, g.Index, $"Дискретный GPU: {g.Name}");
                }
                // Fallback if no discrete GPU
                break;

            case "gpu_int":
                if (info.IntegratedGpu != null)
                {
                    var g = info.IntegratedGpu;
                    return ("vulkan", g.Index, $"Встроенный GPU: {g.Name}");
                }
                // Fallback if no integrated GPU
                break;
        }

        // Auto resolution: Discrete GPU -> Integrated GPU -> CPU fallback
        if (info.DiscreteGpu != null)
        {
            var g = info.DiscreteGpu;
            string b = g.HasCuda && g.VendorId == 0x10DE ? "cuda" : "vulkan";
            return (b, g.Index, $"Авто: {g.Name} (Дискретная)");
        }

        if (info.IntegratedGpu != null)
        {
            var g = info.IntegratedGpu;
            return ("vulkan", g.Index, $"Авто: {g.Name} (Встроенная)");
        }

        return ("cpu", -1, $"Авто: CPU ({info.CpuCores} потоков)");
    }

    private static List<GpuDevice> EnumerateGpus(bool systemVulkan, bool systemCuda)
    {
        var result = new List<GpuDevice>();
        try
        {
            var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            int hr = CreateDXGIFactory1(ref guid, out IntPtr factoryPtr);
            if (hr != 0 || factoryPtr == IntPtr.Zero)
                return result;

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            uint i = 0;
            IntPtr adapterPtr;
            while (factory.EnumAdapters1(i, out adapterPtr) == 0)
            {
                try
                {
                    var adapter = (IDXGIAdapter1)Marshal.GetObjectForIUnknown(adapterPtr);
                    adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc);

                    bool isSoftware = (desc.Flags & 2) != 0; // DXGI_ADAPTER_FLAG_SOFTWARE
                    if (!isSoftware && !desc.Description.Contains("Basic Render Driver", StringComparison.OrdinalIgnoreCase))
                    {
                        ulong vramMb = (ulong)desc.DedicatedVideoMemory.ToUInt64() / (1024 * 1024);
                        string vendor = desc.VendorId switch
                        {
                            0x10DE => "NVIDIA",
                            0x1002 => "AMD",
                            0x8086 => "Intel",
                            0x5143 => "Qualcomm",
                            _ => $"Other (0x{desc.VendorId:X})"
                        };

                        // Discrete GPUs typically have >= 1024 MB dedicated video memory and non-Intel iGPU IDs
                        // Intel Arc has dedicated VRAM (> 1GB).
                        bool isDiscrete = vramMb > 1024 && desc.VendorId != 0x1414;

                        bool gpuVulkan = systemVulkan;
                        bool gpuCuda = systemCuda && desc.VendorId == 0x10DE;

                        // Avoid duplicate adapter entries
                        if (!result.Any(existing => existing.Name == desc.Description && existing.VramMb == vramMb))
                        {
                            result.Add(new GpuDevice(
                                Index: result.Count,
                                Name: desc.Description.Trim(),
                                Vendor: vendor,
                                VendorId: desc.VendorId,
                                VramMb: vramMb,
                                IsDiscrete: isDiscrete,
                                HasVulkan: gpuVulkan,
                                HasCuda: gpuCuda
                            ));
                        }
                    }
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }
                i++;
            }
            Marshal.Release(factoryPtr);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to enumerate GPUs via DXGI: {ex.Message}");
        }

        return result;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int EnumAdapters(uint Adapter, out IntPtr ppAdapter);
        int MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
        int GetWindowAssociation(out IntPtr pWindowHandle);
        int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        int CreateSoftwareAdapter(IntPtr Module, out IntPtr ppAdapter);
        int EnumAdapters1(uint Adapter, out IntPtr ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        int SetPrivateData(ref Guid Name, uint DataSize, IntPtr pData);
        int SetPrivateDataInterface(ref Guid Name, IntPtr pUnknown);
        int GetPrivateData(ref Guid Name, ref uint pDataSize, IntPtr pData);
        int GetParent(ref Guid riid, out IntPtr ppParent);
        int EnumOutputs(uint Output, out IntPtr ppOutput);
        int GetDesc(out IntPtr pDesc);
        int CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
        int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }
}
