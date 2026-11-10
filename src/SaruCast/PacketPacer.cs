using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SaruCast;

// Task.Delay(1) can sleep for a ~15.6ms Windows timer tick. Use a high-resolution
// waitable timer without busy-waiting or changing the system timer resolution.
internal sealed class PacketPacer : IDisposable
{
    private sealed class TimerHandle : WaitHandle
    {
        internal TimerHandle()
        {
            var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x1f0003);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            SafeWaitHandle = handle;
        }
    }
    private readonly TimerHandle timer = new();
    // A transport serializes calls with its send lock.
    public async Task WaitAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        long dueTime = -10000; // relative 100ns units: 1ms
        if (!SetWaitableTimer(timer.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(timer, (_, _) => done.TrySetResult(), null, Timeout.Infinite, true);
        try { await done.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally { registration.Unregister(null); }
    }
    public void Dispose() => timer.Dispose();
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr callback, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
}
