using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RimWorldAiTranslator.Tooling;

internal static class WindowsProcessTree
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static IReadOnlyList<ProcessRecord> GetDescendants(Process parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Package smoke testing requires Windows.");
        if (parent.HasExited)
            throw new InvalidOperationException("The package-smoke parent exited before its process tree was inspected.");

        var parentCreationTime = GetCreationTime(parent.SafeHandle, $"package-smoke parent PID {parent.Id}");
        return FindDescendants(
            parent.Id,
            parentCreationTime,
            Snapshot(),
            GetCreationTime);
    }

    internal static IReadOnlyList<ProcessRecord> FindDescendantsForTesting(
        int parentProcessId,
        ulong parentCreationTime,
        IReadOnlyList<ProcessRecord> records,
        Func<ProcessRecord, ulong> creationTimeResolver) =>
        FindDescendants(parentProcessId, parentCreationTime, records, creationTimeResolver);

    private static List<ProcessRecord> FindDescendants(
        int parentProcessId,
        ulong parentCreationTime,
        IReadOnlyList<ProcessRecord> records,
        Func<ProcessRecord, ulong> creationTimeResolver)
    {
        var byParent = records
            .GroupBy(record => record.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var descendants = new List<ProcessRecord>();
        var pending = new Queue<(int ProcessId, ulong CreationTime)>();
        var visited = new HashSet<int> { parentProcessId };
        pending.Enqueue((parentProcessId, parentCreationTime));
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!byParent.TryGetValue(current.ProcessId, out var children)) continue;
            foreach (var child in children)
            {
                if (!visited.Add(child.ProcessId)) continue;

                // PROCESSENTRY32 retains the numeric parent PID after that parent exits.
                // If Windows later reuses the PID for this package process, an unrelated
                // older process can otherwise look like a child. Creation-time lookup
                // failures propagate so the release gate remains fail-closed.
                var childCreationTime = creationTimeResolver(child);
                if (childCreationTime < current.CreationTime)
                    continue;

                descendants.Add(child);
                pending.Enqueue((child.ProcessId, childCreationTime));
            }
        }
        return descendants;
    }

    private static ulong GetCreationTime(ProcessRecord record)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, checked((uint)record.ProcessId));
        if (process.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not pin candidate process {record.Name} ({record.ProcessId}) for creation-time validation.");
        return GetCreationTime(process, $"candidate process {record.Name} ({record.ProcessId})");
    }

    private static ulong GetCreationTime(SafeProcessHandle process, string label)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read the creation time of {label}.");
        return ((ulong)creation.HighDateTime << 32) | creation.LowDateTime;
    }

    private static List<ProcessRecord> Snapshot()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not snapshot running processes.");

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 18) return [];
                throw new Win32Exception(error, "Could not read the process snapshot.");
            }

            var result = new List<ProcessRecord>();
            while (true)
            {
                result.Add(new ProcessRecord(
                    checked((int)entry.ProcessId),
                    checked((int)entry.ParentProcessId),
                    entry.ExecutableFile ?? string.Empty));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                if (Process32Next(snapshot, ref entry)) continue;
                var error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error, "Could not continue reading the process snapshot.");
                break;
            }
            return result;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    internal sealed record ProcessRecord(int ProcessId, int ParentProcessId, string Name);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

#pragma warning disable SYSLIB1054 // Classic marshalling is required for PROCESSENTRY32's fixed ByValTStr buffer.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
