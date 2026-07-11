using PdVm.Compiler;
using PdVm.Runtime;

namespace PdVm.Tests;

public sealed class PdVmTypedDotNetInteropTests
{
    [Fact]
    public void NativeCompilerEmitsReadableVmbcWithoutRunnerProcess()
    {
        using var fixture = new SourceFixture("let answer: int = 40 + 2;\n");

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
                "system::Math::Sqrt",
                [PdVmValue.FromFloat(4)]));

        Assert.Contains("unbound host import", error.Message);
    }

    [Fact]
    public void SourceWrapperCompilesTypedCommonModuleWithOriginalCompiler()
    {
        using var fixture = new SourceFixture(
            "use system::System::IO::Path;\n" +
            "let name: string = Path::GetFileName(\"folder/demo.rss\");\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);

        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "PdVm.Runtime.dll")));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void SourceWrapperReportsParameterTypeErrorsAtCompileTime()
    {
        using var fixture = new SourceFixture(
            "use system::System::Console;\n" +
            "Console::WriteLine(123);\n");

        var error = Assert.Throws<PdVmCompilerException>(() =>
            PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath));

        Assert.Contains("RustScript compilation failed", error.Message);
        Assert.False(File.Exists(fixture.OutputPath));
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
