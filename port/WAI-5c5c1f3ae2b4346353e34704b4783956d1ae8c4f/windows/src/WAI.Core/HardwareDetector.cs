// Hardware detection and backend resolution for AI embedding acceleration.
// Supports:
//  - CPU: Any Intel / AMD x64 or ARM64 processor (pure CPU, AVX/AVX2/AVX-512)
//  - Discrete GPUs: NVIDIA (CUDA / Vulkan), AMD Radeon (Vulkan), Intel Arc (Vulkan)
//  - Integrated GPUs: Intel Iris Xe / UHD (Vulkan), AMD Radeon APU (Vulkan)
// Uses native Windows DXGI enumeration (dxgi.dll) with zero external dependencies.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WAI.Core;

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
    public string DisplayName => GetDisplayName(true);
    public string GetDisplayName(bool isRu = true) =>
        $"{Name} ({(isRu ? (IsDiscrete ? "Дискретная" : "Встроенная") : (IsDiscrete ? "Discrete" : "Integrated"))}, {VramMb} {(isRu ? "МБ" : "MB")} VRAM)";
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

    public string Summary => GetSummary(true);

    public string GetSummary(bool isRu = true)
    {
        var parts = new List<string>();
        if (DiscreteGpu != null)
            parts.Add(isRu ? $"Дискретная: {DiscreteGpu.Name} ({DiscreteGpu.VramMb} МБ)" : $"Discrete: {DiscreteGpu.Name} ({DiscreteGpu.VramMb} MB)");
        if (IntegratedGpu != null)
            parts.Add(isRu ? $"Встроенная: {IntegratedGpu.Name}" : $"Integrated: {IntegratedGpu.Name}");
        parts.Add(isRu ? $"CPU: {CpuCores} потоков ({CpuArchitecture})" : $"CPU: {CpuCores} threads ({CpuArchitecture})");
        return string.Join(" | ", parts);
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

    public static (string Backend, int DeviceIndex, string Summary) ResolveBackend(string? requestedMode, HardwareInfo info, bool isRu = true)
    {
        string mode = (requestedMode ?? "auto").ToLowerInvariant().Trim();

        switch (mode)
        {
            case "cpu":
                return ("cpu", -1, isRu ? $"CPU ({info.CpuCores} потоков)" : $"CPU ({info.CpuCores} threads)");

            case "gpu_ext":
                if (info.DiscreteGpu != null)
                {
                    var g = info.DiscreteGpu;
                    string b = g.HasCuda && g.VendorId == 0x10DE ? "cuda" : "vulkan";
                    return (b, g.Index, isRu ? $"Дискретный GPU: {g.Name}" : $"Discrete GPU: {g.Name}");
                }
                // Fallback if no discrete GPU
                break;

            case "gpu_int":
                if (info.IntegratedGpu != null)
                {
                    var g = info.IntegratedGpu;
                    return ("vulkan", g.Index, isRu ? $"Встроенный GPU: {g.Name}" : $"Integrated GPU: {g.Name}");
                }
                // Fallback if no integrated GPU
                break;
        }

        // Auto resolution: AutoTune (checks discrete GPU, integrated GPU vs CPU speed, and vector agreement)
        var auto = AutoTune(info, isRu: isRu);
        return (auto.RecommendedBackend, auto.DeviceIndex, auto.Reason);
    }

    public const double VectorAgreementThreshold = 0.999;

    /// <summary>
    /// Computes the cosine agreement between CPU and GPU embedding vectors.
    /// Returns 1.0 for identical vectors; &lt; 0.999 indicates numerical instability or driver artifacts.
    /// </summary>
    public static double CalculateAgreement(ReadOnlySpan<float> cpuVector, ReadOnlySpan<float> gpuVector)
    {
        if (cpuVector.Length != gpuVector.Length || cpuVector.Length == 0) return 0.0;
        float dot = Vectors.DotProduct(cpuVector, gpuVector);
        double normCpu = 0, normGpu = 0;
        for (int i = 0; i < cpuVector.Length; i++)
        {
            normCpu += cpuVector[i] * cpuVector[i];
            normGpu += gpuVector[i] * gpuVector[i];
        }
        if (normCpu <= 0 || normGpu <= 0) return 0.0;
        return dot / Math.Sqrt(normCpu * normGpu);
    }

    public sealed record AutoTuneResult(
        string RecommendedBackend,
        int DeviceIndex,
        double VectorAgreement,
        bool FallbackToCpu,
        string Reason);

    /// <summary>
    /// Evaluates hardware options, checks vector agreement against CPU baseline,
    /// and falls back to CPU if integrated GPU (Intel UHD) underperforms or vectors diverge (OPT-05).
    /// </summary>
    public static AutoTuneResult AutoTune(
        HardwareInfo info,
        ReadOnlySpan<float> sampleCpuVector = default,
        ReadOnlySpan<float> sampleGpuVector = default,
        double? cpuLatencyMs = null,
        double? gpuLatencyMs = null,
        bool isRu = true)
    {
        // 1. Vector agreement verification (if samples provided)
        if (!sampleCpuVector.IsEmpty && !sampleGpuVector.IsEmpty)
        {
            double agreement = CalculateAgreement(sampleCpuVector, sampleGpuVector);
            if (agreement < VectorAgreementThreshold)
            {
                return new AutoTuneResult(
                    "cpu",
                    -1,
                    agreement,
                    FallbackToCpu: true,
                    Reason: isRu
                        ? $"Расхождение векторов GPU с эталоном CPU ({agreement:F4} < {VectorAgreementThreshold}). Выбран безопасный откат на CPU."
                        : $"GPU vector divergence from CPU baseline ({agreement:F4} < {VectorAgreementThreshold}). Safely falling back to CPU.");
            }
        }

        // 2. Discrete GPU priority
        if (info.DiscreteGpu != null)
        {
            var g = info.DiscreteGpu;
            string b = g.HasCuda && g.VendorId == 0x10DE ? "cuda" : "vulkan";
            return new AutoTuneResult(b, g.Index, 1.0, false,
                isRu ? $"Авто: Дискретный GPU {g.Name} ({g.VramMb} МБ)" : $"Auto: Discrete GPU {g.Name} ({g.VramMb} MB)");
        }

        // 3. Integrated GPU: latency or heuristic benchmark
        if (info.IntegratedGpu != null)
        {
            var g = info.IntegratedGpu;
            if (cpuLatencyMs.HasValue && gpuLatencyMs.HasValue && cpuLatencyMs.Value < gpuLatencyMs.Value)
            {
                return new AutoTuneResult("cpu", -1, 1.0, true,
                    isRu
                        ? $"Авто: CPU быстрее встроенной графики {g.Name} ({cpuLatencyMs.Value:F1}мс vs {gpuLatencyMs.Value:F1}мс)"
                        : $"Auto: CPU is faster than integrated graphics {g.Name} ({cpuLatencyMs.Value:F1}ms vs {gpuLatencyMs.Value:F1}ms)");
            }

            // Older Intel UHD with 8+ CPU cores usually slower than AVX2 CPU
            if (g.VendorId == 0x8086 && g.Name.Contains("UHD", StringComparison.OrdinalIgnoreCase) && info.CpuCores >= 8)
            {
                return new AutoTuneResult("cpu", -1, 1.0, true,
                    isRu
                        ? $"Авто: Для {g.Name} на {info.CpuCores}-поточном CPU выбран процессорный режим"
                        : $"Auto: For {g.Name} on {info.CpuCores}-thread CPU, CPU mode selected");
            }

            return new AutoTuneResult("vulkan", g.Index, 1.0, false,
                isRu ? $"Авто: {g.Name} (Встроенная)" : $"Auto: {g.Name} (Integrated)");
        }

        return new AutoTuneResult("cpu", -1, 1.0, false,
            isRu ? $"Авто: CPU ({info.CpuCores} потоков)" : $"Auto: CPU ({info.CpuCores} threads)");
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
