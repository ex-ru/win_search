using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WAI.Core;

/// <summary>
/// Manages a Windows Job Object configured with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
/// Any child processes (wai-embed.exe, ffmpeg, tesseract, etc.) assigned to this Job Object
/// will be unconditionally terminated by the Windows kernel if the main process exits or crashes.
/// Also provides an explicit TerminateAll() method before application shutdown or updates.
/// </summary>
public static class JobObjectManager
{
    private static readonly object _sync = new();
    private static IntPtr _jobHandle = IntPtr.Zero;
    private static bool _initialized = false;

    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static bool Initialize()
    {
        if (!IsSupported) return false;

        lock (_sync)
        {
            if (_initialized && _jobHandle != IntPtr.Zero)
                return true;

            try
            {
                _jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero)
                {
                    return false;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr pInfo = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, pInfo, false);
                    if (!SetInformationJobObject(
                        _jobHandle,
                        JobObjectInfoClass.JobObjectExtendedLimitInformation,
                        pInfo,
                        (uint)length))
                    {
                        CloseHandle(_jobHandle);
                        _jobHandle = IntPtr.Zero;
                        return false;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pInfo);
                }

                _initialized = true;
                return true;
            }
            catch
            {
                if (_jobHandle != IntPtr.Zero)
                {
                    CloseHandle(_jobHandle);
                    _jobHandle = IntPtr.Zero;
                }
                return false;
            }
        }
    }

    /// <summary>
    /// Assigns a running process to the Job Object. If the Job Object is not yet initialized,
    /// it will be initialized automatically. Optionally applies Windows 11 EcoQoS (Efficiency Mode)
    /// and BelowNormal priority for background worker processes.
    /// </summary>
    public static bool AssignProcess(Process? process, bool isBackground = false)
    {
        if (process is null || !IsSupported) return false;

        try
        {
            if (process.HasExited) return false;
            bool assigned = AssignProcess(process.Handle, isBackground);
            if (assigned && isBackground)
            {
                EnableEfficiencyMode(process);
            }
            return assigned;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Assigns a native process handle to the Job Object.
    /// </summary>
    public static bool AssignProcess(IntPtr processHandle, bool isBackground = false)
    {
        if (!IsSupported || processHandle == IntPtr.Zero) return false;

        lock (_sync)
        {
            if (!_initialized || _jobHandle == IntPtr.Zero)
            {
                if (!Initialize()) return false;
            }

            try
            {
                bool ok = AssignProcessToJobObject(_jobHandle, processHandle);
                if (ok && isBackground)
                {
                    EnableEfficiencyMode(processHandle);
                }
                return ok;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Enables Windows 11 EcoQoS (Efficiency Mode) and sets BelowNormal process priority.
    /// Windows will schedule the process on efficient CPU cores and throttle execution speed to reduce power and noise.
    /// </summary>
    public static bool EnableEfficiencyMode(Process? process)
    {
        if (process is null || !IsSupported) return false;
        try
        {
            if (process.HasExited) return false;
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch { }
            return EnableEfficiencyMode(process.Handle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables Windows 11 EcoQoS (Efficiency Mode) on a native process handle.
    /// </summary>
    public static bool EnableEfficiencyMode(IntPtr processHandle)
    {
        if (!IsSupported || processHandle == IntPtr.Zero) return false;

        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED
            };
            return SetProcessInformation(
                processHandle,
                ProcessPowerThrottlingClass,
                ref state,
                (uint)Marshal.SizeOf(state));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Immediately terminates all processes assigned to the Job Object.
    /// </summary>
    public static bool TerminateAll(uint exitCode = 0)
    {
        if (!IsSupported) return false;

        lock (_sync)
        {
            if (_jobHandle == IntPtr.Zero) return true;

            try
            {
                return TerminateJobObject(_jobHandle, exitCode);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Closes the job object handle. The Windows kernel will automatically terminate
    /// any remaining child processes assigned to the job object due to KILL_ON_JOB_CLOSE.
    /// </summary>
    public static void Dispose()
    {
        lock (_sync)
        {
            if (_jobHandle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(_jobHandle);
                }
                catch { }
                _jobHandle = IntPtr.Zero;
            }
            _initialized = false;
        }
    }

    #region Win32 P/Invoke

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private enum JobObjectInfoClass
    {
        JobObjectExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryLimit;
        public UIntPtr PeakJobMemoryLimit;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoClass JobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const int ProcessPowerThrottlingClass = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);

    #endregion
}
