using System.Diagnostics;
using System.Runtime.InteropServices;
using WAI.Core;
using Xunit;

namespace WAI.Tests;

// TerminateAll owns the process-wide job: running it beside WorkerClient tests kills their workers.
[CollectionDefinition("Job objects", DisableParallelization = true)]
public sealed class JobObjectCollection { }

[Collection("Job objects")]
public class JobObjectTests
{
    [Fact]
    public void IsSupported_ReturnsTrueOnWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.True(JobObjectManager.IsSupported);
        }
    }

    [Fact]
    public void Initialize_SucceedsOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        bool initialized = JobObjectManager.Initialize();
        Assert.True(initialized);
    }

    [Fact]
    public void AssignProcess_GracefullyHandlesNullOrExited()
    {
        Assert.False(JobObjectManager.AssignProcess((Process?)null));

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Process that exits immediately
        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process.WaitForExit(3000);
        Assert.True(process.HasExited);

        // Assigning already exited process should not throw and return false
        bool assigned = JobObjectManager.AssignProcess(process);
        Assert.False(assigned);
    }

    [Fact]
    public void TerminateAll_KillsAssignedChildProcesses()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        // Start a long-running process
        var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 30 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        try
        {
            Assert.False(process.HasExited);

            bool assigned = JobObjectManager.AssignProcess(process);
            Assert.True(assigned);

            // Terminate via JobObject
            bool terminated = JobObjectManager.TerminateAll(42);
            Assert.True(terminated);

            bool exited = process.WaitForExit(3000);
            Assert.True(exited);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
            }
        }
    }

    [Fact]
    public void AssignProcess_WithIsBackground_SetsBelowNormalPriority()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 10 > nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        try
        {
            bool assigned = JobObjectManager.AssignProcess(process, isBackground: true);
            Assert.True(assigned);
            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);

            bool ecoOk = JobObjectManager.EnableEfficiencyMode(process);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) Assert.True(ecoOk);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(); } catch { }
            }
        }
    }

    [Fact]
    public void EnableEfficiencyMode_GracefullyHandlesNullOrExited()
    {
        Assert.False(JobObjectManager.EnableEfficiencyMode((Process?)null));
        Assert.False(JobObjectManager.EnableEfficiencyMode(IntPtr.Zero));
    }
}
