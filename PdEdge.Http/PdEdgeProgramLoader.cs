using System.Reflection;
using PdVm.Compiler;
using PdVm.Runtime;

namespace PdEdge.Http;

public sealed class PdEdgeLoadedProgram
{
    public required IReadOnlyList<PdVmHostImport> Imports { get; init; }

    public required bool UsesAsyncHostOps { get; init; }

    public required Func<IPdVmProgram> CreateProgram { get; init; }
}

public static class PdEdgeProgramLoader
{
    public static async Task<PdEdgeLoadedProgram?> LoadAsync(
        PdEdgeHttpOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ProgramSourcePath) &&
            string.IsNullOrWhiteSpace(options.ProgramVmbcPath))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.ProgramSourcePath))
        {
            return await LoadFromSourceFileAsync(options.ProgramSourcePath!, cancellationToken);
        }

        return await LoadFromVmbcFileAsync(options.ProgramVmbcPath!, cancellationToken);
    }

    public static async Task<PdEdgeLoadedProgram> LoadFromSourceFileAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var vmbc = await CompileSourceFileAsync(sourcePath, cancellationToken);
        return LoadFromVmbcBytes(vmbc, Path.GetFileNameWithoutExtension(sourcePath));
    }

    public static async Task<PdEdgeLoadedProgram> LoadFromVmbcFileAsync(
        string vmbcPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vmbcPath);
        var bytes = await File.ReadAllBytesAsync(vmbcPath, cancellationToken);
        return LoadFromVmbcBytes(bytes, Path.GetFileNameWithoutExtension(vmbcPath));
    }

    public static PdEdgeLoadedProgram LoadFromVmbcBytes(byte[] vmbcBytes, string? assemblyStem = null)
    {
        ArgumentNullException.ThrowIfNull(vmbcBytes);

        var model = PdVmVmbcReader.ReadBytes(vmbcBytes);
        PdEdgeHostFunctions.ValidateImports(model.Imports);

        var assemblyName = $"{(string.IsNullOrWhiteSpace(assemblyStem) ? "PdEdge.Program" : assemblyStem)}.{Guid.NewGuid():N}";
        var tempRoot = GetTempWorkRoot();
        Directory.CreateDirectory(tempRoot);
        var outputPath = Path.Combine(tempRoot, $"{assemblyName}.dll");
        PdVmClrCompiler.Compile(
            model,
            outputPath,
            new PdVmCompileOptions
            {
                AssemblyName = assemblyName,
                ModuleName = $"{assemblyName}.dll",
                TypeName = $"PdEdge.Generated.Program_{Guid.NewGuid():N}",
            });

        var assembly = Assembly.LoadFile(outputPath);
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

        return new PdEdgeLoadedProgram
        {
            Imports = model.Imports,
            UsesAsyncHostOps = PdEdgeHostFunctions.UsesAsyncHostOps(model.Imports),
            CreateProgram = () => (IPdVmProgram)Activator.CreateInstance(programType)!,
        };
    }

    public static async Task CompileSourceFileToVmbcAsync(
        string sourcePath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var vmbc = await CompileSourceFileAsync(sourcePath, cancellationToken);
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllBytesAsync(fullOutputPath, vmbc, cancellationToken);
    }

    private static async Task<byte[]> CompileSourceFileAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var normalizedSourcePath = await NormalizeSourcePathAsync(sourcePath, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return PdVmNativeCompiler.CompileFile(normalizedSourcePath);
        }
        finally
        {
            if (!string.Equals(normalizedSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(normalizedSourcePath);
            }
        }
    }

    private static async Task<string> NormalizeSourcePathAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        if (bytes.Length < 3 ||
            bytes[0] != 0xEF ||
            bytes[1] != 0xBB ||
            bytes[2] != 0xBF)
        {
            return sourcePath;
        }

        var tempPath = Path.Combine(GetTempWorkRoot(), $"{Guid.NewGuid():N}.rss");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        await File.WriteAllBytesAsync(tempPath, bytes[3..], cancellationToken);
        return tempPath;
    }

    private static string GetTempWorkRoot() =>
        Path.Combine(Path.GetTempPath(), "pd-edge-http-clr");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
