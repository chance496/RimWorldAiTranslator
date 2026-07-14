using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RimWorldAiTranslator.Tooling;

internal sealed class WindowsKillOnCloseJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private SafeFileHandle? handle;
    private bool disposed;

    private WindowsKillOnCloseJob(SafeFileHandle handle)
    {
        this.handle = handle;
    }

    public static WindowsKillOnCloseJob Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Job Object containment requires Windows.");

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not create the package-smoke Job Object.");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
                ActiveProcessLimit = 1
            }
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref limits,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not enable kill-on-close for the package-smoke Job Object.");
        }

        return new WindowsKillOnCloseJob(handle);
    }

    public void Assign(SafeProcessHandle process)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (process.IsInvalid || process.IsClosed)
            throw new InvalidOperationException("The generated application handle is unavailable for Job Object assignment.");
        var job = handle ?? throw new ObjectDisposedException(nameof(WindowsKillOnCloseJob));
        if (!AssignProcessToJobObject(job, process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not contain the generated application in its Job Object.");
    }

    public JobAccounting GetAccounting()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var job = handle ?? throw new ObjectDisposedException(nameof(WindowsKillOnCloseJob));
        var accounting = new JobObjectBasicAccountingInformation();
        if (!QueryBasicAccounting(
                job,
                JobObjectInformationClass.BasicAccountingInformation,
                ref accounting,
                checked((uint)Marshal.SizeOf<JobObjectBasicAccountingInformation>()),
                out var returnedLength))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query package-smoke Job Object accounting.");
        }
        if (returnedLength != Marshal.SizeOf<JobObjectBasicAccountingInformation>())
            throw new InvalidDataException($"Job Object accounting returned an unexpected size: {returnedLength} bytes.");
        return new JobAccounting(accounting.TotalProcesses, accounting.ActiveProcesses, accounting.TotalTerminatedProcesses);
    }

    internal uint GetActiveProcessLimit()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var job = handle ?? throw new ObjectDisposedException(nameof(WindowsKillOnCloseJob));
        var limits = new JobObjectExtendedLimitInformation();
        if (!QueryExtendedLimits(
                job,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref limits,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()),
                out var returnedLength))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query package-smoke Job Object limits.");
        }
        if (returnedLength != Marshal.SizeOf<JobObjectExtendedLimitInformation>()
            || (limits.BasicLimitInformation.LimitFlags & JobObjectLimitActiveProcess) == 0)
        {
            throw new InvalidDataException("Job Object active-process containment was not retained.");
        }
        return limits.BasicLimitInformation.ActiveProcessLimit;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        handle?.Dispose();
        handle = null;
    }

    private enum JobObjectInformationClass
    {
        BasicAccountingInformation = 1,
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

#pragma warning disable SYSLIB1054 // SafeHandle-aware classic interop is used for this Windows-only containment boundary.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryBasicAccounting(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectBasicAccountingInformation information,
        uint informationLength,
        out uint returnLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryExtendedLimits(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength,
        out uint returnLength);
#pragma warning restore SYSLIB1054

    public sealed record JobAccounting(uint TotalProcesses, uint ActiveProcesses, uint TotalTerminatedProcesses);
}
