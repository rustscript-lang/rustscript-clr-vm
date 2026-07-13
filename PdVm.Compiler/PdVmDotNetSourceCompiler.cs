using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
    public string? NativeCompilerLibraryPath { get; init; }

    public string? SourceRoot { get; init; }

    public PdVmDotNetInteropProfile Profile { get; init; } = PdVmDotNetInteropProfile.Common;

    public bool KeepTemporaryFiles { get; init; }

}

public static class PdVmDotNetSourceCompiler
{
    private static readonly ConcurrentDictionary<Type, Binding[]> MetadataBindingCache = new();

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

    private sealed record SystemImport(
        string SourcePath,
        int Line,
        string Path,
        MemberUse[] UsedMembers)
    {
        public string TypeName => Path.Replace("::", ".", StringComparison.Ordinal);

        public string ModulePath => Path.Replace("::", "/", StringComparison.Ordinal) + ".rss";

        public string DisplayLocation => $"{SourcePath}:{Line}";
    }

    private sealed record MemberUse(string Name, int Line);

    private sealed record ResolvedSystemImport(SystemImport Import, Type Type, bool IsExternal);

    private static readonly Regex SystemUsePattern = new(
        @"^\s*use\s+(?<path>System(?:::[A-Za-z_][A-Za-z0-9_]*)+)(?:\s+as\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*))?\s*;",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex UsePattern = new(
        @"^\s*use\s+(?<path>[A-Za-z_][A-Za-z0-9_]*(?:::[A-Za-z_][A-Za-z0-9_]*)*)(?:\s+as\s+[A-Za-z_][A-Za-z0-9_]*)?\s*;",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

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

        var systemImports = ScanSystemImports(sourceRoot, fullSourcePath);
        var resolvedImports = ResolveSystemImports(
            systemImports,
            sourceRoot);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-dotnet-source",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            CopySourceOverlay(sourceRoot, temporaryRoot);
            var bindings = BuildBindings(resolvedImports);
            var importMap = WriteBindingModules(temporaryRoot, bindings);
            var relativeSource = Path.GetRelativePath(sourceRoot, fullSourcePath);
            var overlaySource = Path.Combine(temporaryRoot, relativeSource);
            var vmbc = PdVmNativeCompiler.CompileFile(
                overlaySource,
                options.NativeCompilerLibraryPath);
            var model = RemapImports(PdVmVmbcReader.ReadBytes(vmbc), importMap);
            return PdVmClrCompiler.Compile(
                model,
                outputPath,
                MergeCompileOptions(clrOptions, bindings, resolvedImports));
        }
        finally
        {
            if (!options.KeepTemporaryFiles)
            {
                TryDeleteDirectory(temporaryRoot);
            }
        }
    }

    private static IReadOnlyList<Binding> BuildBindings(
        IReadOnlyList<ResolvedSystemImport> resolvedImports)
    {
        var bindings = new List<Binding>();
        foreach (var group in resolvedImports
                     .GroupBy(item => item.Import.ModulePath, StringComparer.Ordinal)
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var resolved = group.First();
            var generated = BuildMetadataBindings(resolved.Import.ModulePath, resolved.Type).ToArray();
            if (generated.Length == 0)
            {
                throw new PdVmCompilerException(
                    $"CLR import '{resolved.Import.Path}' at {resolved.Import.DisplayLocation} exposes no supported public members. " +
                    "Members with generic, ref, pointer, or byref-like parameters are not available to RustScript.");
            }
            var requestedNames = group
                .SelectMany(item => item.Import.UsedMembers)
                .Select(member => member.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (requestedNames.Count == 0)
            {
                bindings.AddRange(generated);
                continue;
            }
            var selected = generated
                .Where(binding => requestedNames.Contains(binding.PublicName))
                .ToArray();
            var missingNames = requestedNames
                .Except(selected.Select(binding => binding.PublicName), StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (missingNames.Length != 0)
            {
                var missing = group
                    .SelectMany(item => item.Import.UsedMembers.Select(member => (item.Import, Member: member)))
                    .First(item => missingNames.Contains(item.Member.Name, StringComparer.Ordinal));
                throw new PdVmCompilerException(
                    $"CLR metadata call at {missing.Import.SourcePath}:{missing.Member.Line}: type '{resolved.Type.FullName}' " +
                    $"has no supported metadata member named {string.Join(", ", missingNames.Select(name => $"'{name}'"))}. " +
                    "Use the generated overload suffix when a CLR member has multiple supported signatures.");
            }
            bindings.AddRange(selected);
        }
        return bindings;
    }

    private static IReadOnlyList<SystemImport> ScanSystemImports(string sourceRoot, string entrySourcePath)
    {
        var imports = new List<SystemImport>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        void ScanSource(string sourcePath)
        {
            if (!visited.Add(Path.GetFullPath(sourcePath)))
            {
                return;
            }
            var text = File.ReadAllText(sourcePath);
            foreach (Match match in SystemUsePattern.Matches(text))
            {
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                var path = match.Groups["path"].Value;
                var alias = match.Groups["alias"].Success
                    ? match.Groups["alias"].Value
                    : path[(path.LastIndexOf("::", StringComparison.Ordinal) + 2)..];
                var usedMembers = Regex.Matches(
                        text,
                        $@"\b{Regex.Escape(alias)}::(?<member>[A-Za-z_][A-Za-z0-9_]*)\b",
                        RegexOptions.CultureInvariant)
                    .Select(call => new MemberUse(
                        call.Groups["member"].Value,
                        text.AsSpan(0, call.Index).Count('\n') + 1))
                    .DistinctBy(member => member.Name, StringComparer.Ordinal)
                    .ToArray();
                imports.Add(new SystemImport(
                    Path.GetRelativePath(sourceRoot, sourcePath),
                    line,
                    path,
                    usedMembers));
            }

            foreach (Match match in UsePattern.Matches(text))
            {
                var modulePath = match.Groups["path"].Value;
                if (modulePath == "System" || modulePath.StartsWith("System::", StringComparison.Ordinal))
                {
                    continue;
                }
                var dependency = ResolveModuleSourcePath(sourceRoot, sourcePath, modulePath);
                if (dependency is not null)
                {
                    ScanSource(dependency);
                }
            }
        }

        ScanSource(entrySourcePath);
        return imports;
    }

    private static string? ResolveModuleSourcePath(string sourceRoot, string importerPath, string importPath)
    {
        var segments = importPath.Split("::", StringSplitOptions.RemoveEmptyEntries);
        var directory = Path.GetDirectoryName(importerPath)!;
        var index = 0;
        while (index < segments.Length && (segments[index] == "self" || segments[index] == "super"))
        {
            if (segments[index] == "super")
            {
                directory = Directory.GetParent(directory)?.FullName ?? directory;
            }
            index++;
        }
        if (index == 0)
        {
            directory = sourceRoot;
        }
        if (index >= segments.Length)
        {
            return null;
        }

        var candidate = Path.Combine(directory, Path.Combine(segments[index..]) + ".rss");
        return File.Exists(candidate) ? candidate : null;
    }

    private static IReadOnlyList<ResolvedSystemImport> ResolveSystemImports(
        IReadOnlyList<SystemImport> imports,
        string sourceRoot)
    {
        if (imports.Count == 0)
        {
            return [];
        }

        var runtimeResolver = new PdVmDotNetHost(allowDynamicSystemCalls: true);
        foreach (var import in imports)
        {
            _ = runtimeResolver.CanResolveType(import.TypeName);
        }

        var externalPaths = DiscoverExternalAssemblyPaths(sourceRoot);
        var assemblies = DiscoverAssemblies(externalPaths);
        var attributedTypes = DiscoverAttributedInteropTypes(assemblies);
        var resolved = new List<ResolvedSystemImport>();
        foreach (var import in imports)
        {
            var type = attributedTypes.GetValueOrDefault(import.TypeName) ??
                assemblies
                    .Select(assembly => FindClrType(assembly, import.TypeName))
                    .FirstOrDefault(candidate => candidate is not null);
            if (type is null)
            {
                var isNamespace = assemblies.Any(assembly => SafeExportedTypes(assembly)
                    .Any(candidate => string.Equals(candidate.Namespace, import.TypeName, StringComparison.Ordinal)));
                var reason = isNamespace
                    ? $"'{import.Path}' names a CLR namespace, not a concrete type. Import a type such as '{import.Path}::TypeName'."
                    : $"CLR type '{import.TypeName}' was not found.";
                throw new PdVmCompilerException(
                    $"Unable to resolve CLR import at {import.DisplayLocation}: {reason} " +
                    "Searched the .NET runtime, the source directory, the Runner directory, and the current working directory for managed assemblies.");
            }

            resolved.Add(new ResolvedSystemImport(
                import,
                type,
                !string.IsNullOrWhiteSpace(type.Assembly.Location) &&
                externalPaths.Contains(Path.GetFullPath(type.Assembly.Location), StringComparer.OrdinalIgnoreCase)));
        }
        return resolved;
    }

    private static IReadOnlyDictionary<string, Type> DiscoverAttributedInteropTypes(
        IEnumerable<Assembly> assemblies)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in assemblies.SelectMany(SafeExportedTypes))
        {
            var attribute = type.GetCustomAttribute<PdVmInteropTypeAttribute>();
            if (attribute is null)
            {
                continue;
            }
            if (!result.TryAdd(attribute.TypeName, type))
            {
                throw new InvalidOperationException(
                    $"multiple CLR types declare interop name '{attribute.TypeName}'");
            }
        }
        return result;
    }

    private static Type? FindClrType(Assembly assembly, string typeName)
    {
        var resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (resolved is not null)
        {
            return resolved;
        }

        var separator = typeName.LastIndexOf('.');
        while (separator > 0)
        {
            var nestedName = typeName[..separator] + "+" + typeName[(separator + 1)..];
            resolved = assembly.GetType(nestedName, throwOnError: false, ignoreCase: false);
            if (resolved is not null)
            {
                return resolved;
            }
            separator = typeName.LastIndexOf('.', separator - 1);
        }

        return null;
    }

    private static IReadOnlyList<string> DiscoverExternalAssemblyPaths(string sourceRoot)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        foreach (var directory in new[]
                 {
                     sourceRoot,
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory,
                 }
                 .Where(Directory.Exists)
                 .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            var searchOption = string.Equals(directory, sourceRoot, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            foreach (var assemblyPath in Directory.EnumerateFiles(directory, "*.dll", searchOption))
            {
                paths.Add(Path.GetFullPath(assemblyPath));
            }
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<Assembly> DiscoverAssemblies(IReadOnlyList<string> externalPaths)
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            assemblies[assembly.FullName!] = assembly;
        }

        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        foreach (var path in (trustedPlatformAssemblies ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            TryAddAssembly(assemblies, path, isExternal: false);
        }
        foreach (var path in externalPaths)
        {
            TryAddAssembly(assemblies, path, isExternal: true);
        }
        return assemblies.Values.ToArray();
    }

    private static void TryAddAssembly(Dictionary<string, Assembly> assemblies, string path, bool isExternal)
    {
        try
        {
            var name = AssemblyName.GetAssemblyName(path);
            if (assemblies.ContainsKey(name.FullName!))
            {
                return;
            }

            var assembly = isExternal
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path))
                : Assembly.Load(name);
            assemblies[assembly.FullName!] = assembly;
        }
        catch (Exception exception) when (exception is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            // A non-managed DLL near the source is not a CLR reference candidate.
        }
    }

    private static IEnumerable<Type> SafeExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>();
        }
    }

    private static IEnumerable<Binding> BuildMetadataBindings(string modulePath, Type type)
    {
        return MetadataBindingCache.GetOrAdd(
                type,
                static resolvedType => ScanMetadataBindings(resolvedType).ToArray())
            .Select(binding => binding with { ModulePath = modulePath });
    }

    private static IEnumerable<Binding> ScanMetadataBindings(Type type)
    {
        var bindings = new List<Binding>();
        var typeName = InteropTypeName(type);
        if (!type.IsAbstract || !type.IsSealed)
        {
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (TryBuildParameters(constructor.GetParameters(), includeHandle: false, out var parameterTypes, out var parameters))
                {
                    bindings.Add(Constructor(string.Empty, $"New{typeName}", type, parameterTypes, parameters, "int"));
                }
            }
        }

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                     .Where(method => !method.IsSpecialName && !method.ContainsGenericParameters && method.DeclaringType != typeof(object)))
        {
            if (!TryGetSchema(method.ReturnType, out var returnSchema) ||
                !TryBuildParameters(method.GetParameters(), !method.IsStatic, out var parameterTypes, out var parameters))
            {
                continue;
            }
            bindings.Add(method.IsStatic
                ? Static(string.Empty, method.Name, type, method.Name, parameterTypes, parameters, returnSchema)
                : Instance(string.Empty, method.Name, type, method.Name, parameterTypes, parameters, returnSchema));
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetIndexParameters().Length == 0))
        {
            if (property.GetMethod is not null && TryGetSchema(property.PropertyType, out var getSchema))
            {
                bindings.Add(PropertyGet(
                    string.Empty,
                    $"Get{typeName}{property.Name}",
                    type,
                    property.Name,
                    [("handle", "int")],
                    getSchema));
            }
            if (property.SetMethod is not null && TryGetSchema(property.PropertyType, out var setSchema))
            {
                bindings.Add(new Binding(
                    string.Empty,
                    $"Set{typeName}{property.Name}",
                    PdVmDotNetMemberKind.InstancePropertySet,
                    type,
                    property.Name,
                    [property.PropertyType],
                    [new Parameter("handle", "int"), new Parameter("value", setSchema)],
                    "null"));
            }
        }

        if (!type.IsValueType && !(type.IsAbstract && type.IsSealed))
        {
            bindings.Add(Release(string.Empty, $"Release{typeName}", type));
        }
        return AssignGeneratedNames(bindings.DistinctBy(BindingIdentity));
    }

    private static string BindingIdentity(Binding binding) =>
        $"{binding.Kind}|{binding.MemberName}|" +
        string.Join("|", binding.ClrParameterTypes.Select(type => type.AssemblyQualifiedName));

    private static string InteropTypeName(Type type)
    {
        var declaredName = type.GetCustomAttribute<PdVmInteropTypeAttribute>()?.TypeName;
        var name = declaredName is null
            ? type.Name
            : declaredName[(declaredName.LastIndexOf('.') + 1)..];
        var genericMarker = name.IndexOf('`');
        return genericMarker < 0 ? name : name[..genericMarker];
    }

    private static bool TryBuildParameters(
        ParameterInfo[] clrParameters,
        bool includeHandle,
        out Type[] parameterTypes,
        out (string Name, string Schema)[] parameters)
    {
        parameterTypes = clrParameters.Select(parameter => parameter.ParameterType).ToArray();
        var generated = new List<(string Name, string Schema)>();
        if (includeHandle)
        {
            generated.Add(("handle", "int"));
        }
        for (var index = 0; index < clrParameters.Length; index++)
        {
            if (clrParameters[index].IsOut || !TryGetSchema(clrParameters[index].ParameterType, out var schema))
            {
                parameters = [];
                return false;
            }
            generated.Add(($"arg{index}", schema));
        }
        parameters = generated.ToArray();
        return true;
    }

    private static bool TryGetSchema(Type type, out string schema)
    {
        if (type == typeof(void)) { schema = "null"; return true; }
        if (type == typeof(string) || type == typeof(char) || type.IsEnum) { schema = "string"; return true; }
        if (type == typeof(bool)) { schema = "bool"; return true; }
        if (type == typeof(byte[])) { schema = "bytes"; return true; }
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) { schema = "float"; return true; }
        if (type is { IsPointer: true } || type.IsByRef || type.IsByRefLike || type.ContainsGenericParameters)
        {
            schema = string.Empty;
            return false;
        }
        if (type.IsArray && type.GetArrayRank() == 1 && TryGetSchema(type.GetElementType()!, out var elementSchema))
        {
            schema = $"[{elementSchema}]";
            return true;
        }
        schema = "int";
        return true;
    }

    private static IEnumerable<Binding> AssignGeneratedNames(IEnumerable<Binding> bindings)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in bindings.GroupBy(binding => binding.PublicName, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(binding => binding.ClrParameterTypes.Length)
                .ThenBy(
                    binding => string.Join("_", binding.ClrParameterTypes.Select(type => type.AssemblyQualifiedName)),
                    StringComparer.Ordinal)
                .ThenBy(binding => binding.Kind)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                var name = index == 0
                    ? ordered[index].PublicName
                    : ordered[index].PublicName + string.Concat(ordered[index].ClrParameterTypes.Select(TypeSuffix));
                if (ordered[index].Kind == PdVmDotNetMemberKind.InstanceMethod && index > 0)
                {
                    name += "Instance";
                }
                var uniqueName = name;
                var duplicate = 2;
                while (!names.Add(uniqueName))
                {
                    uniqueName = name + duplicate++;
                }
                yield return ordered[index] with { PublicName = uniqueName };
            }
        }
    }

    private static string TypeSuffix(Type type)
    {
        if (type == typeof(byte[])) return "Bytes";
        if (type == typeof(string)) return "String";
        if (type == typeof(bool)) return "Bool";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "Float";
        if (type.IsArray) return "Array";
        var name = type.Name.Replace("`", "", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(name) ? "Value" : name;
    }

    private static PdVmCompileOptions MergeCompileOptions(
        PdVmCompileOptions? original,
        IReadOnlyList<Binding> bindings,
        IReadOnlyList<ResolvedSystemImport> resolvedImports)
    {
        original ??= new PdVmCompileOptions();
        var externalAssemblies = resolvedImports
            .Where(import => import.IsExternal && !string.IsNullOrWhiteSpace(import.Type.Assembly.Location))
            .Select(import => import.Type.Assembly.Location)
            .Concat(original.AdditionalRuntimeAssemblyPaths)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        return new PdVmCompileOptions
        {
            AssemblyName = original.AssemblyName,
            ModuleName = original.ModuleName,
            TypeName = original.TypeName,
            CopyRuntimeAssembly = original.CopyRuntimeAssembly,
            ReferencedClrTypes = bindings.Select(binding => binding.DeclaringType)
                .Concat(original.ReferencedClrTypes)
                .Distinct()
                .ToArray(),
            AdditionalRuntimeAssemblyPaths = externalAssemblies,
        };
    }

    private static Binding Static(string module, string name, Type type, string member, Type[] clrParameters, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.StaticMethod, type, member, clrParameters, parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding Constructor(string module, string name, Type type, Type[] clrParameters, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.Constructor, type, ".ctor", clrParameters, parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding Instance(string module, string name, Type type, string member, Type[] clrParameters, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.InstanceMethod, type, member, clrParameters, parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding PropertyGet(string module, string name, Type type, string property, (string Name, string Schema)[] parameters, string result) =>
        new(module, name, PdVmDotNetMemberKind.InstancePropertyGet, type, property, [], parameters.Select(item => new Parameter(item.Name, item.Schema)).ToArray(), result);

    private static Binding Release(string module, string name, Type type) =>
        new(module, name, PdVmDotNetMemberKind.Release, type, "Release", [], [new Parameter("handle", "int")], "bool");

    private static Dictionary<string, PdVmDotNetBindingDescriptor> WriteBindingModules(
        string root,
        IReadOnlyList<Binding> bindings)
    {
        var importMap = new Dictionary<string, PdVmDotNetBindingDescriptor>(StringComparer.Ordinal);
        foreach (var group in bindings.GroupBy(binding => binding.ModulePath, StringComparer.Ordinal))
        {
            var duplicate = group.GroupBy(binding => binding.PublicName, StringComparer.Ordinal)
                .FirstOrDefault(names => names.Count() > 1);
            if (duplicate is not null)
            {
                throw new PdVmCompilerException(
                    $"generated CLR module '{group.Key}' contains duplicate member '{duplicate.Key}'");
            }
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
                    var existing = importMap[internalName];
                    throw new InvalidOperationException(
                        $"duplicate CLR binding identifier '{internalName}' for " +
                        $"{existing.TypeName}.{existing.MemberName} and {descriptor.TypeName}.{descriptor.MemberName}");
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
