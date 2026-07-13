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
    public void WindowsAdaptersExcludeTaskSpecificUiTypes()
    {
        var runtime = typeof(PdVmDotNetHost).Assembly;

        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsGrid"));
        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsActionMap"));
        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsPrompt"));
        Assert.NotNull(runtime.GetType("PdVm.Runtime.PdVmWinFormsScene"));
        Assert.NotNull(typeof(PdVmWinFormsEventLoop).GetMethod("BindPointer"));
        Assert.NotNull(typeof(PdVmWinFormsEventLoop).GetMethod("GetPointerX"));
        Assert.NotNull(typeof(PdVmWinFormsEventLoop).GetMethod("GetPointerY"));
        Assert.NotNull(typeof(PdVmWinFormsEventLoop).GetMethod("GetPointerButton"));
    }

    [Fact]
    public void PointerQueuePreservesTheSnapshotForEveryEvent()
    {
        var form = new object();
        var control = new FakePointerControl();
        PdVmWinFormsEventLoop.BindPointer(form, control, "surface");

        control.RaiseMouseUp(new FakePointerEventArgs("Left", 11, 12, 1));
        control.RaiseMouseUp(new FakePointerEventArgs("Left", 31, 32, 1));

        Assert.Equal("surface_up", PdVmWinFormsEventLoop.Wait(form));
        Assert.Equal(11, PdVmWinFormsEventLoop.GetPointerX(form));
        Assert.Equal(12, PdVmWinFormsEventLoop.GetPointerY(form));
        Assert.Equal("surface_up", PdVmWinFormsEventLoop.Wait(form));
        Assert.Equal(31, PdVmWinFormsEventLoop.GetPointerX(form));
        Assert.Equal(32, PdVmWinFormsEventLoop.GetPointerY(form));
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

    private sealed record FakePointerEventArgs(string Button, int X, int Y, int Clicks);

    private sealed class FakePointerControl
    {
        public event EventHandler<FakePointerEventArgs>? MouseDown;
        public event EventHandler<FakePointerEventArgs>? MouseUp;
        public event EventHandler<FakePointerEventArgs>? MouseDoubleClick;
        public event EventHandler<FakePointerEventArgs>? MouseLeave;

        public void RaiseMouseDown(FakePointerEventArgs args) => MouseDown?.Invoke(this, args);
        public void RaiseMouseUp(FakePointerEventArgs args) => MouseUp?.Invoke(this, args);
        public void RaiseMouseDoubleClick(FakePointerEventArgs args) => MouseDoubleClick?.Invoke(this, args);
        public void RaiseMouseLeave(FakePointerEventArgs args) => MouseLeave?.Invoke(this, args);
    }

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
