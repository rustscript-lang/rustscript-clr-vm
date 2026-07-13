using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace PdVm.Runtime;

public readonly record struct PdVmExecutionResult(PdVmStatus Status, int Steps);

public static class PdVmExecution
{
    public static PdVmExecutionResult Run(IPdVmProgram program, IPdVmHost host, int maxSteps = 1_000_000)
    {
        var executedSteps = 0;
        while (executedSteps < maxSteps)
        {
            var before = program.ExecutedInstructionCount;
            var status = program.RunStep(host, maxSteps - executedSteps);
            executedSteps = CheckedAccumulateSteps(executedSteps, program, before, maxSteps);
            switch (status.Kind)
            {
                case PdVmStatusKind.Halted:
                    return new PdVmExecutionResult(status, executedSteps);
                case PdVmStatusKind.Yielded:
                    continue;
                case PdVmStatusKind.Waiting:
                    throw new InvalidOperationException(
                        "program entered a waiting state; use RunAsync with an async-capable host");
                default:
                    throw new InvalidOperationException($"unexpected status {status.Kind}");
            }
        }

        throw new InvalidOperationException($"execution exceeded {maxSteps} steps");
    }

    public static async ValueTask<PdVmExecutionResult> RunAsync(
        IPdVmProgram program,
        IAsyncPdVmHost host,
        int maxSteps = 1_000_000,
        CancellationToken cancellationToken = default)
    {
        var executedSteps = 0;
        while (executedSteps < maxSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = program.ExecutedInstructionCount;
            var status = program.RunStep(host, maxSteps - executedSteps);
            executedSteps = CheckedAccumulateSteps(executedSteps, program, before, maxSteps);
            switch (status.Kind)
            {
                case PdVmStatusKind.Halted:
                    return new PdVmExecutionResult(status, executedSteps);
                case PdVmStatusKind.Yielded:
                    continue;
                case PdVmStatusKind.Waiting:
                    var values = await host.WaitAsync(status.WaitingOpId, cancellationToken);
                    program.ResumePending(status.WaitingOpId, values);
                    continue;
                default:
                    throw new InvalidOperationException($"unexpected status {status.Kind}");
            }
        }

        throw new InvalidOperationException($"execution exceeded {maxSteps} steps");
    }

    private static int CheckedAccumulateSteps(
        int currentSteps,
        IPdVmProgram program,
        long before,
        int maxSteps)
    {
        var delta = program.ExecutedInstructionCount - before;
        if (delta <= 0 || delta > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"program reported an invalid executed instruction delta of {delta}");
        }

        var next = checked(currentSteps + (int)delta);
        if (next > maxSteps)
        {
            throw new InvalidOperationException($"execution exceeded {maxSteps} steps");
        }

        return next;
    }
}

public static class PdVmAssemblyLoader
{
    private static readonly ConcurrentDictionary<string, byte> AssemblyDirectories = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static int _resolverRegistered;

    public static void RegisterAssemblyDirectory(string assemblyPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        AssemblyDirectories.TryAdd(directory, 0);
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0)
        {
            AssemblyLoadContext.Default.Resolving += ResolveProgramAssembly;
        }
    }

    public static IPdVmProgram LoadProgram(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        RegisterAssemblyDirectory(fullPath);
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        return CreateProgram(assembly);
    }

    private static Assembly? ResolveProgramAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name))
        {
            return null;
        }

        foreach (var directory in AssemblyDirectories.Keys)
        {
            var candidate = Path.Combine(directory, $"{assemblyName.Name}.dll");
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }

    public static IPdVmProgram CreateProgram(Assembly assembly)
    {
        var programType = assembly
            .GetTypes()
            .FirstOrDefault(type =>
                !type.IsAbstract &&
                typeof(IPdVmProgram).IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) is not null);

        if (programType is null)
        {
            throw new InvalidOperationException("no concrete IPdVmProgram implementation found");
        }

        return (IPdVmProgram)Activator.CreateInstance(programType)!;
    }
}
