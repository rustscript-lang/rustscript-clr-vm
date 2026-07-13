using System.Reflection;
using PdVm.Compiler;
using PdVm.Runtime;

internal static class Program
{
    [STAThread]
    public static Task<int> Main(string[] args)
    {
        PdVmDotNetHost.InitializeWindowsFormsApplication();
        return ProgramEntry.RunAsync(args);
    }
}

internal static class ProgramEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "compile":
                    return RunCompile(args);
                case "compile-source":
                    return RunCompileSource(args);
                case "emit-vmbc":
                    return RunEmitVmbc(args);
                case "run":
                    return await RunAssemblyAsync(args);
                case "compile-run":
                    return await RunCompileAndExecuteAsync(args);
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int RunCompile(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
        {
            throw new ArgumentException("compile requires <input.vmbc> <output.dll>");
        }

        var output = PdVmClrCompiler.CompileFile(args[1], args[2]);
        Console.WriteLine(output);
        return 0;
    }

    private static int RunCompileSource(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
        {
            throw new ArgumentException("compile-source requires <input.rss> <output.dll>");
        }

        var output = PdVmDotNetSourceCompiler.CompileFile(
            args[1],
            args[2],
            new PdVmDotNetSourceCompileOptions
            {
                Profile = GetInteropProfile(args),
                NativeCompilerLibraryPath = GetOption(args, "--pd-vm-library"),
                SourceRoot = GetOption(args, "--source-root"),
            });
        Console.WriteLine(output);
        return 0;
    }

    private static int RunEmitVmbc(IReadOnlyList<string> args)
    {
        if (args.Count < 3)
        {
            throw new ArgumentException("emit-vmbc requires <input.rss> <output.vmbc>");
        }
        PdVmNativeCompiler.CompileFileToVmbc(
            args[1],
            args[2],
            GetOption(args, "--pd-vm-library"));
        Console.WriteLine(Path.GetFullPath(args[2]));
        return 0;
    }

    private static async Task<int> RunAssemblyAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            throw new ArgumentException("run requires <program.dll|program.rss>");
        }

        if (string.Equals(Path.GetExtension(args[1]), ".rss", StringComparison.OrdinalIgnoreCase))
        {
            return await RunSourceAsync(args);
        }

        return await RunProgramAssemblyAsync(args[1], args, 2);
    }

    private static async Task<int> RunSourceAsync(IReadOnlyList<string> args)
    {
        var sourcePath = Path.GetFullPath(args[1]);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("RustScript source file was not found", sourcePath);
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-run",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var outputPath = Path.Combine(temporaryRoot, "program.dll");
            PdVmDotNetSourceCompiler.CompileFile(
                sourcePath,
                outputPath,
                new PdVmDotNetSourceCompileOptions
                {
                    Profile = GetInteropProfile(args),
                    NativeCompilerLibraryPath = GetOption(args, "--pd-vm-library"),
                    SourceRoot = GetOption(args, "--source-root"),
                });
            return await RunProgramAssemblyAsync(outputPath, args, 2, loadFromMemory: true);
        }
        finally
        {
            DeleteTemporaryDirectory(temporaryRoot);
        }
    }

    private static async Task<int> RunProgramAssemblyAsync(
        string assemblyPath,
        IReadOnlyList<string> args,
        int optionsStartIndex,
        bool loadFromMemory = false)
    {
        PdVmAssemblyLoader.RegisterAssemblyDirectory(assemblyPath);
        var program = loadFromMemory
            ? PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(assemblyPath)))
            : PdVmAssemblyLoader.LoadProgram(assemblyPath);
        var host = PdVmDefaultHost.CreateConsoleHost();
        var dotNetHost = new PdVmDotNetHost(
            allowDynamicSystemCalls: HasFlag(args, "--enable-dynamic-dotnet"));
        host.RegisterFallback(dotNetHost.Call);
        var result = await PdVmExecution.RunAsync(program, host, GetMaxSteps(args, optionsStartIndex));
        Console.WriteLine($"status={result.Status} steps={result.Steps}");
        return 0;
    }

    private static async Task<int> RunCompileAndExecuteAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            throw new ArgumentException("compile-run requires <input.vmbc> [output.dll]");
        }

        var output = args.Count >= 3
            ? args[2]
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(args[1])) ?? Environment.CurrentDirectory,
                $"{Path.GetFileNameWithoutExtension(args[1])}.dll");

        PdVmClrCompiler.CompileFile(args[1], output);
        var runArgs = new List<string> { "run", output };
        for (var index = 2; index < args.Count; index++)
        {
            if (string.Equals(args[index], output, StringComparison.Ordinal))
            {
                continue;
            }

            runArgs.Add(args[index]);
        }

        return await RunAssemblyAsync(runArgs);
    }

    private static int GetMaxSteps(IReadOnlyList<string> args, int startIndex)
    {
        const int defaultMaxSteps = 1_000_000;
        for (var index = startIndex; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--max-steps", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Count || !int.TryParse(args[index + 1], out var maxSteps) || maxSteps <= 0)
            {
                throw new ArgumentException("--max-steps requires a positive integer value");
            }

            return maxSteps;
        }

        return defaultMaxSteps;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.Ordinal));

    private static PdVmDotNetInteropProfile GetInteropProfile(IReadOnlyList<string> args) =>
        GetOption(args, "--profile")?.ToLowerInvariant() switch
        {
            null or "common" => PdVmDotNetInteropProfile.Common,
            "winforms" => PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            var value => throw new ArgumentException($"unknown .NET interop profile '{value}'"),
        };

    private static void DeleteTemporaryDirectory(string path)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pd-vm-run"));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"refusing to delete path outside temporary run root: {fullPath}");
        }
        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static string? GetOption(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.Ordinal))
            {
                continue;
            }
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException($"{option} requires a value");
            }
            return args[index + 1];
        }
        return null;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  PdVm.Runner compile <input.vmbc> <output.dll>");
        Console.Error.WriteLine("  PdVm.Runner compile-source <input.rss> <output.dll> [--profile common|winforms]");
        Console.Error.WriteLine("    [--pd-vm-library <path>] [--source-root <path>]");
        Console.Error.WriteLine("  PdVm.Runner emit-vmbc <input.rss> <output.vmbc> [--pd-vm-library <path>]");
        Console.Error.WriteLine("  PdVm.Runner run <program.dll|program.rss> [--profile common|winforms]");
        Console.Error.WriteLine("    [--source-root <path>] [--pd-vm-library <path>]");
        Console.Error.WriteLine("    [--max-steps <count>] [--enable-dynamic-dotnet]");
        Console.Error.WriteLine("    --max-steps is enforced inside generated CLR code");
        Console.Error.WriteLine("    --enable-dynamic-dotnet enables the experimental reflection host");
        Console.Error.WriteLine("  PdVm.Runner compile-run <input.vmbc> [output.dll] [--max-steps <count>] [--enable-dynamic-dotnet]");
        Console.Error.WriteLine("    --max-steps is enforced inside generated CLR code");
        Console.Error.WriteLine("    --enable-dynamic-dotnet enables the experimental reflection host");
    }
}
