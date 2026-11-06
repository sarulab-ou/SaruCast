using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SaruCast;

internal sealed class KillOnCloseJob : IDisposable
{
    private IntPtr handle;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    public KillOnCloseJob(Process child)
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) { child.Kill(true); throw new IOException("エンコーダーの終了監視を作成できません。"); }
        IntPtr data = Marshal.AllocHGlobal(144);
        try
        {
            Marshal.Copy(new byte[144], 0, data, 144); Marshal.WriteInt32(data, 16, 0x2000);
            if (!SetInformationJobObject(handle, 9, data, 144) || !AssignProcessToJobObject(handle, child.Handle))
            { child.Kill(true); Dispose(); throw new IOException("エンコーダーの終了監視を設定できません。"); }
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
}
