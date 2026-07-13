namespace PdVm.Runtime;

/// <summary>
/// Exposes the process monotonic clock to RustScript in milliseconds.
/// </summary>
[PdVmInteropType("System.Diagnostics.MonotonicClock")]
public static class PdVmMonotonicClock
{
    public static long GetMilliseconds() => Environment.TickCount64;
}
