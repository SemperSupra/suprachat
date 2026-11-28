using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace SupraChat.Core;

internal sealed class WindowsInteractiveTerminal
{
    private const uint ExtendedStartup = 0x00080000;
    private const uint UnicodeEnvironment = 0x00000400;
    private const uint UseStandardHandles = 0x00000100;
    private static readonly IntPtr ConsoleStandardHandle = new(-1);
    private static readonly IntPtr PseudoConsoleAttribute = new(0x00020016);
    private readonly object _consoleGate = new();
    private readonly Channel<string> _input = Channel.CreateBounded<string>(128);
    private IntPtr _console;

    internal Task<int> Completion { get; }
    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    internal WindowsInteractiveTerminal(string executable, string arguments, string directory,
        IReadOnlyDictionary<string, string>? environment, int columns, int rows, Action<string> output)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Interactive terminals require Windows 10 build 17763 or later.");
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("The workspace startup directory is unavailable.");
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        using var consoleInput = inputRead;
        using var childInput = new SafeFileHandle(inputWrite.DangerousGetHandle(), ownsHandle: true);
        inputWrite.SetHandleAsInvalid();

        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        using var consoleOutput = outputWrite;
        using var childOutput = new SafeFileHandle(outputRead.DangerousGetHandle(), ownsHandle: true);
        outputRead.SetHandleAsInvalid();

        var result = CreatePseudoConsole(Size(columns, rows), consoleInput, consoleOutput, 0, out _console);
        Marshal.ThrowExceptionForHR(result);
        SafeFileHandle process;
        try
        {
            process = StartProcess(executable, arguments, directory, environment, _console);
        }
        catch
        {
            CloseConsole();
            throw;
        }

        var outputPipe = new SafeFileHandle(childOutput.DangerousGetHandle(), ownsHandle: true);
        var inputPipe = new SafeFileHandle(childInput.DangerousGetHandle(), ownsHandle: true);
        childOutput.SetHandleAsInvalid();
        childInput.SetHandleAsInvalid();
        var read = Task.Run(() => DrainOutput(outputPipe, output));
        var write = Task.Run(() => WriteInputAsync(inputPipe));
        Completion = Task.Run(async () =>
        {
            using (process)
            {
                try
                {
                    if (WaitForSingleObject(process, uint.MaxValue) != 0 || !GetExitCodeProcess(process, out var code))
                        throw new Win32Exception();
                    return unchecked((int)code);
                }
                finally
                {
                    _input.Writer.TryComplete();
                    CloseConsole();
                    await Task.WhenAll(read, write);
                }
            }
        });
    }

    internal bool Send(string input) => _input.Writer.TryWrite(input);

    internal void Resize(int columns, int rows)
    {
        lock (_consoleGate)
            if (_console != IntPtr.Zero)
                _ = ResizePseudoConsole(_console, Size(columns, rows));
    }

    internal void Stop()
    {
        _input.Writer.TryComplete();
        _ = Task.Run(CloseConsole);
    }

    private void CloseConsole()
    {
        IntPtr console;
        lock (_consoleGate)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        if (console != IntPtr.Zero)
            ClosePseudoConsole(console);
    }

    private static void DrainOutput(SafeFileHandle pipe, Action<string> output)
    {
        using (pipe)
        using (var reader = new StreamReader(new FileStream(pipe, FileAccess.Read), new UTF8Encoding(false)))
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    output(new string(buffer, 0, count));
            }
            catch (IOException)
            {
                // The output pipe closes when the pseudoconsole exits.
            }
        }
    }

    private async Task WriteInputAsync(SafeFileHandle pipe)
    {
        using (pipe)
        using (var stream = new FileStream(pipe, FileAccess.Write))
        {
            try
            {
                await foreach (var text in _input.Reader.ReadAllAsync())
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes);
                    stream.Flush();
                }
            }
            catch (IOException)
            {
                // The shell may exit while input is queued.
            }
        }
    }

    private static SafeFileHandle StartProcess(string executable, string arguments, string directory,
        IReadOnlyDictionary<string, string>? overrides, IntPtr console)
    {
        nuint size = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(checked((int)size));
        var initialized = false;
        var environment = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                throw new Win32Exception();
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console,
                    (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            if (overrides is { Count: > 0 })
            {
                var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    values[(string)entry.Key] = (string)entry.Value!;
                foreach (var (key, value) in overrides)
                    values[key] = value;
                environment = Marshal.StringToHGlobalUni(
                    string.Join('\0', values.Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0");
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = UseStandardHandles,
                    Input = ConsoleStandardHandle,
                    Output = ConsoleStandardHandle,
                    Error = ConsoleStandardHandle
                },
                Attributes = attributes
            };
            var command = new StringBuilder($"\"{executable}\" {arguments}");
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartup | UnicodeEnvironment, environment, directory, ref startup, out var info))
                throw new Win32Exception();
            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            return new SafeFileHandle(info.Process, ownsHandle: true);
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(environment);
        }
    }

    private static ConsoleSize Size(int columns, int rows) =>
        new((short)Math.Clamp(columns, 10, 500), (short)Math.Clamp(rows, 3, 200));

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ConsoleSize(short Columns, short Rows);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, CharactersX, CharactersY, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, uint size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(ConsoleSize size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, ConsoleSize size);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
        nuint size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
}
