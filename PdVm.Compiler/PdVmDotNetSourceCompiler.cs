using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PdVm.Runtime;

namespace PdVm.Compiler;

[Flags]
public enum PdVmDotNetInteropProfile
{
    Common = 1,
    WindowsForms = 2,
}

public sealed class PdVmDotNetSourceCompileOptions
{
    public string? RustScriptCompilerPath { get; init; }

    public string? SourceRoot { get; init; }

    public PdVmDotNetInteropProfile Profile { get; init; } = PdVmDotNetInteropProfile.Common;

    public bool KeepTemporaryFiles { get; init; }
}

public static class PdVmDotNetSourceCompiler
{
    private sealed record Parameter(string Name, string Schema);

    private sealed record Binding(
        string ModulePath,
        string PublicName,
        PdVmDotNetMemberKind Kind,
        Type DeclaringType,
        string MemberName,
        Type[] ClrParameterTypes,
        Parameter[] Parameters,
        string ReturnSchema);

    public static string CompileFile(
        string sourcePath,
        string outputPath,
        PdVmDotNetSourceCompileOptions? options = null,
        PdVmCompileOptions? clrOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new PdVmDotNetSourceCompileOptions();

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var sourceRoot = Path.GetFullPath(options.SourceRoot ?? Path.GetDirectoryName(fullSourcePath)!);
        if (!IsPathWithin(fullSourcePath, sourceRoot))
        {
            throw new InvalidOperationException(
                $"source '{fullSourcePath}' is outside source root '{sourceRoot}'");
        }

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-dotnet-source",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            CopySourceOverlay(sourceRoot, temporaryRoot);
            var bindings = BuildBindings(options.Profile);
            var importMap = WriteBindingModules(temporaryRoot, bindings);
            var relativeSource = Path.GetRelativePath(sourceRoot, fullSourcePath);
            var overlaySource = Path.Combine(temporaryRoot, relativeSource);
            var vmbcPath = Path.Combine(temporaryRoot, "program.vmbc");
            RunRustScriptCompiler(
                FindRustScriptCompiler(options.RustScriptCompilerPath),
                overlaySource,
                vmbcPath);

            var model = RemapImports(PdVmVmbcReader.ReadFile(vmbcPath), importMap);
            return PdVmClrCompiler.Compile(model, outputPath, clrOptions);
        }
        finally
        {
            if (!options.KeepTemporaryFiles)
            {
                TryDeleteDirectory(temporaryRoot);
            }
        }
    }

    internal static IReadOnlyDictionary<string, string> GenerateModules(
        string outputRoot,
        PdVmDotNetInteropProfile profile)
    {
        Directory.CreateDirectory(outputRoot);
        return WriteBindingModules(outputRoot, BuildBindings(profile))
            .ToDictionary(pair => pair.Key, pair => pair.Value.EncodeImportName(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<Binding> BuildBindings(PdVmDotNetInteropProfile profile)
    {
        var bindings = new List<Binding>();
        if (profile.HasFlag(PdVmDotNetInteropProfile.Common))
        {
            bindings.AddRange(BuildCommonBindings());
        }
        if (profile.HasFlag(PdVmDotNetInteropProfile.WindowsForms))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Windows Forms interop requires Windows");
            }
            bindings.AddRange(BuildWindowsFormsBindings());
        }
        return bindings;
    }

    private static IEnumerable<Binding> BuildCommonBindings()
    {
        yield return Static("system/System/Console.rss", "WriteLine", typeof(Console), nameof(Console.WriteLine), [typeof(string)], [("value", "string")], "null");
        yield return Static("system/System/Console.rss", "WriteLineInt", typeof(Console), nameof(Console.WriteLine), [typeof(long)], [("value", "int")], "null");
        yield return Static("system/System/Math.rss", "Sqrt", typeof(Math), nameof(Math.Sqrt), [typeof(double)], [("value", "float")], "float");
        yield return Static("system/System/Math.rss", "AbsInt", typeof(Math), nameof(Math.Abs), [typeof(long)], [("value", "int")], "int");
        yield return Static("system/System/Math.rss", "AbsFloat", typeof(Math), nameof(Math.Abs), [typeof(double)], [("value", "float")], "float");
        yield return Static("system/System/IO/Path.rss", "GetFileName", typeof(Path), nameof(Path.GetFileName), [typeof(string)], [("path", "string")], "string");
        yield return Static("system/System/IO/Path.rss", "Combine", typeof(Path), nameof(Path.Combine), [typeof(string), typeof(string)], [("left", "string"), ("right", "string")], "string");
        yield return Static("system/System/IO/File.rss", "Exists", typeof(File), nameof(File.Exists), [typeof(string)], [("path", "string")], "bool");
        yield return Static("system/System/IO/File.rss", "ReadAllText", typeof(File), nameof(File.ReadAllText), [typeof(string)], [("path", "string")], "string");
        yield return Static("system/System/IO/File.rss", "WriteAllText", typeof(File), nameof(File.WriteAllText), [typeof(string), typeof(string)], [("path", "string"), ("content", "string")], "null");

        var stringBuilder = typeof(StringBuilder);
        yield return Constructor("system/System/Text/StringBuilder.rss", "New", stringBuilder, [], [], "int");
        yield return Instance("system/System/Text/StringBuilder.rss", "Append", stringBuilder, nameof(StringBuilder.Append), [typeof(string)], [("handle", "int"), ("value", "string")], "int");
        yield return Instance("system/System/Text/StringBuilder.rss", "ToString", stringBuilder, nameof(StringBuilder.ToString), [], [("handle", "int")], "string");
        yield return PropertyGet("system/System/Text/StringBuilder.rss", "GetLength", stringBuilder, nameof(StringBuilder.Length), [("handle", "int")], "int");
        yield return Release("system/System/Text/StringBuilder.rss", "Release", stringBuilder);
    }

    private static IEnumerable<Binding> BuildWindowsFormsBindings()
    {
        var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);
        var form = ResolveLoadedType(host, "System.Windows.Forms.Form");
        var label = ResolveLoadedType(host, "System.Windows.Forms.Label");
        var button = ResolveLoadedType(host, "System.Windows.Forms.Button");
        var control = ResolveLoadedType(host, "System.Windows.Forms.Control");
        var controlCollection = ResolveLoadedType(host, "System.Windows.Forms.Control+ControlCollection");

        foreach (var binding in ControlBindings("system/System/Windows/Forms/Form.rss", "Form", form)) yield return binding;
        yield return PropertySet("system/System/Windows/Forms/Form.rss", "SetFormStartPosition", form, "StartPosition", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("system/System/Windows/Forms/Form.rss", "SetFormAcceptButton", form, "AcceptButton", [("handle", "int"), ("button", "int")]);
        yield return PropertyGet("system/System/Windows/Forms/Form.rss", "GetFormControls", form, "Controls", [("handle", "int")], "int");
        yield return Instance("system/System/Windows/Forms/Form.rss", "ShowFormDialog", form, "ShowDialog", [], [("handle", "int")], "string");

        foreach (var binding in ControlBindings("system/System/Windows/Forms/Label.rss", "Label", label)) yield return binding;
        yield return PropertySet("system/System/Windows/Forms/Label.rss", "SetLabelAutoSize", label, "AutoSize", [("handle", "int"), ("value", "bool")]);

        foreach (var binding in ControlBindings("system/System/Windows/Forms/Button.rss", "Button", button)) yield return binding;
        yield return PropertySet("system/System/Windows/Forms/Button.rss", "SetButtonDialogResult", button, "DialogResult", [("handle", "int"), ("value", "string")]);

        yield return Instance("system/System/Windows/Forms/ControlCollection.rss", "AddControl", controlCollection, "Add", [control], [("handle", "int"), ("control", "int")], "null");
        yield return Release("system/System/Windows/Forms/ControlCollection.rss", "ReleaseControlCollection", controlCollection);
    }

    private static IEnumerable<Binding> ControlBindings(string module, string typeName, Type type)
    {
        yield return Constructor(module, $"New{typeName}", type, [], [], "int");
        yield return PropertySet(module, $"Set{typeName}Text", type, "Text", [("handle", "int"), ("value", "string")]);
        yield return PropertySet(module, $"Set{typeName}Left", type, "Left", [("handle", "int"), ("value", "int")]);
        yield return PropertySet(module, $"Set{typeName}Top", type, "Top", [("handle", "int"), ("value", "int")]);
        yield return PropertySet(module, $"Set{typeName}Width", type, "Width", [("handle", "int"), ("value", "int")]);
        yield return PropertySet(module, $"Set{typeName}Height", type, "Height", [("handle", "int"), ("value", "int")]);
        yield return Release(module, $"Release{typeName}", type);
    }

    private static Binding Static(string module, string name, Type type, string member, Type[] clrParameters, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.StaticMethod, type, member, clrParameters, parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding Constructor(string module, string name, Type type, Type[] clrParameters, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.Constructor, type, ".ctor", clrParameters, parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding Instance(string module, string name, Type type, string member, Type[] clrParameters, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.InstanceMethod, type, member, clrParameters, parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding PropertyGet(string module, string name, Type type, string property, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.InstancePropertyGet, type, property, [], parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding PropertySet(string module, string name, Type type, string property, (string Name, string Schema)[] parameters)
    {
        var propertyType = type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.PropertyType ??
            throw new InvalidOperationException($"property {type.FullName}.{property} was not found");
        return new(module, name, PdVmDotNetMemberKind.InstancePropertySet, type, property, [propertyType], parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), "null");
    }

    private static Binding Release(string module, string name, Type type) =>
        new(module, name, PdVmDotNetMemberKind.Release, type, "Release", [], [new Parameter("handle", "int")], "bool");

    private static Type ResolveLoadedType(PdVmDotNetHost host, string name)
    {
        if (!host.CanResolveType(name))
        {
            throw new InvalidOperationException($"CLR type '{name}' was not found");
        }
        return AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, throwOnError: false, ignoreCase: false))
            .First(type => type is not null)!;
    }

    private static Dictionary<string, PdVmDotNetBindingDescriptor> WriteBindingModules(
        string root,
        IReadOnlyList<Binding> bindings)
    {
        var importMap = new Dictionary<string, PdVmDotNetBindingDescriptor>(StringComparer.Ordinal);
        foreach (var group in bindings.GroupBy(binding => binding.ModulePath, StringComparer.Ordinal))
        {
            var path = Path.Combine(root, group.Key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"source overlay already contains reserved interop module '{group.Key}'");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var source = new StringBuilder();
            source.AppendLine("// Generated by PdVmDotNetSourceCompiler. Do not edit.");
            source.AppendLine();
            foreach (var binding in group.OrderBy(item => item.PublicName, StringComparer.Ordinal))
            {
                ValidateBinding(binding);
                var descriptor = CreateDescriptor(binding);
                var internalName = InternalName(descriptor);
                if (!importMap.TryAdd(internalName, descriptor))
                {
                    throw new InvalidOperationException($"duplicate CLR binding identifier '{internalName}'");
                }
                EmitBinding(source, binding, internalName);
            }
            File.WriteAllText(path, source.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        return importMap;
    }

    private static void ValidateBinding(Binding binding)
    {
        _ = (object)(binding.Kind switch
        {
            PdVmDotNetMemberKind.StaticMethod => binding.DeclaringType.GetMethod(
                binding.MemberName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                binding.ClrParameterTypes,
                modifiers: null) ?? throw Missing(binding),
            PdVmDotNetMemberKind.Constructor => binding.DeclaringType.GetConstructor(binding.ClrParameterTypes) ?? throw Missing(binding),
            PdVmDotNetMemberKind.InstanceMethod => binding.DeclaringType.GetMethod(
                binding.MemberName,
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                binding.ClrParameterTypes,
                modifiers: null) ?? throw Missing(binding),
            PdVmDotNetMemberKind.InstancePropertyGet or PdVmDotNetMemberKind.InstancePropertySet =>
                binding.DeclaringType.GetProperty(binding.MemberName, BindingFlags.Public | BindingFlags.Instance) ?? throw Missing(binding),
            PdVmDotNetMemberKind.Release => binding.DeclaringType,
            _ => throw new InvalidOperationException($"unsupported binding kind {binding.Kind}"),
        });
    }

    private static InvalidOperationException Missing(Binding binding) =>
        new($"CLR binding member {binding.DeclaringType.FullName}.{binding.MemberName} was not found");

    private static PdVmDotNetBindingDescriptor CreateDescriptor(Binding binding)
    {
        var returnType = binding.Kind switch
        {
            PdVmDotNetMemberKind.StaticMethod or PdVmDotNetMemberKind.InstanceMethod =>
                binding.DeclaringType.GetMethod(
                    binding.MemberName,
                    BindingFlags.Public | (binding.Kind == PdVmDotNetMemberKind.StaticMethod ? BindingFlags.Static : BindingFlags.Instance),
                    binder: null,
                    binding.ClrParameterTypes,
                    modifiers: null)!.ReturnType,
            PdVmDotNetMemberKind.Constructor => binding.DeclaringType,
            PdVmDotNetMemberKind.InstancePropertyGet => binding.DeclaringType.GetProperty(binding.MemberName)!.PropertyType,
            PdVmDotNetMemberKind.InstancePropertySet => typeof(void),
            PdVmDotNetMemberKind.Release => typeof(bool),
            _ => typeof(void),
        };
        return new PdVmDotNetBindingDescriptor(
            binding.DeclaringType.Assembly.FullName!,
            binding.DeclaringType.Assembly.ManifestModule.ModuleVersionId,
            binding.DeclaringType.FullName!,
            binding.MemberName,
            binding.Kind,
            binding.ClrParameterTypes.Select(type => type.AssemblyQualifiedName!).ToArray(),
            returnType.AssemblyQualifiedName!);
    }

    private static string InternalName(PdVmDotNetBindingDescriptor descriptor)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.EncodeImportName()));
        return $"__clr_b_{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static void EmitBinding(StringBuilder source, Binding binding, string internalName)
    {
        var parameters = string.Join(", ", binding.Parameters.Select(item => $"{item.Name}: {item.Schema}"));
        var arguments = string.Join(", ", binding.Parameters.Select(item => item.Name));
        source.Append("pub fn ").Append(internalName).Append('(').Append(parameters).Append(')')
            .Append(" -> ").Append(binding.ReturnSchema).AppendLine(";");
        source.Append("pub fn ").Append(binding.PublicName).Append('(').Append(parameters).Append(')')
            .Append(" -> ").Append(binding.ReturnSchema).AppendLine(" {");
        source.Append("    ");
        if (binding.ReturnSchema == "null")
        {
            source.Append(internalName).Append('(').Append(arguments).AppendLine(");");
        }
        else
        {
            source.Append(internalName).Append('(').Append(arguments).AppendLine(")");
        }
        source.AppendLine("}").AppendLine();
    }

    private static PdVmProgramModel RemapImports(
        PdVmProgramModel model,
        IReadOnlyDictionary<string, PdVmDotNetBindingDescriptor> importMap)
    {
        var imports = model.Imports.Select(import =>
        {
            if (!import.Name.StartsWith("__clr_b_", StringComparison.Ordinal))
            {
                return import;
            }
            if (!importMap.TryGetValue(import.Name, out var descriptor))
            {
                throw new PdVmCompilerException($"unknown generated CLR import '{import.Name}'");
            }
            return new PdVmHostImport(descriptor.EncodeImportName(), import.Arity, import.ReturnType);
        }).ToArray();
        return new PdVmProgramModel(
            model.Constants,
            model.Code,
            model.LocalCount,
            imports,
            model.Instructions,
            model.TypeMap);
    }

    private static void CopySourceOverlay(string sourceRoot, string destinationRoot)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*.rss", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void RunRustScriptCompiler(string compilerPath, string sourcePath, string vmbcPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--emit-vmbc");
        process.StartInfo.ArgumentList.Add(vmbcPath);
        process.StartInfo.ArgumentList.Add(sourcePath);
        if (!process.Start())
        {
            throw new InvalidOperationException("failed to start the RustScript compiler");
        }
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new PdVmCompilerException(
                $"RustScript compilation failed with exit code {process.ExitCode}:{Environment.NewLine}{stdout}{stderr}".Trim());
        }
    }

    private static string FindRustScriptCompiler(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }
        var environmentPath = Environment.GetEnvironmentVariable("RUSTSCRIPT_COMPILER");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            return Path.GetFullPath(environmentPath);
        }

        var fileName = OperatingSystem.IsWindows() ? "pd-vm-run.exe" : "pd-vm-run";
        foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "rustscript", "target", "debug", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                var sibling = Path.Combine(directory.FullName, "..", "rustscript", "target", "debug", fileName);
                if (File.Exists(sibling))
                {
                    return Path.GetFullPath(sibling);
                }
                directory = directory.Parent;
            }
        }
        return fileName;
    }

    private static bool IsPathWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
