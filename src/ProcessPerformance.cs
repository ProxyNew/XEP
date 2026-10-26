using System.Runtime.InteropServices;

namespace XEnchantmentPatcher;

// Analysis is CPU-heavy even when the app's window is inactive. Request HighQoS
// for this process, preserving other power controls. Unsupported policy leaves
// the system defaults in effect; it must never prevent patching.
internal static class ProcessPerformance
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 1;

    public static void PreferPerformance()
    {
        var process = GetCurrentProcess();
        var policy = new PowerThrottlingState { Version = 1 };
        var size = (uint)Marshal.SizeOf<PowerThrottlingState>();
        if (!GetProcessInformation(process, ProcessPowerThrottling, ref policy, size)) return;
        policy.ControlMask |= ExecutionSpeed;
        policy.StateMask &= ~ExecutionSpeed;
        _ = SetProcessInformation(process, ProcessPowerThrottling, ref policy, size);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(nint process, int informationClass,
        ref PowerThrottlingState information, uint size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass,
        ref PowerThrottlingState information, uint size);
}
