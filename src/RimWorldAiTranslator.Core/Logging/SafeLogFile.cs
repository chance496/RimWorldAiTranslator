using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RimWorldAiTranslator.Core.Safety;

namespace RimWorldAiTranslator.Core.Logging;

public static class SafeLogFile
{
    public static TextWriter OpenUtf8AppendWriter(string path, int bufferSize = 32 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        var stream = OpenAppendStream(path, bufferSize);
        try
        {
            return new StreamWriter(stream, new UTF8Encoding(false), bufferSize);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static void AppendUtf8Line(string path, string line, bool flushToDisk)
    {
        using var stream = OpenAppendStream(path, 4_096);
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
            bufferSize: 4_096,
            leaveOpen: true);
        writer.WriteLine(line);
        writer.Flush();
        if (flushToDisk) stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenAppendStream(string path, int bufferSize)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Safe persistent log append requires Windows.");
        var fullPath = PathSafety.Normalize(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("A persistent log path has no parent directory.");
        EnsureLocalPath(directory);
        using var creationBoundary =
            PathSafety.AcquireTrustedDirectoryCreationBoundary(directory);
        PathSafety.EnsureNoReparsePointsToVolumeRoot(directory);
        var canonicalDirectory = PathSafety.GetCanonicalExistingDirectory(directory);
        if (!canonicalDirectory.Equals(PathSafety.Normalize(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The persistent log directory is redirected.");
        using var directoryLease =
            PathSafety.WindowsPathHandle.OpenDirectoryWithoutDeleteSharing(canonicalDirectory);
        var leasedDirectory = PathSafety.Normalize(
            PathSafety.WindowsPathHandle.GetFinalPath(directoryLease));
        var leasedDirectoryIdentity = PathSafety.WindowsPathHandle.GetIdentity(directoryLease);
        if (!leasedDirectory.Equals(canonicalDirectory, StringComparison.OrdinalIgnoreCase)
            || (leasedDirectoryIdentity.FileAttributes
                & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
               != FileAttributes.Directory)
        {
            throw new InvalidDataException("The persistent log directory changed before append.");
        }

        SafeFileHandle? handle = CreateFile(
            ToExtendedPath(fullPath),
            FileAppendData | FileReadAttributes,
            FileShareRead,
            IntPtr.Zero,
            OpenAlways,
            FileAttributeNormal | FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "The persistent log file could not be opened safely.",
                new Win32Exception(error));
        }

        try
        {
            var identity = PathSafety.WindowsPathHandle.GetIdentity(handle);
            if ((identity.FileAttributes
                 & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || identity.NumberOfLinks != 1
                || identity.FileIndex == 0)
            {
                throw new InvalidDataException(
                    "Persistent logging requires a regular, non-reparse, single-link file.");
            }
            var finalPath = PathSafety.Normalize(PathSafety.WindowsPathHandle.GetFinalPath(handle));
            if (!finalPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The persistent log file resolved outside its exact expected path.");

            var stream = new FileStream(handle, FileAccess.Write, bufferSize, isAsync: false);
            handle = null;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string GetCanonicalExistingDirectory(string directory)
    {
        PathSafety.EnsureNoReparsePointsToVolumeRoot(directory);
        return PathSafety.GetCanonicalExistingDirectory(directory);
    }

    private static void EnsureLocalPath(string path)
    {
        if (PathSafety.IsNetworkPath(path))
            throw new InvalidDataException("Persistent logs require a local filesystem path.");
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(volumeRoot)
            || new DriveInfo(volumeRoot).DriveType == DriveType.Network)
        {
            throw new InvalidDataException("Persistent logs require a local filesystem volume.");
        }
    }

    private static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + fullPath[2..]
            : @"\\?\" + fullPath;
    }

    private const uint FileAppendData = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
