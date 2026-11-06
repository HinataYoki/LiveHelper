using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveHelper.Core.Services;

/// <summary>
/// Prevents macOS App Nap from throttling the hidden GUI process. When requested,
/// the same activity also prevents idle system sleep so scheduled live checks keep
/// running. Closing the lid can still put the machine to sleep.
/// </summary>
internal sealed class MacBackgroundActivity : IDisposable
{
    private const ulong ActivityIdleSystemSleepDisabled = 1UL << 20;
    private const ulong ActivityUserInitiatedAllowingIdleSystemSleep = 0x00FFFFFFUL;

    private IntPtr _activity;
    private IntPtr _reason;

    private MacBackgroundActivity(IntPtr activity, IntPtr reason)
    {
        _activity = activity;
        _reason = reason;
    }

    public static MacBackgroundActivity? TryStart(bool preventIdleSystemSleep)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var pool = IntPtr.Zero;
        var reason = IntPtr.Zero;
        var reasonTransferred = false;
        try
        {
            pool = CreateAutoreleasePool();
            var processInfo = Send(
                ObjcGetClass("NSProcessInfo"),
                SelRegisterName("processInfo"));
            var nsStringClass = ObjcGetClass("NSString");
            var allocated = Send(nsStringClass, SelRegisterName("alloc"));
            var reasonUtf8 = Marshal.StringToCoTaskMemUTF8(
                "LiveHelper keeps live-room checks and recording active while hidden.");
            try
            {
                reason = SendPointer(
                    allocated,
                    SelRegisterName("initWithUTF8String:"),
                    reasonUtf8);
            }
            finally
            {
                Marshal.FreeCoTaskMem(reasonUtf8);
            }

            if (processInfo == IntPtr.Zero || reason == IntPtr.Zero)
            {
                return null;
            }

            var options = ActivityUserInitiatedAllowingIdleSystemSleep;
            if (preventIdleSystemSleep)
            {
                options |= ActivityIdleSystemSleepDisabled;
            }

            var activity = SendActivity(
                processInfo,
                SelRegisterName("beginActivityWithOptions:reason:"),
                (nuint)options,
                reason);
            if (activity == IntPtr.Zero)
            {
                return null;
            }

            // beginActivityWithOptions returns an autoreleased token. Retain it
            // beyond the temporary autorelease pool used by this method.
            _ = Send(activity, SelRegisterName("retain"));
            reasonTransferred = true;
            return new MacBackgroundActivity(activity, reason);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to start macOS background activity: {ex.Message}");
            return null;
        }
        finally
        {
            if (reason != IntPtr.Zero && !reasonTransferred)
            {
                _ = Send(reason, SelRegisterName("release"));
            }

            DrainAutoreleasePool(pool);
        }
    }

    public void Dispose()
    {
        var activity = Interlocked.Exchange(ref _activity, IntPtr.Zero);
        var reason = Interlocked.Exchange(ref _reason, IntPtr.Zero);
        if (activity == IntPtr.Zero)
        {
            if (reason != IntPtr.Zero)
            {
                _ = Send(reason, SelRegisterName("release"));
            }

            return;
        }

        try
        {
            _ = Send(activity, SelRegisterName("release"));
            if (reason != IntPtr.Zero)
            {
                _ = Send(reason, SelRegisterName("release"));
            }
        }
        catch
        {
            // Process shutdown is best effort.
        }
    }

    private static IntPtr CreateAutoreleasePool()
    {
        var pool = Send(ObjcGetClass("NSAutoreleasePool"), SelRegisterName("alloc"));
        return pool == IntPtr.Zero ? IntPtr.Zero : Send(pool, SelRegisterName("init"));
    }

    private static void DrainAutoreleasePool(IntPtr pool)
    {
        if (pool == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SendVoid(pool, SelRegisterName("drain"));
        }
        catch
        {
            // The token is still retained independently of the pool.
        }
    }

    [DllImport("libobjc", EntryPoint = "objc_getClass")]
    private static extern IntPtr ObjcGetClass(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libobjc", EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libobjc", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("libobjc", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(
        IntPtr receiver,
        IntPtr selector,
        IntPtr argument);

    [DllImport("libobjc", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendActivity(
        IntPtr receiver,
        IntPtr selector,
        nuint options,
        IntPtr reason);

    [DllImport("libobjc", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);
}
