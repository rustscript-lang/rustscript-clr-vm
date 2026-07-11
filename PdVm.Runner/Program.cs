using PdVm.Compiler;
using PdVm.Runtime;

internal static class Program
{
    [STAThread]
    public static Task<int> Main(string[] args) => ProgramEntry.RunAsync(args);
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

        var profile = GetOption(args, "--profile")?.ToLowerInvariant() switch
        {
            null or "common" => PdVmDotNetInteropProfile.Common,
            "winforms" => PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            var value => throw new ArgumentException($"unknown .NET interop profile '{value}'"),
        };
        var output = PdVmDotNetSourceCompiler.CompileFile(
            args[1],
            args[2],
            new PdVmDotNetSourceCompileOptions
            {
                Profile = profile,
                RustScriptCompilerPath = GetOption(args, "--rustscript-compiler"),
                SourceRoot = GetOption(args, "--source-root"),
            });
        Console.WriteLine(output);
        return 0;
    }

    private static async Task<int> RunAssemblyAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
        {
            throw new ArgumentException("run requires <program.dll>");
        }

        var program = PdVmAssemblyLoader.LoadProgram(args[1]);
        var host = PdVmDefaultHost.CreateConsoleHost();
        var dotNetHost = new PdVmDotNetHost(
            allowDynamicSystemCalls: HasFlag(args, "--enable-dynamic-dotnet"));
        host.RegisterFallback(dotNetHost.Call);
        var result = await PdVmExecution.RunAsync(program, host, GetMaxSteps(args, 2));
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
        Console.Error.WriteLine("    [--rustscript-compiler <path>] [--source-root <path>]");
        Console.Error.WriteLine("  PdVm.Runner run <program.dll> [--max-steps <count>] [--enable-dynamic-dotnet]");
        Console.Error.WriteLine("    --max-steps is enforced inside generated CLR code");
        Console.Error.WriteLine("    --enable-dynamic-dotnet enables the experimental reflection host");
        Console.Error.WriteLine("  PdVm.Runner compile-run <input.vmbc> [output.dll] [--max-steps <count>] [--enable-dynamic-dotnet]");
        Console.Error.WriteLine("    --max-steps is enforced inside generated CLR code");
        Console.Error.WriteLine("    --enable-dynamic-dotnet enables the experimental reflection host");
    }
}
