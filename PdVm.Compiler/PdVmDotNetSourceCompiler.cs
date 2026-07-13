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

    private sealed record SystemImport(string SourcePath, int Line, string Path)
    {
        public string TypeName => Path.Replace("::", ".", StringComparison.Ordinal);

        public string ModulePath => Path.Replace("::", "/", StringComparison.Ordinal) + ".rss";

        public string DisplayLocation => $"{SourcePath}:{Line}";
    }

    private sealed record ResolvedSystemImport(SystemImport Import, Type Type, bool IsExternal);

    private static readonly Regex SystemUsePattern = new(
        @"^\s*use\s+(?<path>System(?:::[A-Za-z_][A-Za-z0-9_]*)+)(?:\s+as\s+[A-Za-z_][A-Za-z0-9_]*)?\s*;",
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
            sourceRoot,
            options.Profile);

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-dotnet-source",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            CopySourceOverlay(sourceRoot, temporaryRoot);
            var bindings = BuildBindings(options.Profile, resolvedImports);
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

    internal static IReadOnlyDictionary<string, string> GenerateModules(
        string outputRoot,
        PdVmDotNetInteropProfile profile)
    {
        Directory.CreateDirectory(outputRoot);
        return WriteBindingModules(outputRoot, BuildBindings(profile, []))
            .ToDictionary(pair => pair.Key, pair => pair.Value.EncodeImportName(), StringComparer.Ordinal);
    }

    private static IReadOnlyList<Binding> BuildBindings(
        PdVmDotNetInteropProfile profile,
        IReadOnlyList<ResolvedSystemImport> resolvedImports)
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

        foreach (var resolved in resolvedImports
                     .GroupBy(item => item.Import.ModulePath, StringComparer.Ordinal)
                     .Select(group => group.First())
                     .OrderBy(item => item.Import.ModulePath, StringComparer.Ordinal))
        {
            if (bindings.Any(binding =>
                    string.Equals(binding.ModulePath, resolved.Import.ModulePath, StringComparison.Ordinal)))
            {
                continue;
            }

            var generated = BuildMetadataBindings(resolved.Import.ModulePath, resolved.Type).ToArray();
            if (generated.Length == 0)
            {
                throw new PdVmCompilerException(
                    $"CLR import '{resolved.Import.Path}' at {resolved.Import.DisplayLocation} exposes no supported public members. " +
                    "Members with generic, ref, pointer, or byref-like parameters are not available to RustScript.");
            }
            bindings.AddRange(generated);
        }
        return bindings;
    }

    private static IEnumerable<Binding> BuildCommonBindings()
    {
        yield return Static("System/Console.rss", "WriteLine", typeof(Console), nameof(Console.WriteLine), [typeof(string)], [("value", "string")], "null");
        yield return Static("System/Console.rss", "WriteLineInt", typeof(Console), nameof(Console.WriteLine), [typeof(long)], [("value", "int")], "null");
        yield return Static("System/Math.rss", "Sqrt", typeof(Math), nameof(Math.Sqrt), [typeof(double)], [("value", "float")], "float");
        yield return Static("System/Math.rss", "AbsInt", typeof(Math), nameof(Math.Abs), [typeof(long)], [("value", "int")], "int");
        yield return Static("System/Math.rss", "AbsFloat", typeof(Math), nameof(Math.Abs), [typeof(double)], [("value", "float")], "float");
        yield return Static("System/IO/Path.rss", "GetFileName", typeof(Path), nameof(Path.GetFileName), [typeof(string)], [("path", "string")], "string");
        yield return Static("System/IO/Path.rss", "Combine", typeof(Path), nameof(Path.Combine), [typeof(string), typeof(string)], [("left", "string"), ("right", "string")], "string");
        yield return Static("System/IO/File.rss", "Exists", typeof(File), nameof(File.Exists), [typeof(string)], [("path", "string")], "bool");
        yield return Static("System/IO/File.rss", "ReadAllText", typeof(File), nameof(File.ReadAllText), [typeof(string)], [("path", "string")], "string");
        yield return Static("System/IO/File.rss", "WriteAllText", typeof(File), nameof(File.WriteAllText), [typeof(string), typeof(string)], [("path", "string"), ("content", "string")], "null");

        var stringBuilder = typeof(StringBuilder);
        yield return Constructor("System/Text/StringBuilder.rss", "New", stringBuilder, [], [], "int");
        yield return Instance("System/Text/StringBuilder.rss", "Append", stringBuilder, nameof(StringBuilder.Append), [typeof(string)], [("handle", "int"), ("value", "string")], "int");
        yield return Instance("System/Text/StringBuilder.rss", "ToString", stringBuilder, nameof(StringBuilder.ToString), [], [("handle", "int")], "string");
        yield return PropertyGet("System/Text/StringBuilder.rss", "GetLength", stringBuilder, nameof(StringBuilder.Length), [("handle", "int")], "int");
        yield return Release("System/Text/StringBuilder.rss", "Release", stringBuilder);
    }

    private static IEnumerable<Binding> BuildWindowsFormsBindings()
    {
        var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);
        var form = ResolveLoadedType(host, "System.Windows.Forms.Form");
        var richTextBox = ResolveLoadedType(host, "System.Windows.Forms.RichTextBox");
        var menuStrip = ResolveLoadedType(host, "System.Windows.Forms.MenuStrip");
        var menuItem = ResolveLoadedType(host, "System.Windows.Forms.ToolStripMenuItem");
        var separator = ResolveLoadedType(host, "System.Windows.Forms.ToolStripSeparator");
        var statusStrip = ResolveLoadedType(host, "System.Windows.Forms.StatusStrip");
        var statusLabel = ResolveLoadedType(host, "System.Windows.Forms.ToolStripStatusLabel");
        var control = ResolveLoadedType(host, "System.Windows.Forms.Control");
        var controlCollection = ResolveLoadedType(host, "System.Windows.Forms.Control+ControlCollection");
        var itemCollection = ResolveLoadedType(host, "System.Windows.Forms.ToolStripItemCollection");
        var toolStripItem = ResolveLoadedType(host, "System.Windows.Forms.ToolStripItem");
        var openDialog = ResolveLoadedType(host, "System.Windows.Forms.OpenFileDialog");
        var saveDialog = ResolveLoadedType(host, "System.Windows.Forms.SaveFileDialog");
        var fontDialog = ResolveLoadedType(host, "System.Windows.Forms.FontDialog");
        var colorDialog = ResolveLoadedType(host, "System.Windows.Forms.ColorDialog");
        var window = ResolveLoadedType(host, "System.Windows.Forms.IWin32Window");

        yield return Constructor("System/Windows/Forms/Form.rss", "NewForm", form, [], [], "int");
        yield return PropertySet("System/Windows/Forms/Form.rss", "SetFormText", form, "Text", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("System/Windows/Forms/Form.rss", "SetFormWidth", form, "Width", [("handle", "int"), ("value", "int")]);
        yield return PropertySet("System/Windows/Forms/Form.rss", "SetFormHeight", form, "Height", [("handle", "int"), ("value", "int")]);
        yield return PropertySet("System/Windows/Forms/Form.rss", "SetFormStartPosition", form, "StartPosition", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("System/Windows/Forms/Form.rss", "SetFormMainMenuStrip", form, "MainMenuStrip", [("handle", "int"), ("menu", "int")]);
        yield return PropertyGet("System/Windows/Forms/Form.rss", "GetFormControls", form, "Controls", [("handle", "int")], "int");
        yield return Instance("System/Windows/Forms/Form.rss", "ShowForm", form, "Show", [], [("handle", "int")], "null");
        yield return Release("System/Windows/Forms/Form.rss", "ReleaseForm", form);

        yield return Constructor("System/Windows/Forms/RichTextBox.rss", "NewRichTextBox", richTextBox, [], [], "int");
        yield return PropertySet("System/Windows/Forms/RichTextBox.rss", "SetRichTextBoxText", richTextBox, "Text", [("handle", "int"), ("value", "string")]);
        yield return PropertyGet("System/Windows/Forms/RichTextBox.rss", "GetRichTextBoxText", richTextBox, "Text", [("handle", "int")], "string");
        yield return PropertySet("System/Windows/Forms/RichTextBox.rss", "SetRichTextBoxDock", richTextBox, "Dock", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("System/Windows/Forms/RichTextBox.rss", "SetRichTextBoxBorderStyle", richTextBox, "BorderStyle", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("System/Windows/Forms/RichTextBox.rss", "SetRichTextBoxWordWrap", richTextBox, "WordWrap", [("handle", "int"), ("value", "bool")]);
        yield return PropertySet("System/Windows/Forms/RichTextBox.rss", "SetRichTextBoxFont", richTextBox, "Font", [("handle", "int"), ("font", "int")]);
        yield return PropertySet("System/Windows/Forms/RichTextBox.rss", "SetRichTextBoxForeColor", richTextBox, "ForeColor", [("handle", "int"), ("color", "int")]);
        yield return Release("System/Windows/Forms/RichTextBox.rss", "ReleaseRichTextBox", richTextBox);

        yield return Constructor("System/Windows/Forms/MenuStrip.rss", "NewMenuStrip", menuStrip, [], [], "int");
        yield return PropertySet("System/Windows/Forms/MenuStrip.rss", "SetMenuStripDock", menuStrip, "Dock", [("handle", "int"), ("value", "string")]);
        yield return PropertyGet("System/Windows/Forms/MenuStrip.rss", "GetMenuStripItems", menuStrip, "Items", [("handle", "int")], "int");
        yield return Release("System/Windows/Forms/MenuStrip.rss", "ReleaseMenuStrip", menuStrip);

        yield return Constructor("System/Windows/Forms/ToolStripMenuItem.rss", "NewToolStripMenuItem", menuItem, [typeof(string)], [("text", "string")], "int");
        yield return PropertySet("System/Windows/Forms/ToolStripMenuItem.rss", "SetToolStripMenuItemShortcutKeys", menuItem, "ShortcutKeys", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("System/Windows/Forms/ToolStripMenuItem.rss", "SetToolStripMenuItemChecked", menuItem, "Checked", [("handle", "int"), ("value", "bool")]);
        yield return Instance("System/Windows/Forms/ToolStripMenuItem.rss", "PerformToolStripMenuItemClick", menuItem, "PerformClick", [], [("handle", "int")], "null");
        yield return PropertyGet("System/Windows/Forms/ToolStripMenuItem.rss", "GetToolStripMenuItemItems", menuItem, "DropDownItems", [("handle", "int")], "int");
        yield return Release("System/Windows/Forms/ToolStripMenuItem.rss", "ReleaseToolStripMenuItem", menuItem);

        yield return Constructor("System/Windows/Forms/ToolStripSeparator.rss", "NewToolStripSeparator", separator, [], [], "int");
        yield return Release("System/Windows/Forms/ToolStripSeparator.rss", "ReleaseToolStripSeparator", separator);

        yield return Constructor("System/Windows/Forms/StatusStrip.rss", "NewStatusStrip", statusStrip, [], [], "int");
        yield return PropertySet("System/Windows/Forms/StatusStrip.rss", "SetStatusStripDock", statusStrip, "Dock", [("handle", "int"), ("value", "string")]);
        yield return PropertyGet("System/Windows/Forms/StatusStrip.rss", "GetStatusStripItems", statusStrip, "Items", [("handle", "int")], "int");
        yield return Release("System/Windows/Forms/StatusStrip.rss", "ReleaseStatusStrip", statusStrip);

        yield return Constructor("System/Windows/Forms/ToolStripStatusLabel.rss", "NewToolStripStatusLabel", statusLabel, [], [], "int");
        yield return PropertySet("System/Windows/Forms/ToolStripStatusLabel.rss", "SetToolStripStatusLabelText", statusLabel, "Text", [("handle", "int"), ("value", "string")]);
        yield return PropertySet("System/Windows/Forms/ToolStripStatusLabel.rss", "SetToolStripStatusLabelSpring", statusLabel, "Spring", [("handle", "int"), ("value", "bool")]);
        yield return Release("System/Windows/Forms/ToolStripStatusLabel.rss", "ReleaseToolStripStatusLabel", statusLabel);

        yield return Instance("System/Windows/Forms/Control/ControlCollection.rss", "AddControl", controlCollection, "Add", [control], [("handle", "int"), ("control", "int")], "null");
        yield return Release("System/Windows/Forms/Control/ControlCollection.rss", "ReleaseControlCollection", controlCollection);
        yield return Instance("System/Windows/Forms/ToolStripItemCollection.rss", "AddToolStripItem", itemCollection, "Add", [toolStripItem], [("handle", "int"), ("item", "int")], "null");
        yield return Release("System/Windows/Forms/ToolStripItemCollection.rss", "ReleaseToolStripItemCollection", itemCollection);

        foreach (var binding in DialogBindings("System/Windows/Forms/OpenFileDialog.rss", "OpenFileDialog", openDialog, window, includeSaveProperties: false)) yield return binding;
        foreach (var binding in DialogBindings("System/Windows/Forms/SaveFileDialog.rss", "SaveFileDialog", saveDialog, window, includeSaveProperties: true)) yield return binding;
        yield return Constructor("System/Windows/Forms/FontDialog.rss", "NewFontDialog", fontDialog, [], [], "int");
        yield return Instance("System/Windows/Forms/FontDialog.rss", "ShowFontDialog", fontDialog, "ShowDialog", [], [("handle", "int")], "string");
        yield return Instance("System/Windows/Forms/FontDialog.rss", "ShowFontDialogForForm", fontDialog, "ShowDialog", [window], [("handle", "int"), ("form", "int")], "string");
        yield return PropertyGet("System/Windows/Forms/FontDialog.rss", "GetFontDialogFont", fontDialog, "Font", [("handle", "int")], "int");
        yield return Release("System/Windows/Forms/FontDialog.rss", "ReleaseFontDialog", fontDialog);
        yield return Constructor("System/Windows/Forms/ColorDialog.rss", "NewColorDialog", colorDialog, [], [], "int");
        yield return Instance("System/Windows/Forms/ColorDialog.rss", "ShowColorDialog", colorDialog, "ShowDialog", [], [("handle", "int")], "string");
        yield return Instance("System/Windows/Forms/ColorDialog.rss", "ShowColorDialogForForm", colorDialog, "ShowDialog", [window], [("handle", "int"), ("form", "int")], "string");
        yield return PropertyGet("System/Windows/Forms/ColorDialog.rss", "GetColorDialogColor", colorDialog, "Color", [("handle", "int")], "int");
        yield return Release("System/Windows/Forms/ColorDialog.rss", "ReleaseColorDialog", colorDialog);

        var bridge = typeof(PdVmWinFormsEventLoop);
        yield return Static("System/Windows/EventLoop.rss", "UiShow", bridge, nameof(PdVmWinFormsEventLoop.Show), [typeof(object)], [("form", "int")], "null");
        yield return Static("System/Windows/EventLoop.rss", "UiBindClick", bridge, nameof(PdVmWinFormsEventLoop.BindClick), [typeof(object), typeof(object), typeof(string)], [("form", "int"), ("control", "int"), ("action", "string")], "null");
        yield return Static("System/Windows/EventLoop.rss", "UiBindDialog", bridge, nameof(PdVmWinFormsEventLoop.BindDialog), [typeof(object), typeof(object), typeof(object), typeof(string)], [("form", "int"), ("control", "int"), ("dialog", "int"), ("action", "string")], "null");
        yield return Static("System/Windows/EventLoop.rss", "UiShowDialog", bridge, nameof(PdVmWinFormsEventLoop.ShowDialog), [typeof(object), typeof(object)], [("form", "int"), ("dialog", "int")], "string");
        yield return Static("System/Windows/EventLoop.rss", "UiBindClosing", bridge, nameof(PdVmWinFormsEventLoop.BindClosing), [typeof(object), typeof(string)], [("form", "int"), ("action", "string")], "null");
        yield return Static("System/Windows/EventLoop.rss", "UiWait", bridge, nameof(PdVmWinFormsEventLoop.Wait), [typeof(object)], [("form", "int")], "string");
        yield return Static("System/Windows/EventLoop.rss", "UiClose", bridge, nameof(PdVmWinFormsEventLoop.Close), [typeof(object)], [("form", "int")], "null");
    }

    private static IEnumerable<Binding> DialogBindings(string module, string typeName, Type type, Type ownerType, bool includeSaveProperties)
    {
        yield return Constructor(module, $"New{typeName}", type, [], [], "int");
        yield return PropertySet(module, $"Set{typeName}Filter", type, "Filter", [("handle", "int"), ("value", "string")]);
        yield return PropertySet(module, $"Set{typeName}Title", type, "Title", [("handle", "int"), ("value", "string")]);
        yield return PropertySet(module, $"Set{typeName}FileName", type, "FileName", [("handle", "int"), ("value", "string")]);
        if (includeSaveProperties)
        {
            yield return PropertySet(module, $"Set{typeName}DefaultExt", type, "DefaultExt", [("handle", "int"), ("value", "string")]);
            yield return PropertySet(module, $"Set{typeName}AddExtension", type, "AddExtension", [("handle", "int"), ("value", "bool")]);
        }
        yield return Instance(module, $"Show{typeName}", type, "ShowDialog", [], [("handle", "int")], "string");
        yield return Instance(module, $"Show{typeName}ForForm", type, "ShowDialog", [ownerType], [("handle", "int"), ("form", "int")], "string");
        yield return PropertyGet(module, $"Get{typeName}FileName", type, "FileName", [("handle", "int")], "string");
        yield return Release(module, $"Release{typeName}", type);
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
                imports.Add(new SystemImport(
                    Path.GetRelativePath(sourceRoot, sourcePath),
                    line,
                    match.Groups["path"].Value));
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
        string sourceRoot,
        PdVmDotNetInteropProfile profile)
    {
        if (imports.Count == 0)
        {
            return [];
        }

        if (profile.HasFlag(PdVmDotNetInteropProfile.WindowsForms) && OperatingSystem.IsWindows())
        {
            _ = new PdVmDotNetHost(allowDynamicSystemCalls: true)
                .CanResolveType("System.Windows.Forms.Form");
        }

        var externalPaths = DiscoverExternalAssemblyPaths(sourceRoot);
        var assemblies = DiscoverAssemblies(externalPaths);
        var resolved = new List<ResolvedSystemImport>();
        foreach (var import in imports)
        {
            var existing = ResolveProfileModule(import, profile);
            if (existing is not null)
            {
                resolved.Add(existing);
                continue;
            }

            var type = assemblies
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

    private static ResolvedSystemImport? ResolveProfileModule(
        SystemImport import,
        PdVmDotNetInteropProfile profile)
    {
        var manualBindings = BuildBindingsForProfileModule(profile, import.ModulePath).ToArray();
        if (manualBindings.Length == 0)
        {
            return null;
        }
        return new ResolvedSystemImport(import, manualBindings[0].DeclaringType, false);
    }

    private static IEnumerable<Binding> BuildBindingsForProfileModule(
        PdVmDotNetInteropProfile profile,
        string modulePath)
    {
        if (profile.HasFlag(PdVmDotNetInteropProfile.Common))
        {
            foreach (var binding in BuildCommonBindings().Where(binding => binding.ModulePath == modulePath))
            {
                yield return binding;
            }
        }
        if (profile.HasFlag(PdVmDotNetInteropProfile.WindowsForms) && OperatingSystem.IsWindows())
        {
            foreach (var binding in BuildWindowsFormsBindings().Where(binding => binding.ModulePath == modulePath))
            {
                yield return binding;
            }
        }
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
        var bindings = new List<Binding>();
        if (!type.IsAbstract || !type.IsSealed)
        {
            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (TryBuildParameters(constructor.GetParameters(), includeHandle: false, out var parameterTypes, out var parameters))
                {
                    bindings.Add(Constructor(modulePath, "New", type, parameterTypes, parameters, "int"));
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
                ? Static(modulePath, method.Name, type, method.Name, parameterTypes, parameters, returnSchema)
                : Instance(modulePath, method.Name, type, method.Name, parameterTypes, parameters, returnSchema));
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetIndexParameters().Length == 0))
        {
            if (property.GetMethod is not null && TryGetSchema(property.PropertyType, out var getSchema))
            {
                bindings.Add(PropertyGet(modulePath, $"Get{property.Name}", type, property.Name, [("handle", "int")], getSchema));
            }
            if (property.SetMethod is not null && TryGetSchema(property.PropertyType, out var setSchema))
            {
                bindings.Add(new Binding(
                    modulePath,
                    $"Set{property.Name}",
                    PdVmDotNetMemberKind.InstancePropertySet,
                    type,
                    property.Name,
                    [property.PropertyType],
                    [new Parameter("handle", "int"), new Parameter("value", setSchema)],
                    "null"));
            }
        }

        if (!type.IsValueType)
        {
            bindings.Add(Release(modulePath, "Release", type));
        }
        return AssignGeneratedNames(bindings);
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
                .ThenBy(binding => string.Join("_", binding.ClrParameterTypes.Select(type => type.Name)), StringComparer.Ordinal)
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
