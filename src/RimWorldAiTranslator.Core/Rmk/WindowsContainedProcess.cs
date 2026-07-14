using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RimWorldAiTranslator.Core.Rmk;

/// <summary>
/// Starts one exact Windows executable suspended, assigns it to a kill-on-close job,
/// and only then lets its primary thread run. Only the three redirected standard
/// stream handles are inherited by the child.
/// </summary>
internal sealed class WindowsContainedProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private SafeFileHandle? processHandle;
    private SafeFileHandle? jobHandle;
    private int? exitCode;
    private bool disposed;

    private WindowsContainedProcess(
        SafeFileHandle processHandle,
        SafeFileHandle jobHandle,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        this.processHandle = processHandle;
        this.jobHandle = jobHandle;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public bool HasExited
    {
        get
        {
            if (processHandle is null || processHandle.IsClosed || processHandle.IsInvalid) return true;
            var result = WaitForSingleObject(processHandle, 0);
            return result switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                _ => throw NativeIOException("RMK Builder process state could not be queried.")
            };
        }
    }

    public int ExitCode
    {
        get
        {
            if (exitCode is { } value) return value;
            if (!HasExited) throw new InvalidOperationException("RMK Builder has not exited.");
            if (processHandle is null || !GetExitCodeProcess(processHandle, out var nativeExitCode))
                throw NativeIOException("RMK Builder exit code could not be read.");
            exitCode = unchecked((int)nativeExitCode);
            return exitCode.Value;
        }
    }

    public static WindowsContainedProcess Start(
        string executablePath,
        string workingDirectory,
        Func<string, bool> removeEnvironmentVariable,
        Action? beforeJobAssignment = null,
        IReadOnlyDictionary<string, string>? additionalEnvironment = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("RMK Builder process containment requires Windows.");
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(removeEnvironmentVariable);

        SafeFileHandle? job = null;
        SafeFileHandle? process = null;
        SafeFileHandle? thread = null;
        SafeFileHandle? childInput = null;
        SafeFileHandle? parentInput = null;
        SafeFileHandle? parentOutput = null;
        SafeFileHandle? childOutput = null;
        SafeFileHandle? parentError = null;
        SafeFileHandle? childError = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        FileStream? errorStream = null;
        StreamWriter? inputWriter = null;
        StreamReader? outputReader = null;
        StreamReader? errorReader = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandles = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr unownedProcessHandle = IntPtr.Zero;
        IntPtr unownedThreadHandle = IntPtr.Zero;
        var processCreated = false;
        var attributeListInitialized = false;

        try
        {
            job = CreateKillOnCloseJob();
            CreateAnonymousPipe(out childInput, out parentInput, parentEndIsRead: false);
            CreateAnonymousPipe(out parentOutput, out childOutput, parentEndIsRead: true);
            CreateAnonymousPipe(out parentError, out childError, parentEndIsRead: true);

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            var sizingError = Marshal.GetLastWin32Error();
            if (attributeListSize == 0 || sizingError != 122)
                throw NativeIOException("RMK Builder inherited-handle policy could not be sized.", sizingError);
            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw NativeIOException("RMK Builder inherited-handle policy could not be initialized.");
            attributeListInitialized = true;

            var handles = new[]
            {
                childInput.DangerousGetHandle(),
                childOutput.DangerousGetHandle(),
                childError.DangerousGetHandle()
            };
            inheritedHandles = Marshal.AllocHGlobal(checked(IntPtr.Size * handles.Length));
            Marshal.Copy(handles, 0, inheritedHandles, handles.Length);
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (nuint)ProcThreadAttributeHandleList,
                    inheritedHandles,
                    checked((nuint)(IntPtr.Size * handles.Length)),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw NativeIOException("RMK Builder inherited-handle allowlist could not be applied.");
            }

            environmentBlock = CreateEnvironmentBlock(
                removeEnvironmentVariable,
                additionalEnvironment);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                    Flags = StartfUseStdHandles,
                    StandardInput = childInput.DangerousGetHandle(),
                    StandardOutput = childOutput.DangerousGetHandle(),
                    StandardError = childError.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            var commandLine = ($"\"{executablePath}\"\0").ToCharArray();
            if (!CreateProcess(
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow | ExtendedStartupInfoPresent,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out var processInformation))
            {
                throw NativeIOException("RMK Builder could not be created in a suspended state.");
            }

            processCreated = true;
            unownedProcessHandle = processInformation.Process;
            unownedThreadHandle = processInformation.Thread;
            process = new SafeFileHandle(unownedProcessHandle, ownsHandle: true);
            unownedProcessHandle = IntPtr.Zero;
            thread = new SafeFileHandle(unownedThreadHandle, ownsHandle: true);
            unownedThreadHandle = IntPtr.Zero;
            childInput.Dispose();
            childInput = null;
            childOutput.Dispose();
            childOutput = null;
            childError.Dispose();
            childError = null;

            beforeJobAssignment?.Invoke();
            if (!AssignProcessToJobObject(job, process))
                throw NativeIOException("RMK Builder could not be assigned to process-tree containment.");

            inputStream = new FileStream(parentInput, FileAccess.Write, 4096, isAsync: false);
            parentInput = null;
            outputStream = new FileStream(parentOutput, FileAccess.Read, 4096, isAsync: false);
            parentOutput = null;
            errorStream = new FileStream(parentError, FileAccess.Read, 4096, isAsync: false);
            parentError = null;
            inputWriter = new StreamWriter(inputStream, new UTF8Encoding(false), 1024, leaveOpen: false)
            {
                AutoFlush = true
            };
            inputStream = null;
            outputReader = new StreamReader(outputStream, Encoding.UTF8, true, 4096, leaveOpen: false);
            outputStream = null;
            errorReader = new StreamReader(errorStream, Encoding.UTF8, true, 4096, leaveOpen: false);
            errorStream = null;

            if (ResumeThread(thread) == Infinite)
                throw NativeIOException("RMK Builder primary thread could not be resumed.");
            thread.Dispose();
            thread = null;

            var result = new WindowsContainedProcess(process, job, inputWriter, outputReader, errorReader);
            process = null;
            job = null;
            inputWriter = null;
            outputReader = null;
            errorReader = null;
            return result;
        }
        catch
        {
            if (processCreated && process is not null && !process.IsInvalid && !process.IsClosed)
            {
                job?.Dispose();
                job = null;
                if (WaitForSingleObject(process, 0) == WaitTimeout)
                {
                    if (!TerminateProcess(process, 1))
                        throw NativeIOException("A suspended RMK Builder could not be terminated after launch setup failed.");
                }
                var waitResult = WaitForSingleObject(process, 10_000);
                if (waitResult != WaitObject0)
                    throw waitResult == WaitTimeout
                        ? new IOException("A suspended RMK Builder did not terminate after launch setup failed.")
                        : NativeIOException("A suspended RMK Builder termination could not be verified.");
            }
            else if (processCreated && unownedProcessHandle != IntPtr.Zero)
            {
                if (WaitForSingleObjectRaw(unownedProcessHandle, 0) == WaitTimeout
                    && !TerminateProcessRaw(unownedProcessHandle, 1))
                {
                    throw NativeIOException("An unowned suspended RMK Builder handle could not be terminated after launch setup failed.");
                }
                var waitResult = WaitForSingleObjectRaw(unownedProcessHandle, 10_000);
                if (waitResult != WaitObject0)
                    throw waitResult == WaitTimeout
                        ? new IOException("An unowned suspended RMK Builder did not terminate after launch setup failed.")
                        : NativeIOException("An unowned suspended RMK Builder termination could not be verified.");
            }
            throw;
        }
        finally
        {
            thread?.Dispose();
            process?.Dispose();
            job?.Dispose();
            childInput?.Dispose();
            parentInput?.Dispose();
            parentOutput?.Dispose();
            childOutput?.Dispose();
            parentError?.Dispose();
            childError?.Dispose();
            inputWriter?.Dispose();
            outputReader?.Dispose();
            errorReader?.Dispose();
            inputStream?.Dispose();
            outputStream?.Dispose();
            errorStream?.Dispose();
            if (attributeList != IntPtr.Zero)
            {
                if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            if (unownedThreadHandle != IntPtr.Zero) _ = CloseHandle(unownedThreadHandle);
            if (unownedProcessHandle != IntPtr.Zero) _ = CloseHandle(unownedProcessHandle);
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        while (!HasExited)
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        _ = ExitCode;
    }

    public void CloseContainment()
    {
        jobHandle?.Dispose();
        jobHandle = null;
    }

    public void TerminateTree()
    {
        CloseContainment();
        if (processHandle is not null
            && !processHandle.IsClosed
            && !processHandle.IsInvalid
            && WaitForSingleObject(processHandle, 0) == WaitTimeout)
        {
            if (!TerminateProcess(processHandle, 1))
                throw NativeIOException("RMK Builder could not be terminated after containment closed.");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Exception? terminationError = null;
        try
        {
            TerminateTree();
            if (processHandle is not null && !processHandle.IsClosed && !processHandle.IsInvalid)
            {
                var waitResult = WaitForSingleObject(processHandle, 10_000);
                if (waitResult != WaitObject0)
                    terminationError = waitResult == WaitTimeout
                        ? new IOException("RMK Builder did not terminate while containment was being disposed.")
                        : NativeIOException("RMK Builder termination could not be verified while containment was being disposed.");
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            terminationError = exception;
        }
        finally
        {
            try { StandardInput.Dispose(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"RMK Builder stdin cleanup ended with {exception.GetType().Name}.");
            }
            try { StandardOutput.Dispose(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"RMK Builder stdout cleanup ended with {exception.GetType().Name}.");
            }
            try { StandardError.Dispose(); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                System.Diagnostics.Debug.WriteLine($"RMK Builder stderr cleanup ended with {exception.GetType().Name}.");
            }
            processHandle?.Dispose();
            processHandle = null;
            GC.SuppressFinalize(this);
        }

        if (terminationError is not null)
            throw new IOException("RMK Builder containment disposal could not prove process termination.", terminationError);
    }

    private static void CreateAnonymousPipe(
        out SafeFileHandle parentReadOrChildRead,
        out SafeFileHandle childWriteOrParentWrite,
        bool parentEndIsRead)
    {
        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            InheritHandle = 1
        };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
        {
            var exception = NativeIOException("RMK Builder standard-stream pipe could not be created.");
            read.Dispose();
            write.Dispose();
            throw exception;
        }

        var parent = parentEndIsRead ? read : write;
        var child = parentEndIsRead ? write : read;
        if (!SetHandleInformation(parent, HandleFlagInherit, 0))
        {
            var exception = NativeIOException("RMK Builder parent pipe handle could not be isolated.");
            read.Dispose();
            write.Dispose();
            throw exception;
        }

        parentReadOrChildRead = read;
        childWriteOrParentWrite = write;
    }

    private static SafeFileHandle CreateKillOnCloseJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            var exception = NativeIOException("RMK Builder containment could not be created.");
            handle.Dispose();
            throw exception;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (!SetInformationJobObject(
                handle,
                9,
                ref limits,
                checked((uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>())))
        {
            var exception = NativeIOException("RMK Builder containment could not be configured.");
            handle.Dispose();
            throw exception;
        }
        return handle;
    }

    private static IntPtr CreateEnvironmentBlock(
        Func<string, bool> removeEnvironmentVariable,
        IReadOnlyDictionary<string, string>? additionalEnvironment)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name
                || entry.Value is not string value
                || name.Length == 0
                || name.Contains('\0')
                || value.Contains('\0')
                || removeEnvironmentVariable(name))
            {
                continue;
            }
            variables[name] = value;
        }

        if (additionalEnvironment is not null)
        {
            if (additionalEnvironment.Count > 16)
                throw new InvalidOperationException("RMK Builder additional environment exceeds its entry limit.");
            foreach (var (name, value) in additionalEnvironment)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || name.Length > 256
                    || name.Contains('=')
                    || name.Contains('\0')
                    || value is null
                    || value.Length > 4096
                    || value.Contains('\0')
                    || removeEnvironmentVariable(name))
                {
                    throw new InvalidOperationException("RMK Builder additional environment is invalid or sensitive.");
                }
                variables[name] = value;
            }
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
            block.Append(name).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static IOException NativeIOException(string message, int? error = null) =>
        new(message, new Win32Exception(error ?? Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
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
        public ushort Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint ResumeThread(SafeHandle thread);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeHandle process, out uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeHandle process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcessRaw(IntPtr process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObjectRaw(IntPtr handle, uint milliseconds);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeHandle job, SafeHandle process);
}
