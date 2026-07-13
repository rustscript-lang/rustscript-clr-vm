using System.Reflection;
using PdVm.Compiler;
using PdVm.Runtime;

namespace PdVm.Tests;

public sealed class PdVmTypedDotNetInteropTests
{
    [Fact]
    public void NativeLibraryUsesDotNetStylePackageName()
    {
        var expected = OperatingSystem.IsWindows()
            ? "Pdvm.Compiler.Native.dll"
            : OperatingSystem.IsMacOS()
                ? "libPdvm.Compiler.Native.dylib"
                : "libPdvm.Compiler.Native.so";

        Assert.Equal(expected, PdVmNativeCompiler.GetLibraryFileName());
    }

    [Fact]
    public void NativeCompilerEmitsReadableVmbcWithoutRunnerProcess()
    {
        using var fixture = new SourceFixture("let answer = 40 + 2;\n");

        var bytes = PdVmNativeCompiler.CompileFile(fixture.SourcePath);
        var model = PdVmVmbcReader.ReadBytes(bytes);

        Assert.NotEmpty(model.Code);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("VMBC"u8));
    }

    [Fact]
    public void ExactDescriptorRoundTripsAndInvokesSelectedOverload()
    {
        var method = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(long)])!;
        var descriptor = new PdVmDotNetBindingDescriptor(
            method.DeclaringType!.Assembly.FullName!,
            method.DeclaringType.Assembly.ManifestModule.ModuleVersionId,
            method.DeclaringType.FullName!,
            method.Name,
            PdVmDotNetMemberKind.StaticMethod,
            [typeof(long).AssemblyQualifiedName!],
            typeof(long).AssemblyQualifiedName!);

        var importName = descriptor.EncodeImportName();
        Assert.True(PdVmDotNetBindingDescriptor.TryDecodeImportName(importName, out var decoded));
        Assert.Equal(descriptor.AssemblyName, decoded.AssemblyName);
        Assert.Equal(descriptor.ModuleVersionId, decoded.ModuleVersionId);
        Assert.Equal(descriptor.ParameterTypeNames, decoded.ParameterTypeNames);

        var value = ReturnValue(new PdVmDotNetHost().Call(
            importName,
            [PdVmValue.FromInt(-42)]));
        Assert.Equal(42, value.AsInt());
    }

    [Fact]
    public void DefaultHostRejectsNameBasedDynamicCalls()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PdVmDotNetHost().Call(
                "System::Math::Sqrt",
                [PdVmValue.FromFloat(4)]));

        Assert.Contains("unbound host import", error.Message);
    }

    [Fact]
    public void SourceWrapperCompilesTypedCommonModuleWithOriginalCompiler()
    {
        using var fixture = new SourceFixture(
            "use System::IO::Path;\n" +
            "let name = Path::GetFileName(\"folder/demo.rss\");\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);

        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "PdVm.Runtime.dll")));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void SourceWrapperReportsParameterTypeErrorsAtCompileTime()
    {
        using var fixture = new SourceFixture(
            "use System::Console;\n" +
            "Console::WriteLine(123);\n");

        var error = Assert.Throws<PdVmCompilerException>(() =>
            PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath));

        Assert.Contains("RustScript compilation failed", error.Message);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void SourceWrapperCompilesTypedWindowsFormsEventLoopProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SourceFixture(
            "use System::Windows::Forms::Form;\n" +
            "use System::Windows::EventLoop as Ui;\n" +
            "let form = Form::NewForm();\n" +
            "Ui::UiBindClosing(form, \"close\");\n" +
            "Ui::UiClose(form);\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(
            fixture.SourcePath,
            fixture.OutputPath,
            new PdVmDotNetSourceCompileOptions
            {
                Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            });

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void SourceWrapperCompilesRustScriptNotepadExample()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var examplePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "examples",
            "dotnet-typed-winforms.rss"));
        using var fixture = new SourceFixture(File.ReadAllText(examplePath));

        var output = PdVmDotNetSourceCompiler.CompileFile(
            fixture.SourcePath,
            fixture.OutputPath,
            new PdVmDotNetSourceCompileOptions
            {
                Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            });

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void WinFormsEventsReturnToRustScriptWithoutBlockingTheUiThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signalPath = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-typed-interop-tests",
            $"event-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(signalPath)!);
        using var fixture = new SourceFixture(
            "use System::IO::File;\n" +
            "use System::Windows::Forms::Form;\n" +
            "use System::Windows::Forms::ToolStripMenuItem;\n" +
            "use System::Windows::EventLoop as Ui;\n" +
            "let form = Form::NewForm();\n" +
            "let item = ToolStripMenuItem::NewToolStripMenuItem(\"Run\");\n" +
            "Ui::UiBindClick(form, item, \"clicked\");\n" +
            "Ui::UiShow(form);\n" +
            "ToolStripMenuItem::PerformToolStripMenuItemClick(item);\n" +
            "let action = Ui::UiWait(form);\n" +
            $"File::WriteAllText(\"{signalPath.Replace('\\', '/')}\", action);\n" +
            "Ui::UiClose(form);\n");

        try
        {
            var output = PdVmDotNetSourceCompiler.CompileFile(
                fixture.SourcePath,
                fixture.OutputPath,
                new PdVmDotNetSourceCompileOptions
                {
                    Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
                });
            var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
            var host = PdVmDefaultHost.CreateConsoleHost();
            host.RegisterFallback(new PdVmDotNetHost().Call);

            var result = PdVmExecution.Run(program, host);

            Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
            Assert.Equal("clicked", File.ReadAllText(signalPath));
        }
        finally
        {
            File.Delete(signalPath);
        }
    }

    [Fact]
    public void SourceWrapperGeneratesTypedCryptographyBindingsFromSystemUse()
    {
        using var fixture = new SourceFixture(
            "use System::Security::Cryptography::SHA256;\n" +
            "let algorithm = SHA256::Create();\n" +
            "SHA256::Release(algorithm);\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
        Assert.Contains(
            program.GetType().Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "System.Security.Cryptography");

        var host = PdVmDefaultHost.CreateConsoleHost();
        host.RegisterFallback(new PdVmDotNetHost().Call);
        var result = PdVmExecution.Run(program, host);
        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
    }

    [Fact]
    public void SourceWrapperReportsReadableErrorForMissingSystemType()
    {
        using var fixture = new SourceFixture(
            "use System::Security::Cryptography::MissingAlgorithm;\n");

        var error = Assert.Throws<PdVmCompilerException>(() =>
            PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath));

        Assert.Contains("main.rss:1", error.Message);
        Assert.Contains("System.Security.Cryptography.MissingAlgorithm", error.Message);
        Assert.Contains("Searched the .NET runtime", error.Message);
    }

    private static PdVmValue ReturnValue(PdVmCallOutcome outcome) =>
        Assert.Single(outcome.ReturnValues.Values);

    private sealed class SourceFixture : IDisposable
    {
        public SourceFixture(string source)
        {
            Root = Path.Combine(Path.GetTempPath(), "pd-vm-typed-interop-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SourcePath = Path.Combine(Root, "main.rss");
            OutputPath = Path.Combine(Root, "program.dll");
            File.WriteAllText(SourcePath, source);
        }

        public string Root { get; }

        public string SourcePath { get; }

        public string OutputPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
