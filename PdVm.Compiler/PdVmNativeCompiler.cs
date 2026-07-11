using System.Runtime.InteropServices;
using System.Text;

namespace PdVm.Compiler;

public static class PdVmNativeCompiler
{
    private const string NativeLibraryName = "pd_vm_compiler";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CompileFileDelegate(
        byte[] path,
        nuint pathLength,
        out IntPtr output,
        out nuint outputLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeBufferDelegate(IntPtr buffer, nuint bufferLength);

    private sealed record NativeApi(
        CompileFileDelegate CompileFile,
        FreeBufferDelegate FreeBuffer);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, NativeApi> Apis = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly NativeApi DefaultApi = new(
        NativeMethods.CompileFile,
        NativeMethods.FreeBuffer);

    static PdVmNativeCompiler()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(PdVmNativeCompiler).Assembly,
            static (libraryName, _, _) =>
                string.Equals(libraryName, NativeLibraryName, StringComparison.Ordinal)
                    ? NativeLibrary.Load(ResolveLibraryPath(configuredPath: null))
                    : IntPtr.Zero);
    }

    public static byte[] CompileFile(string sourcePath, string? libraryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var api = GetApi(libraryPath);
        var pathBytes = Encoding.UTF8.GetBytes(fullSourcePath);
        var status = api.CompileFile(
            pathBytes,
            checked((nuint)pathBytes.Length),
            out var output,
            out var outputLength);
        try
        {
            if (outputLength > int.MaxValue)
            {
                throw new PdVmCompilerException("pd-vm native compiler returned an oversized buffer");
            }
            var bytes = new byte[(int)outputLength];
            if (bytes.Length > 0)
            {
                if (output == IntPtr.Zero)
                {
                    throw new PdVmCompilerException("pd-vm native compiler returned an invalid buffer");
                }
                Marshal.Copy(output, bytes, 0, bytes.Length);
            }
            if (status != 0)
            {
                var diagnostic = Encoding.UTF8.GetString(bytes);
                throw new PdVmCompilerException(
                    $"RustScript compilation failed in pd-vm native compiler (status {status}):{Environment.NewLine}{diagnostic}".Trim());
            }
            return bytes;
        }
        finally
        {
            if (output != IntPtr.Zero)
            {
                api.FreeBuffer(output, outputLength);
            }
        }
    }

    public static void CompileFileToVmbc(
        string sourcePath,
        string outputPath,
        string? libraryPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var bytes = CompileFile(sourcePath, libraryPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllBytes(fullOutputPath, bytes);
    }

    private static NativeApi GetApi(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return DefaultApi;
        }
        var libraryPath = ResolveLibraryPath(configuredPath);
        lock (Sync)
        {
            if (Apis.TryGetValue(libraryPath, out var existing))
            {
                return existing;
            }
            var handle = NativeLibrary.Load(libraryPath);
            var api = new NativeApi(
                Marshal.GetDelegateForFunctionPointer<CompileFileDelegate>(
                    NativeLibrary.GetExport(handle, "pdvm_compile_file_utf8")),
                Marshal.GetDelegateForFunctionPointer<FreeBufferDelegate>(
                    NativeLibrary.GetExport(handle, "pdvm_free_buffer")));
            Apis.Add(libraryPath, api);
            return api;
        }
    }

    private static string ResolveLibraryPath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var explicitPath = Path.GetFullPath(configuredPath);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("pd-vm native compiler library was not found", explicitPath);
            }
            return explicitPath;
        }

        var environmentPath = Environment.GetEnvironmentVariable("PDVM_NATIVE_COMPILER");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return ResolveLibraryPath(environmentPath);
        }

        var fileName = GetLibraryFileName();
        foreach (var directory in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.GetDirectoryName(typeof(PdVmNativeCompiler).Assembly.Location),
                     Environment.CurrentDirectory,
                 }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory!, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                foreach (var configuration in new[] { "debug", "release" })
                {
                    var candidate = Path.Combine(
                        directory.FullName,
                        "native",
                        "pd-vm-compiler",
                        "target",
                        configuration,
                        fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                directory = directory.Parent;
            }
        }

        throw new DllNotFoundException(
            $"{fileName} was not found beside the application. Set PDVM_NATIVE_COMPILER to its full path.");
    }

    public static string GetLibraryFileName() =>
        OperatingSystem.IsWindows()
            ? "pd_vm_compiler.dll"
            : OperatingSystem.IsMacOS()
                ? "libpd_vm_compiler.dylib"
                : "libpd_vm_compiler.so";

    private static class NativeMethods
    {
        [DllImport(
            NativeLibraryName,
            EntryPoint = "pdvm_compile_file_utf8",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CompileFile(
            byte[] path,
            nuint pathLength,
            out IntPtr output,
            out nuint outputLength);

        [DllImport(
            NativeLibraryName,
            EntryPoint = "pdvm_free_buffer",
            CallingConvention = CallingConvention.Cdecl)]
        internal static extern void FreeBuffer(IntPtr buffer, nuint bufferLength);
    }
}
