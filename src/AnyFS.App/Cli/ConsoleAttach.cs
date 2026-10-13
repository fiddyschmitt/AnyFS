using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AnyFS.App.Cli;

/// <summary>
/// anyfs is a Windows GUI executable (no console of its own) so the window starts without a console
/// flashing up. With command-line arguments it attaches to the parent's console so output lands in
/// the terminal that started it (spec 12). Handles the parent redirected (a pipe, a file) are kept.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ConsoleAttach
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;

    public static bool AttachToParent()
    {
        var input = GetStdHandle(StdInput);
        var output = GetStdHandle(StdOutput);
        var error = GetStdHandle(StdError);
        if (!AttachConsole(AttachParentProcess))
        {
            return false;
        }

        // attaching points the standard handles at the console; put back any the parent had redirected
        Restore(StdInput, input);
        Restore(StdOutput, output);
        Restore(StdError, error);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
        }

        return true;
    }

    private static void Restore(int which, nint original)
    {
        if (original != 0 && original != -1)
        {
            SetStdHandle(which, original);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int which, nint handle);
}
