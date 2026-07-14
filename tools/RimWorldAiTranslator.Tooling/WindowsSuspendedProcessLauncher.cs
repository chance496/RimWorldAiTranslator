using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RimWorldAiTranslator.Tooling;

internal static class WindowsSuspendedProcessLauncher
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint WaitMilliseconds = 5_000;
    private const int MaximumEnvironmentCharacters = 32_767;

    public static Process Start(
        string executable,
        string workingDirectory,
        IEnumerable<KeyValuePair<string, string?>> environment,
        WindowsKillOnCloseJob containment)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Suspended process launch requires Windows.");

        using var environmentBlock = AllocateUnicodeBlock(BuildEnvironmentBlock(environment));
        using var commandLine = AllocateUnicodeBlock(QuoteCommandLineArgument(executable) + '\0');
        var startup = new StartupInfo { Size = checked((uint)Marshal.SizeOf<StartupInfo>()) };
        var native = default(ProcessInformation);
        SafeProcessHandle? processHandle = null;
        SafeThreadHandle? threadHandle = null;
        Process? managedProcess = null;
        try
        {
            if (!CreateProcess(
                    executable,
                    commandLine.DangerousGetHandle(),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateSuspended | CreateUnicodeEnvironment,
                    environmentBlock.DangerousGetHandle(),
                    workingDirectory,
                    ref startup,
                    out native))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the generated application in a suspended state.");
            }

            processHandle = new SafeProcessHandle(native.ProcessHandle, ownsHandle: true);
            native.ProcessHandle = IntPtr.Zero;
            threadHandle = new SafeThreadHandle(native.ThreadHandle, ownsHandle: true);
            native.ThreadHandle = IntPtr.Zero;

            containment.Assign(processHandle);

            // The original live process handle and suspended primary thread pin this PID while
            // Process obtains and caches its own handle. No later operation reopens a snapshot PID.
            managedProcess = Process.GetProcessById(checked((int)native.ProcessId));
            _ = managedProcess.SafeHandle;

            var previousSuspendCount = ResumeThread(threadHandle);
            if (previousSuspendCount == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resume the contained generated application.");
            if (previousSuspendCount != 1)
                throw new InvalidOperationException($"Generated application had an unexpected suspend count: {previousSuspendCount}.");

            var result = managedProcess;
            managedProcess = null;
            return result;
        }
        catch
        {
            managedProcess?.Dispose();
            containment.Dispose();
            if (processHandle is { IsInvalid: false, IsClosed: false })
            {
                _ = TerminateProcess(processHandle, 1);
                _ = WaitForSingleObject(processHandle, WaitMilliseconds);
            }
            throw;
        }
        finally
        {
            threadHandle?.Dispose();
            processHandle?.Dispose();
            if (native.ThreadHandle != IntPtr.Zero) _ = CloseHandle(native.ThreadHandle);
            if (native.ProcessHandle != IntPtr.Zero)
            {
                _ = TerminateProcess(native.ProcessHandle, 1);
                _ = CloseHandle(native.ProcessHandle);
            }
        }
    }

    private static string BuildEnvironmentBlock(IEnumerable<KeyValuePair<string, string?>> values)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            if (string.IsNullOrEmpty(pair.Key)
                || pair.Key.Contains('=')
                || pair.Key.Contains('\0')
                || pair.Value is null
                || pair.Value.Contains('\0'))
            {
                throw new InvalidDataException($"Child environment contains an invalid name or value: {pair.Key}");
            }
            if (!environment.TryAdd(pair.Key, pair.Value))
                throw new InvalidDataException($"Child environment contains a duplicate name: {pair.Key}");
        }

        var builder = new StringBuilder();
        foreach (var pair in environment
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }
        if (builder.Length == 0) builder.Append('\0');
        builder.Append('\0');
        if (builder.Length > MaximumEnvironmentCharacters)
            throw new InvalidDataException($"Child environment block exceeds {MaximumEnvironmentCharacters} UTF-16 characters.");
        return builder.ToString();
    }

    internal static string BuildEnvironmentBlockForSelfTest(IEnumerable<KeyValuePair<string, string?>> values) =>
        BuildEnvironmentBlock(values);

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Contains('\0') || value.Contains('"'))
            throw new InvalidDataException("Generated application path contains a character that cannot be quoted safely.");
        return $"\"{value}\"";
    }

    private static SafeHGlobalHandle AllocateUnicodeBlock(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return new SafeHGlobalHandle(pointer, ownsHandle: true);
        }
        catch
        {
            Marshal.FreeHGlobal(pointer);
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Bytes;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    private sealed class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeThreadHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeHGlobalHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeHGlobalHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

#pragma warning disable SYSLIB1054 // SafeHandle-aware classic interop is used for this Windows-only launch boundary.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        IntPtr commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeThreadHandle thread);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
