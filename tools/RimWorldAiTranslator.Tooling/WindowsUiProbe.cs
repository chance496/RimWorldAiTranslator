using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RimWorldAiTranslator.Tooling;

internal static class WindowsUiProbe
{
    private const int IdOk = 1;
    private const int IdCancel = 2;
    private const uint WmNull = 0x0000;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;
    private const uint WmClose = 0x0010;
    private const uint WmUser = 0x0400;
    private const uint DmGetDefId = WmUser;
    private const uint BmClick = 0x00F5;
    private const uint DcHasDefId = 0x534B;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint MessageTimeoutMilliseconds = 1_000;
    private const int MaximumWindowTextCharacters = 4_096;
    private const string DialogClassName = "#32770";
    private const string ButtonClassName = "Button";
    private const string StaticClassName = "Static";
    private const string WinFormsWindowClassPrefix = "WindowsForms10.Window.";

    internal static bool HasVisibleWindow(int processId) => EnumerateTopLevelWindows(processId).Count > 0;

    internal static bool TryFindMainWindow(
        int processId,
        string expectedTitle,
        out IntPtr window)
    {
        var candidates = EnumerateTopLevelWindows(processId)
            .Where(candidate => candidate.Enabled
                                && candidate.Title.Equals(expectedTitle, StringComparison.Ordinal)
                                && candidate.ClassName.StartsWith(
                                    WinFormsWindowClassPrefix,
                                    StringComparison.Ordinal)
                                && candidate.Width >= 800
                                && candidate.Height >= 500)
            .ToArray();
        if (candidates.Length > 1)
            throw new InvalidDataException("The packaged application exposed multiple candidate MainForm windows.");
        window = candidates.Length == 1 ? candidates[0].Handle : IntPtr.Zero;
        return window != IntPtr.Zero;
    }

    internal static void AssertMainWindowResponsive(
        int processId,
        IntPtr window,
        string expectedTitle)
    {
        var current = RequireOwnedWindow(processId, window);
        if (!current.Visible
            || !current.Enabled
            || !current.Title.Equals(expectedTitle, StringComparison.Ordinal)
            || !current.ClassName.StartsWith(WinFormsWindowClassPrefix, StringComparison.Ordinal)
            || current.Width < 800
            || current.Height < 500)
        {
            throw new InvalidDataException("The packaged application MainForm changed before its responsiveness check.");
        }
        SendBounded(window, WmNull, IntPtr.Zero, IntPtr.Zero, "MainForm responsiveness check");
    }

    internal static long RequestNormalClose(
        int processId,
        IntPtr window,
        string expectedTitle)
    {
        AssertMainWindowResponsive(processId, window, expectedTitle);
        var requestedAt = Stopwatch.GetTimestamp();
        if (!PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not post a normal close to the packaged MainForm.");
        return requestedAt;
    }

    internal static DuplicateDialog? InspectDuplicateDialog(
        int processId,
        string expectedTitle,
        string expectedBody)
    {
        var dialogs = EnumerateTopLevelWindows(processId)
            .Where(candidate => candidate.ClassName.Equals(DialogClassName, StringComparison.Ordinal))
            .ToArray();
        if (dialogs.Length == 0) return null;
        if (dialogs.Length != 1)
            throw new InvalidDataException("The duplicate process exposed multiple native dialogs.");

        var dialog = dialogs[0];
        if (!dialog.Visible
            || !dialog.Enabled
            || !dialog.Title.Equals(expectedTitle, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The duplicate process exposed an unexpected dialog title or state.");
        }
        SendBounded(dialog.Handle, WmNull, IntPtr.Zero, IntPtr.Zero, "duplicate dialog responsiveness check");

        var children = EnumerateChildWindows(processId, dialog.Handle);
        var normalizedExpectedBody = NormalizeBody(expectedBody);
        var matchingBodies = children
            .Where(child => child.ClassName.Equals(StaticClassName, StringComparison.Ordinal))
            .Select(child => NormalizeBody(child.Title))
            .Count(text => text.Equals(normalizedExpectedBody, StringComparison.Ordinal));
        if (matchingBodies != 1)
            throw new InvalidDataException("The duplicate-instance dialog body did not exactly match its contract.");

        var buttons = children
            .Where(child => child.ClassName.Equals(ButtonClassName, StringComparison.Ordinal)
                            && child.Visible
                            && child.Enabled)
            .ToArray();
        if (buttons.Length != 1)
            throw new InvalidDataException("The duplicate-instance dialog did not expose exactly one enabled confirmation button.");
        var button = RequireOwnedWindow(processId, buttons[0].Handle);
        var confirmationControlId = GetDlgCtrlID(button.Handle);
        if (!button.Visible
            || !button.Enabled
            || !button.ClassName.Equals(ButtonClassName, StringComparison.Ordinal)
            || confirmationControlId is not (IdOk or IdCancel)
            || GetDlgItem(dialog.Handle, confirmationControlId) != button.Handle)
        {
            throw new InvalidDataException("The duplicate-instance confirmation control was not a supported standard button.");
        }

        var defaultResult = SendBounded(
            dialog.Handle,
            DmGetDefId,
            IntPtr.Zero,
            IntPtr.Zero,
            "duplicate dialog default-button query");
        var packedDefault = defaultResult.ToUInt64();
        var defaultId = (ushort)(packedDefault & 0xffff);
        var hasDefaultMarker = (ushort)((packedDefault >> 16) & 0xffff);
        var defaultMapsDirectly = defaultId == confirmationControlId
                                  && GetDlgItem(dialog.Handle, defaultId) == button.Handle;
        var singleButtonIdAlias = defaultId == IdOk
                                  && confirmationControlId == IdCancel
                                  && GetDlgItem(dialog.Handle, IdOk) == IntPtr.Zero;
        if (hasDefaultMarker != DcHasDefId || (!defaultMapsDirectly && !singleButtonIdAlias))
            throw new InvalidDataException("The duplicate-instance confirmation button was not the default button.");

        return new DuplicateDialog(
            dialog.Handle,
            button.Handle,
            confirmationControlId,
            defaultId,
            TitleMatched: true,
            BodyMatched: true,
            ConfirmationButtonVisibleAndEnabled: true,
            DefaultButtonIsConfirmation: true);
    }

    internal static void ClickDuplicateDialogConfirmation(
        int processId,
        DuplicateDialog observed,
        string expectedTitle,
        string expectedBody)
    {
        var current = InspectDuplicateDialog(processId, expectedTitle, expectedBody)
            ?? throw new InvalidDataException("The duplicate-instance dialog disappeared before confirmation.");
        if (current.DialogHandle != observed.DialogHandle
            || current.ConfirmationButtonHandle != observed.ConfirmationButtonHandle
            || current.ConfirmationButtonControlId != observed.ConfirmationButtonControlId
            || current.ReportedDefaultControlId != observed.ReportedDefaultControlId)
            throw new InvalidDataException("The duplicate-instance dialog identity changed before confirmation.");
        _ = SendBounded(
            current.ConfirmationButtonHandle,
            BmClick,
            IntPtr.Zero,
            IntPtr.Zero,
            "duplicate dialog confirmation click");
    }

    private static List<WindowSnapshot> EnumerateTopLevelWindows(int processId)
    {
        var result = new List<WindowSnapshot>();
        Exception? callbackFailure = null;
        EnumWindowsCallback callback = (window, _) =>
        {
            try
            {
                if (GetOwnerProcessId(window) == processId && IsWindowVisible(window))
                    result.Add(Snapshot(window));
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                                                and not StackOverflowException)
            {
                if (!IsWindow(window)) return true;
                callbackFailure = exception;
                return false;
            }
        };
        var completed = EnumWindows(callback, IntPtr.Zero);
        if (callbackFailure is not null) throw callbackFailure;
        if (!completed)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate packaged application windows.");
        GC.KeepAlive(callback);
        return result;
    }

    private static List<WindowSnapshot> EnumerateChildWindows(int processId, IntPtr parent)
    {
        var result = new List<WindowSnapshot>();
        Exception? callbackFailure = null;
        EnumWindowsCallback callback = (window, _) =>
        {
            try
            {
                if (GetOwnerProcessId(window) == processId)
                    result.Add(Snapshot(window, readCrossProcessControlText: true));
                return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                                                and not StackOverflowException)
            {
                if (!IsWindow(window)) return true;
                callbackFailure = exception;
                return false;
            }
        };
        var completed = EnumChildWindows(parent, callback, IntPtr.Zero);
        if (callbackFailure is not null) throw callbackFailure;
        if (!completed)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate duplicate-dialog controls.");
        GC.KeepAlive(callback);
        return result;
    }

    private static WindowSnapshot RequireOwnedWindow(int processId, IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window) || GetOwnerProcessId(window) != processId)
            throw new InvalidDataException("A probed window is no longer owned by the expected process.");
        return Snapshot(window);
    }

    private static WindowSnapshot Snapshot(
        IntPtr window,
        bool readCrossProcessControlText = false)
    {
        if (!GetWindowRect(window, out var bounds))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read a packaged application window boundary.");
        return new WindowSnapshot(
            window,
            GetClassName(window),
            readCrossProcessControlText ? GetControlText(window) : GetWindowText(window),
            IsWindowVisible(window),
            IsWindowEnabled(window),
            Math.Max(0, bounds.Right - bounds.Left),
            Math.Max(0, bounds.Bottom - bounds.Top));
    }

    private static int GetOwnerProcessId(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return checked((int)processId);
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        if (length <= 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read a packaged application window class.");
        return new string(buffer, 0, length);
    }

    private static string GetWindowText(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length < 0 || length > MaximumWindowTextCharacters)
            throw new InvalidDataException("A packaged application window caption exceeded its bounded size.");
        if (length == 0) return string.Empty;
        var buffer = new char[length + 1];
        var copied = GetWindowText(window, buffer, buffer.Length);
        var finalLength = GetWindowTextLength(window);
        if (copied != length || finalLength != length)
            throw new InvalidDataException("A packaged application window caption changed during inspection.");
        return new string(buffer, 0, copied);
    }

    private static string GetControlText(IntPtr window)
    {
        var length = BoundedTextLength(SendBounded(
            window,
            WmGetTextLength,
            IntPtr.Zero,
            IntPtr.Zero,
            "cross-process control-text length query"));
        if (length == 0) return string.Empty;

        var buffer = new char[length + 1];
        if (SendMessageTimeoutText(
                window,
                WmGetText,
                (IntPtr)buffer.Length,
                buffer,
                SmtoBlock | SmtoAbortIfHung,
                MessageTimeoutMilliseconds,
                out var copiedResult) == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The cross-process control-text query timed out or failed.");
        }

        var copied = BoundedTextLength(copiedResult);
        var finalLength = BoundedTextLength(SendBounded(
            window,
            WmGetTextLength,
            IntPtr.Zero,
            IntPtr.Zero,
            "cross-process control-text revalidation"));
        if (copied != length || finalLength != length)
            throw new InvalidDataException("A duplicate-dialog control changed during exact text inspection.");
        return new string(buffer, 0, copied);
    }

    private static int BoundedTextLength(UIntPtr value)
    {
        var length = value.ToUInt64();
        if (length > MaximumWindowTextCharacters)
            throw new InvalidDataException("A duplicate-dialog control exceeded its bounded text size.");
        return checked((int)length);
    }

    private static UIntPtr SendBounded(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        string operation)
    {
        if (SendMessageTimeout(
                window,
                message,
                wParam,
                lParam,
                SmtoBlock | SmtoAbortIfHung,
                MessageTimeoutMilliseconds,
                out var result) == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"The {operation} timed out or failed.");
        }
        return result;
    }

    private static string NormalizeBody(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal sealed record DuplicateDialog(
        IntPtr DialogHandle,
        IntPtr ConfirmationButtonHandle,
        int ConfirmationButtonControlId,
        int ReportedDefaultControlId,
        bool TitleMatched,
        bool BodyMatched,
        bool ConfirmationButtonVisibleAndEnabled,
        bool DefaultButtonIsConfirmation);

    private sealed record WindowSnapshot(
        IntPtr Handle,
        string ClassName,
        string Title,
        bool Visible,
        bool Enabled,
        int Width,
        int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

#pragma warning disable SYSLIB1054 // Classic user32 callbacks are required for PID-scoped native window inspection.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassName(IntPtr window, [Out] char[] className, int maximumCount);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maximumCount);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr window);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SendMessageTimeoutText(
        IntPtr window,
        uint message,
        IntPtr wParam,
        [Out] char[] text,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
#pragma warning restore SYSLIB1054
}
