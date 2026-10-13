using DigUp.Core;
using Xunit;

namespace DigUp.Tests;

public class HardwareDetectorTests
{
    [Fact]
    public void Detect_ReturnsLiveSystemInformation()
    {
        var info = HardwareDetector.Detect();
        Assert.NotNull(info);
        Assert.True(info.CpuCores >= 1);
        Assert.False(string.IsNullOrWhiteSpace(info.CpuArchitecture));
        Assert.False(string.IsNullOrWhiteSpace(info.Summary));
    }

    [Fact]
    public void ResolveBackend_ExplicitCpu_AlwaysReturnsCpu()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "NVIDIA GeForce RTX 4090", "NVIDIA", 0x10DE, 24576, IsDiscrete: true, HasVulkan: true, HasCuda: true),
            new GpuDevice(1, "Intel UHD Graphics 770", "Intel", 0x8086, 128, IsDiscrete: false, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(16, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: true);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("cpu", info);

        Assert.Equal("cpu", backend);
        Assert.Equal(-1, deviceIdx);
        Assert.Contains("CPU", summary);
    }

    [Fact]
    public void ResolveBackend_Auto_PrefersDiscreteGpu()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "AMD Radeon RX 7900 GRE", "AMD", 0x1002, 16384, IsDiscrete: true, HasVulkan: true, HasCuda: false),
            new GpuDevice(1, "AMD Radeon 780M Graphics", "AMD", 0x1002, 512, IsDiscrete: false, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(12, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: false);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("auto", info);

        Assert.Equal("vulkan", backend);
        Assert.Equal(0, deviceIdx);
        Assert.Contains("RX 7900 GRE", summary);
    }

    [Fact]
    public void ResolveBackend_Auto_NvidiaUsesCudaWhenAvailable()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "NVIDIA GeForce RTX 4070", "NVIDIA", 0x10DE, 12288, IsDiscrete: true, HasVulkan: true, HasCuda: true)
        };
        var info = new HardwareInfo(16, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: true);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("auto", info);

        Assert.Equal("cuda", backend);
        Assert.Equal(0, deviceIdx);
        Assert.Contains("RTX 4070", summary);
    }

    [Fact]
    public void ResolveBackend_Auto_UsesIntegratedWhenNoDiscrete()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "Intel(R) Iris(R) Xe Graphics", "Intel", 0x8086, 128, IsDiscrete: false, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(8, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: false);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("auto", info);

        Assert.Equal("vulkan", backend);
        Assert.Equal(0, deviceIdx);
        Assert.Contains("Iris(R) Xe", summary);
    }

    [Fact]
    public void ResolveBackend_Auto_FallsBackToCpuWhenNoGpu()
    {
        var info = new HardwareInfo(4, "X64", Array.Empty<GpuDevice>(), SystemVulkanAvailable: false, SystemCudaAvailable: false);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("auto", info);

        Assert.Equal("cpu", backend);
        Assert.Equal(-1, deviceIdx);
        Assert.Contains("CPU", summary);
    }

    [Fact]
    public void ResolveBackend_GpuInt_SelectsIntegratedGpu()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "NVIDIA GeForce RTX 3060", "NVIDIA", 0x10DE, 6144, IsDiscrete: true, HasVulkan: true, HasCuda: true),
            new GpuDevice(1, "Intel UHD Graphics", "Intel", 0x8086, 128, IsDiscrete: false, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(12, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: true);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("gpu_int", info);

        Assert.Equal("vulkan", backend);
        Assert.Equal(1, deviceIdx);
        Assert.Contains("Intel UHD", summary);
    }

    [Fact]
    public void ResolveBackend_GpuExt_FallsBackSafelyWhenNoDiscrete()
    {
        var dummyGpus = new List<GpuDevice>
        {
            new GpuDevice(0, "Intel Iris Xe", "Intel", 0x8086, 128, IsDiscrete: false, HasVulkan: true, HasCuda: false)
        };
        var info = new HardwareInfo(8, "X64", dummyGpus, SystemVulkanAvailable: true, SystemCudaAvailable: false);

        var (backend, deviceIdx, summary) = HardwareDetector.ResolveBackend("gpu_ext", info);

        // Safely falls back to available integrated GPU or CPU
        Assert.Equal("vulkan", backend);
        Assert.Equal(0, deviceIdx);
        Assert.Contains("Iris Xe", summary);
    }
}
