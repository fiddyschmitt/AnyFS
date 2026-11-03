using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenQA.Selenium;

namespace AnyFS.Sources.Common;

/// <summary>
/// What one WebDriver session runs: its driver, the browser the driver starts, and all they start. A driver that goes first
/// leaves its browser running with nothing left that can close it, holding AnyFS's profile, so the next sign-in on that profile
/// cannot start. Selenium ends the driver whenever a Quit fails or times out (a page that stalled holds up the driver's commands,
/// a browser that stopped answering) and whenever a session cannot be made, and AnyFS itself can end at any moment. So on Windows
/// they run in a job object that ends everything in it with its last handle: this object's, or the system's to close if AnyFS goes
/// first. Elsewhere, a Quit that does not come back in time ends the driver's process tree while the driver is still there to
/// follow it from; a browser whose driver went some other way stays.
/// </summary>
internal sealed class DriverProcesses(TimeSpan? quitDeadline = null) : IDisposable
{
    private static readonly TimeSpan EndedDeadline = TimeSpan.FromSeconds(5);

    // a browser closes in a second or two; long enough for a busy machine, and shorter than Selenium's minute, after which its
    // driver is gone
    private readonly TimeSpan _quitDeadline = quitDeadline ?? TimeSpan.FromSeconds(20);
    private readonly Lock _gate = new();
    private SafeWaitHandle? _job;
    private Process? _driver;

    /// <summary>Takes in the driver as soon as <paramref name="service"/> starts it, before it starts a browser.</summary>
    public void Watch(DriverService service) => service.DriverProcessStarted += (_, e) => Take(e.ProcessId);

    /// <summary>Asks the browser to close through its driver, then ends what is left. False when it did not close in time.</summary>
    public async Task<bool> QuitAsync(IWebDriver driver)
    {
        var quit = Task.Run(() =>
        {
            try
            {
                driver.Quit();
            }
            catch (Exception ex) when (ex is WebDriverException or InvalidOperationException or ObjectDisposedException or Win32Exception)
            {
                // already gone
            }
        });

        var inTime = true;
        try
        {
            await quit.WaitAsync(_quitDeadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            inTime = false;
            End();
            try
            {
                await quit.WaitAsync(EndedDeadline).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // nothing is left for it to wait on; it will come back by itself
            }
        }

        End();
        return inTime;
    }

    public void Dispose()
    {
        End();
        _driver?.Dispose();
    }

    /// <summary>The driver, just started; one already gone has started nothing.</summary>
    internal void Take(int processId)
    {
        lock (_gate)
        {
            try
            {
                _driver = Process.GetProcessById(processId);
                _job = OperatingSystem.IsWindows() ? JobHolding(_driver) : null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
            }
        }
    }

    /// <summary>Ends all that is still running: the job on Windows; elsewhere, or without a job, the tree below a driver still there.</summary>
    private void End()
    {
        lock (_gate)
        {
            if (_job is { } job)
            {
                _job = null;
                job.Dispose();
                return;
            }

            try
            {
                if (_driver is { HasExited: false } driver)
                {
                    driver.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
            {
                // gone meanwhile
            }
        }
    }

    /// <summary>A job that ends all in it when its last handle closes, with <paramref name="process"/> in it and so all it starts from now on; null if Windows refuses.</summary>
    private static SafeWaitHandle? JobHolding(Process process)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (!job.IsInvalid
            && SetInformationJobObject(job, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
            && AssignProcessToJobObject(job, process.SafeHandle))
        {
            return job;
        }

        job.Dispose();
        return null;
    }

    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeWaitHandle job, int informationClass, ref ExtendedLimits information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeWaitHandle job, SafeProcessHandle process);
}
