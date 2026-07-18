using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using PdVm.Compiler;
using PdVm.Runtime;

namespace PdVm.Tests;

public sealed class PdVmCompilerTests
{
    [Fact]
    public void CompilesArithmeticAndBranchProgram()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitLdc(1)
            .Emit(PdVmBytecodeOpCode.Add)
            .EmitStloc(0)
            .EmitLdc(2)
            .EmitBrfalse("use_local")
            .EmitLdc(3)
            .EmitStloc(0)
            .MarkLabel("use_local")
            .EmitLdloc(0)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var program = CompileProgram(
            constants:
            [
                PdVmValue.FromInt(1),
                PdVmValue.FromInt(2),
                PdVmValue.FromBool(false),
                PdVmValue.FromInt(999),
            ],
            code: code);

        var result = PdVmExecution.Run(program, new PdVmDelegateHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        var stack = Assert.Single(program.Stack);
        Assert.Equal(3, stack.AsInt());
    }

    [Fact]
    public void RunsBuiltinLenIntrinsic()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.Len), 1)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();
        var text = "ab" + char.ConvertFromUtf32(0x1F642);

        var program = CompileProgram(
            constants: [PdVmValue.FromString(text)],
            code: code);

        var result = PdVmExecution.Run(program, new PdVmDelegateHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        var stack = Assert.Single(program.Stack);
        Assert.Equal(3, stack.AsInt());
    }

    [Fact]
    public void ReadsV10CallableMetadataAndExportSchema()
    {
        var callableSchema = new PdVmTypeSchema(
            PdVmTypeSchemaKind.Callable,
            items: Array.Empty<PdVmTypeSchema>(),
            result: new PdVmTypeSchema(PdVmTypeSchemaKind.Null));
        var typeMap = new PdVmTypeMap(
            [PdVmValueType.Callable],
            new Dictionary<int, PdVmOperandTypes>(),
            [callableSchema],
            [true],
            [false],
            strictTypes: true);
        var payload = EncodeVmbc(
            Array.Empty<PdVmValue>(),
            [(byte)PdVmBytecodeOpCode.Ret, (byte)PdVmBytecodeOpCode.Ret],
            Array.Empty<PdVmHostImport>(),
            typeMap,
            writer =>
            {
                writer.Write((uint)1); // script functions
                writer.Write((uint)1);
                writer.Write((uint)2);

                writer.Write((uint)1); // callable prototypes
                writer.Write((byte)PdVmCallableKind.FunctionItem);
                writer.Write((byte)PdVmCallableTargetKind.ScriptFunction);
                writer.Write((uint)0);
                writer.Write((byte)0);
                writer.Write((uint)1);
                writer.Write((uint)0); // parameters
                writer.Write((uint)0); // capture sources
                writer.Write((uint)0); // capture targets
                writer.Write((uint)0); // capture modes
                writer.Write((byte)0); // self slot
                writer.Write((byte)1); // schema
                WriteSchema(writer, callableSchema);

                writer.Write((uint)2); // function regions
                writer.Write((uint)0);
                writer.Write((uint)1);
                writer.Write((byte)0);
                writer.Write((uint)1);
                writer.Write((uint)2);
                writer.Write((byte)1);
                writer.Write((uint)0);

                writer.Write((uint)1); // root callable bindings
                writer.Write((ushort)0);
                writer.Write((uint)0);

                writer.Write((uint)1); // exported callables
                WriteString(writer, "on_click");
                writer.Write((ushort)0);
            });

        var model = PdVmVmbcReader.ReadBytes(payload);

        var prototype = Assert.Single(model.CallablePrototypes);
        Assert.Equal(PdVmCallableTargetKind.ScriptFunction, prototype.Target.Kind);
        Assert.Equal(PdVmTypeSchemaKind.Callable, prototype.Schema?.Kind);
        Assert.Equal(PdVmTypeSchemaKind.Null, prototype.Schema?.Result?.Kind);
        Assert.Equal("on_click", Assert.Single(model.ExportedCallables).Name);
        Assert.Equal(2, model.FunctionRegions.Count);
        Assert.True(Assert.Single(model.TypeMap!.CallableSlots));
    }

    [Fact]
    public void ExecutesMoveCaptureAndDetachLocalContract()
    {
        var builder = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitStloc(0)
            .EmitLdc(1)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.ArrayNew), 0)
            .EmitLdloc(0)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.ArrayPush), 2)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.BindCallable), 2)
            .EmitStloc(1)
            .EmitLdc(2)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.DetachLocal), 1)
            .EmitLdloc(1)
            .EmitCallValue(0)
            .Emit(PdVmBytecodeOpCode.Ret);
        var rootEnd = builder.Position;
        builder.EmitLdloc(2).Emit(PdVmBytecodeOpCode.Ret);
        var functionEnd = builder.Position;
        var typeMap = new PdVmTypeMap(
            [PdVmValueType.Array, PdVmValueType.Callable, PdVmValueType.Array],
            new Dictionary<int, PdVmOperandTypes>(),
            [null, null, null],
            [false, true, false],
            [false, false, false],
            strictTypes: true);
        var artifact = CompileProgramArtifact(
            [
                PdVmValue.FromArray([PdVmValue.FromInt(1), PdVmValue.FromInt(2)]),
                PdVmValue.FromInt(0),
                PdVmValue.FromInt(0),
            ],
            builder.Build(),
            typeMap: typeMap,
            writeCallableMetadata: writer =>
            {
                writer.Write((uint)1); // script functions
                writer.Write((uint)rootEnd);
                writer.Write((uint)functionEnd);

                writer.Write((uint)1); // callable prototypes
                writer.Write((byte)PdVmCallableKind.Closure);
                writer.Write((byte)PdVmCallableTargetKind.ScriptFunction);
                writer.Write((uint)0);
                writer.Write((byte)0);
                writer.Write((uint)3);
                writer.Write((uint)0); // parameters
                writer.Write((uint)1); // capture sources
                writer.Write((ushort)0);
                writer.Write((uint)1); // capture targets
                writer.Write((ushort)2);
                writer.Write((uint)1); // capture modes
                writer.Write((byte)PdVmCaptureBindingMode.Move);
                writer.Write((byte)0); // self slot
                writer.Write((byte)0); // schema

                writer.Write((uint)2); // function regions
                writer.Write((uint)0);
                writer.Write((uint)rootEnd);
                writer.Write((byte)0);
                writer.Write((uint)rootEnd);
                writer.Write((uint)functionEnd);
                writer.Write((byte)1);
                writer.Write((uint)0);

                writer.Write((uint)0); // root callable bindings
                writer.Write((uint)0); // exported callables
            });

        var result = PdVmExecution.Run(artifact.Program, new PdVmDelegateHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(PdVmValueKind.Null, artifact.Program.Locals[0].Kind);
        Assert.Equal(
            [1L, 2L],
            Assert.Single(artifact.Program.Stack).AsArray().Select(value => value.AsInt()));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void RejectsPreV10PayloadsWithPreciseVersion(int version)
    {
        var payload = EncodeVmbc(
            Array.Empty<PdVmValue>(),
            [(byte)PdVmBytecodeOpCode.Ret],
            Array.Empty<PdVmHostImport>());
        payload[4] = checked((byte)version);
        payload[5] = 0;

        var error = Assert.Throws<PdVmCompilerException>(() => PdVmVmbcReader.ReadBytes(payload));

        Assert.Equal($"unsupported VMBC version {version}, expected 10", error.Message);
    }

    [Fact]
    public void RejectsCallableRegionGapBeforeAssemblyGeneration()
    {
        var payload = EncodeVmbc(
            Array.Empty<PdVmValue>(),
            [
                (byte)PdVmBytecodeOpCode.Ret,
                (byte)PdVmBytecodeOpCode.Ret,
                (byte)PdVmBytecodeOpCode.Ret,
            ],
            Array.Empty<PdVmHostImport>(),
            writeCallableMetadata: writer =>
            {
                writer.Write((uint)0); // script functions
                writer.Write((uint)0); // callable prototypes
                writer.Write((uint)2); // function regions
                writer.Write((uint)0);
                writer.Write((uint)1);
                writer.Write((byte)0);
                writer.Write((uint)2);
                writer.Write((uint)3);
                writer.Write((byte)0);
                writer.Write((uint)0); // root callable bindings
                writer.Write((uint)0); // exported callables
            });

        var error = Assert.Throws<PdVmCompilerException>(() => PdVmVmbcReader.ReadBytes(payload));

        Assert.Contains("leave a gap", error.Message);
    }

    [Fact]
    public void BuiltinCatalogIndexesRoundTripWithoutCollisions()
    {
        var builtins = Enum.GetValues<PdVmBuiltin>();
        var indexes = builtins.Select(PdVmBuiltins.GetCallIndex).ToArray();

        Assert.Equal(indexes.Length, indexes.Distinct().Count());
        foreach (var builtin in builtins)
        {
            var index = PdVmBuiltins.GetCallIndex(builtin);
            Assert.True(PdVmBuiltins.TryGetBuiltin(index, out var decoded));
            Assert.Equal(builtin, decoded);
            Assert.InRange(PdVmBuiltins.GetArity(builtin), (byte)0, (byte)3);
        }

        Assert.Equal(0xFFA3, PdVmBuiltins.BuiltinCallBase);
        Assert.Equal(89, PdVmBuiltins.BuiltinCallCount);
        Assert.Equal(0xFF95, PdVmBuiltins.GetCallIndex(PdVmBuiltin.BindCallable));
        Assert.Equal(0xFF94, PdVmBuiltins.GetCallIndex(PdVmBuiltin.DetachLocal));
    }

    [Fact]
    public void ImportRemapPreservesCallableMetadataObjects()
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
        var scriptFunctions = new[] { new PdVmScriptFunction(1, 2) };
        var prototypes = new[]
        {
            new PdVmCallablePrototype(
                PdVmCallableKind.FunctionItem,
                new PdVmCallableTarget(PdVmCallableTargetKind.ScriptFunction, 0),
                0,
                1,
                [],
                [],
                [],
                [],
                null,
                null),
        };
        var regions = new[]
        {
            new PdVmFunctionRegion(0, 1, null),
            new PdVmFunctionRegion(1, 2, 0),
        };
        var rootBindings = new[] { new PdVmRootCallableBinding(0, 0) };
        var exports = new[] { new PdVmExportedCallable("run", 0) };
        var model = new PdVmProgramModel(
            [],
            [(byte)PdVmBytecodeOpCode.Ret, (byte)PdVmBytecodeOpCode.Ret],
            1,
            [new PdVmHostImport("__clr_b_0", 1, PdVmValueType.Int)],
            [
                new PdVmInstruction(0, PdVmBytecodeOpCode.Ret, 1),
                new PdVmInstruction(1, PdVmBytecodeOpCode.Ret, 2),
            ],
            scriptFunctions: scriptFunctions,
            callablePrototypes: prototypes,
            functionRegions: regions,
            rootCallableBindings: rootBindings,
            exportedCallables: exports);
        var remap = typeof(PdVmDotNetSourceCompiler).GetMethod(
            "RemapImports",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var remapped = Assert.IsType<PdVmProgramModel>(remap.Invoke(
            null,
            [model, new Dictionary<string, PdVmDotNetBindingDescriptor> { ["__clr_b_0"] = descriptor }]));

        Assert.Equal(descriptor.EncodeImportName(), Assert.Single(remapped.Imports).Name);
        Assert.Same(scriptFunctions, remapped.ScriptFunctions);
        Assert.Same(prototypes, remapped.CallablePrototypes);
        Assert.Same(regions, remapped.FunctionRegions);
        Assert.Same(rootBindings, remapped.RootCallableBindings);
        Assert.Same(exports, remapped.ExportedCallables);
    }

    [Fact]
    public void ReadsAndEmitsNestedContainerConstants()
    {
        var constant = PdVmValue.FromArray(
        [
            PdVmValue.FromInt(1),
            PdVmValue.FromMap(
            [
                new KeyValuePair<PdVmValue, PdVmValue>(
                    PdVmValue.FromString("key"),
                    PdVmValue.FromBool(true)),
            ]),
        ]);
        var program = CompileProgram(
            [constant],
            new BytecodeBuilder()
                .EmitLdc(0)
                .Emit(PdVmBytecodeOpCode.Ret)
                .Build());

        _ = PdVmExecution.Run(program, new PdVmDelegateHost());

        Assert.Equal(constant, Assert.Single(program.Stack));
    }

    [Fact]
    public void UsesTypedAddLoweringWhenOperandTypesAreKnown()
    {
        var builder = new BytecodeBuilder();
        builder.EmitLdc(0).EmitLdc(1);
        var addOffset = builder.Position;
        var code = builder.Emit(PdVmBytecodeOpCode.Add).Emit(PdVmBytecodeOpCode.Ret).Build();

        var typeMap = new PdVmTypeMap(
            Array.Empty<PdVmValueType>(),
            new Dictionary<int, PdVmOperandTypes>
            {
                [addOffset] = new(PdVmValueType.Int, PdVmValueType.Int),
            });

        var artifact = CompileProgramArtifact(
            constants:
            [
                PdVmValue.FromInt(20),
                PdVmValue.FromInt(22),
            ],
            code: code,
            typeMap: typeMap);

        var result = PdVmExecution.Run(artifact.Program, new PdVmDelegateHost());
        var calledMethods = ReadCalledMethods(GetCompiledRunStep(artifact.Program));
        var opCodes = ReadOpCodes(GetCompiledRunStep(artifact.Program));

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(42, Assert.Single(artifact.Program.Stack).AsInt());
        Assert.DoesNotContain(
            calledMethods,
            method => method == typeof(PdVmOps).GetMethod(
                nameof(PdVmOps.Add),
                new[] { typeof(PdVmValue), typeof(PdVmValue) }));
        Assert.Contains(calledMethods, method => method == typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsInt), Type.EmptyTypes));
        Assert.Contains(OpCodes.Add, opCodes);
    }

    [Fact]
    public void FallsBackToDynamicAddHelperWithoutOperandTypes()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitLdc(1)
            .Emit(PdVmBytecodeOpCode.Add)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var artifact = CompileProgramArtifact(
            constants:
            [
                PdVmValue.FromInt(20),
                PdVmValue.FromInt(22),
            ],
            code: code);

        var result = PdVmExecution.Run(artifact.Program, new PdVmDelegateHost());
        var calledMethods = ReadCalledMethods(GetCompiledRunStep(artifact.Program));

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(42, Assert.Single(artifact.Program.Stack).AsInt());
        Assert.Contains(
            calledMethods,
            method => method == typeof(PdVmOps).GetMethod(
                nameof(PdVmOps.Add),
                new[] { typeof(PdVmValue), typeof(PdVmValue) }));
    }

    [Fact]
    public void EmitsSelfContainedClrArtifactPair()
    {
        var artifact = CompileProgramArtifact(
            constants: [PdVmValue.FromInt(42)],
            code: new BytecodeBuilder()
                .EmitLdc(0)
                .Emit(PdVmBytecodeOpCode.Ret)
                .Build());

        var references = artifact.Program.GetType().Assembly.GetReferencedAssemblies();
        var runtimePath = Path.Combine(
            Path.GetDirectoryName(artifact.AssemblyPath)!,
            "PdVm.Runtime.dll");

        Assert.Contains(references, reference => reference.Name == "PdVm.Runtime");
        Assert.DoesNotContain(references, reference => reference.Name == "PdVm.Compiler");
        Assert.DoesNotContain(
            artifact.Program.GetType().GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public),
            field => field.FieldType == typeof(byte[]));
        Assert.True(File.Exists(runtimePath));
    }

    [Fact]
    public void UsesDirectJsonAndIoBuiltinLowering()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.IoExists), 1)
            .Emit(PdVmBytecodeOpCode.Pop)
            .EmitLdc(1)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.JsonDecode), 1)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var artifact = CompileProgramArtifact(
            constants:
            [
                PdVmValue.FromString(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.missing")),
                PdVmValue.FromString("{\"value\":42}"),
            ],
            code: code);

        var result = PdVmExecution.Run(artifact.Program, new PdVmDelegateHost());
        var calledMethods = ReadCalledMethods(GetCompiledRunStep(artifact.Program));

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        var map = Assert.Single(artifact.Program.Stack).AsMap();
        Assert.True(map.TryGetValue(PdVmValue.FromString("value"), out var value));
        Assert.Equal(42, value.AsInt());
        Assert.DoesNotContain(
            calledMethods,
            method => method == GetBaseMethod(
                "DispatchCall",
                typeof(IPdVmHost),
                typeof(PdVmHostImport[]),
                typeof(ushort),
                typeof(byte),
                typeof(int),
                typeof(int)));
        Assert.Contains(calledMethods, method => method == typeof(PdVmBuiltins).GetMethod(nameof(PdVmBuiltins.IoExistsValue), new[] { typeof(PdVmValue) }));
        Assert.Contains(calledMethods, method => method == typeof(PdVmBuiltins).GetMethod(nameof(PdVmBuiltins.JsonDecodeValue), new[] { typeof(PdVmValue) }));
    }

    [Fact]
    public void RunsSyncHostImport()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitCall(0, 1)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var program = CompileProgram(
            constants: [PdVmValue.FromInt(21)],
            code: code,
            imports: [new PdVmHostImport("double", 1, PdVmValueType.Int)]);

        var host = new PdVmDelegateHost();
        host.RegisterValue("double", args => PdVmValue.FromInt(args[0].AsInt() * 2));

        var result = PdVmExecution.Run(program, host);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        var stack = Assert.Single(program.Stack);
        Assert.Equal(42, stack.AsInt());
    }

    [Fact]
    public void PreservesClrEvaluationStateAcrossHostYieldBoundary()
    {
        var program = CompileProgram(
            constants: [PdVmValue.FromInt(21)],
            code: new BytecodeBuilder()
                .EmitLdc(0)
                .EmitCall(0, 1)
                .Emit(PdVmBytecodeOpCode.Ret)
                .Build(),
            imports: [new PdVmHostImport("double_after_yield", 1, PdVmValueType.Int)]);
        var callCount = 0;
        var host = new PdVmDelegateHost();
        host.Register(
            "double_after_yield",
            args => ++callCount == 1
                ? PdVmCallOutcome.Yielded()
                : PdVmCallOutcome.Returned(PdVmCallReturn.One(PdVmValue.FromInt(args[0].AsInt() * 2))));

        var result = PdVmExecution.Run(program, host);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(2, callCount);
        Assert.Equal(42, Assert.Single(program.Stack).AsInt());
    }

    [Fact]
    public void ReportsAggregatedExecutedInstructionCount()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitLdc(1)
            .Emit(PdVmBytecodeOpCode.Add)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var program = CompileProgram(
            constants:
            [
                PdVmValue.FromInt(20),
                PdVmValue.FromInt(22),
            ],
            code: code);

        var result = PdVmExecution.Run(program, new PdVmDelegateHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(4, result.Steps);
        Assert.Equal(4, program.ExecutedInstructionCount);
        Assert.Equal(42, Assert.Single(program.Stack).AsInt());
    }

    [Fact]
    public async Task RunsAsyncHostImport()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitLdc(1)
            .EmitCall(0, 2)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var program = CompileProgram(
            constants:
            [
                PdVmValue.FromInt(20),
                PdVmValue.FromInt(22),
            ],
            code: code,
            imports: [new PdVmHostImport("delay_add", 2, PdVmValueType.Int)]);

        var host = new PdVmDelegateHost();
        host.RegisterAsyncValue(
            "delay_add",
            async (args, cancellationToken) =>
            {
                await Task.Delay(10, cancellationToken);
                return PdVmValue.FromInt(args[0].AsInt() + args[1].AsInt());
            });

        var result = await PdVmExecution.RunAsync(program, host);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        var stack = Assert.Single(program.Stack);
        Assert.Equal(42, stack.AsInt());
    }

    [Fact]
    public void SupportsArrayConcatenationWithAddOpcode()
    {
        var code = new BytecodeBuilder()
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.ArrayNew), 0)
            .EmitLdc(0)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.ArrayPush), 2)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.ArrayNew), 0)
            .EmitLdc(1)
            .EmitCall(PdVmBuiltins.GetCallIndex(PdVmBuiltin.ArrayPush), 2)
            .Emit(PdVmBytecodeOpCode.Add)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var program = CompileProgram(
            constants:
            [
                PdVmValue.FromInt(1),
                PdVmValue.FromInt(2),
            ],
            code: code);

        var result = PdVmExecution.Run(program, new PdVmDelegateHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        var array = Assert.Single(program.Stack).AsArray();
        Assert.Equal(2, array.Count);
        Assert.Equal(1, array[0].AsInt());
        Assert.Equal(2, array[1].AsInt());
    }

    [Fact]
    public void DispatchesRegexJsonAndMathBuiltins()
    {
        var payload = PdVmValue.FromMap(
        [
            new KeyValuePair<PdVmValue, PdVmValue>(PdVmValue.FromString("score"), PdVmValue.FromInt(12)),
        ]);

        var jsonOutcome = PdVmBuiltins.Dispatch(
            PdVmBuiltins.GetCallIndex(PdVmBuiltin.JsonEncode),
            [payload]);
        var json = Assert.Single(jsonOutcome.ReturnValues.Values).AsString();

        var decodedOutcome = PdVmBuiltins.Dispatch(
            PdVmBuiltins.GetCallIndex(PdVmBuiltin.JsonDecode),
            [PdVmValue.FromString(json)]);
        var decoded = Assert.Single(decodedOutcome.ReturnValues.Values).AsMap();

        var regexOutcome = PdVmBuiltins.Dispatch(
            PdVmBuiltins.GetCallIndex(PdVmBuiltin.ReMatch),
            [PdVmValue.FromString("(?i)^rustscript$"), PdVmValue.FromString("RUSTSCRIPT")]);

        var mathOutcome = PdVmBuiltins.Dispatch(
            PdVmBuiltins.GetCallIndex(PdVmBuiltin.MathRound),
            [PdVmValue.FromFloat(1.6)]);

        Assert.True(decoded.TryGetValue(PdVmValue.FromString("score"), out var score));
        Assert.Equal(12, score.AsInt());
        Assert.True(Assert.Single(regexOutcome.ReturnValues.Values).AsBool());
        Assert.Equal(2d, Assert.Single(mathOutcome.ReturnValues.Values).FloatValue);
    }

    [Fact]
    public void EnforcesMaxStepsAcrossBackwardBranchSafepoints()
    {
        var code = new BytecodeBuilder()
            .MarkLabel("loop")
            .Emit(PdVmBytecodeOpCode.Nop)
            .EmitBr("loop")
            .Build();

        var program = CompileProgram(constants: [], code: code);

        var exception = Assert.Throws<InvalidOperationException>(
            () => PdVmExecution.Run(program, new PdVmDelegateHost(), maxSteps: 3));

        Assert.Equal("execution exceeded 3 steps", exception.Message);
    }

    [Fact]
    public void ExecutesFiniteBackwardBranchesInsideOneGeneratedMethodCall()
    {
        var code = new BytecodeBuilder()
            .EmitLdc(0)
            .EmitStloc(0)
            .MarkLabel("loop")
            .EmitLdloc(0)
            .EmitLdc(1)
            .Emit(PdVmBytecodeOpCode.Sub)
            .Emit(PdVmBytecodeOpCode.Dup)
            .EmitStloc(0)
            .EmitLdc(2)
            .Emit(PdVmBytecodeOpCode.Cgt)
            .EmitBrfalse("done")
            .EmitBr("loop")
            .MarkLabel("done")
            .EmitLdloc(0)
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var program = CompileProgram(
            constants:
            [
                PdVmValue.FromInt(3),
                PdVmValue.FromInt(1),
                PdVmValue.FromInt(0),
            ],
            code: code);

        var status = program.RunStep(new PdVmDelegateHost(), instructionBudget: 100);

        Assert.Equal(PdVmStatusKind.Halted, status.Kind);
        Assert.Equal(0, Assert.Single(program.Stack).AsInt());
        Assert.True(program.ExecutedInstructionCount > 13);
    }

    [Fact]
    public void GeneratedProgramsUseFrameRelativeRuntimeLocals()
    {
        var builder = new BytecodeBuilder();
        for (var index = 0; index < byte.MaxValue; index++)
        {
            builder.EmitLdc(0).EmitStloc((byte)index);
        }
        var code = builder
            .EmitLdloc((byte)(byte.MaxValue - 1))
            .EmitCall(0, 1)
            .EmitStloc((byte)(byte.MaxValue - 1))
            .EmitLdloc((byte)(byte.MaxValue - 1))
            .Emit(PdVmBytecodeOpCode.Ret)
            .Build();

        var artifact = CompileProgramArtifact(
            constants: [PdVmValue.FromInt(42)],
            code: code,
            imports: [new PdVmHostImport("identity", 1, PdVmValueType.Int)]);
        var host = new PdVmDelegateHost();
        host.RegisterValue("identity", args => args[0]);

        var result = PdVmExecution.Run(artifact.Program, host);
        var generatedFields = artifact.Program.GetType().GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(42, Assert.Single(artifact.Program.Stack).AsInt());
        Assert.Equal(byte.MaxValue, artifact.Program.Locals.Count);
        Assert.All(artifact.Program.Locals, value => Assert.Equal(42, value.AsInt()));
        Assert.DoesNotContain(generatedFields, field => field.FieldType == typeof(PdVmValue[]));
        Assert.DoesNotContain(generatedFields, field => field.FieldType == typeof(PdVmValue));
        Assert.True(new FileInfo(artifact.AssemblyPath).Length < 256 * 1024);
    }

    private static IPdVmProgram CompileProgram(
        IReadOnlyList<PdVmValue> constants,
        byte[] code,
        IReadOnlyList<PdVmHostImport>? imports = null,
        PdVmTypeMap? typeMap = null)
    {
        return CompileProgramArtifact(constants, code, imports, typeMap).Program;
    }

    private static CompiledProgramArtifact CompileProgramArtifact(
        IReadOnlyList<PdVmValue> constants,
        byte[] code,
        IReadOnlyList<PdVmHostImport>? imports = null,
        PdVmTypeMap? typeMap = null,
        Action<BinaryWriter>? writeCallableMetadata = null)
    {
        var payload = EncodeVmbc(
            constants,
            code,
            imports ?? Array.Empty<PdVmHostImport>(),
            typeMap,
            writeCallableMetadata);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-clr-tests",
            $"{Guid.NewGuid():N}.dll");

        PdVmClrCompiler.Compile(
            payload,
            outputPath,
            new PdVmCompileOptions
            {
                AssemblyName = $"PdVm.Generated.{Guid.NewGuid():N}",
                TypeName = $"PdVm.Generated.Program_{Guid.NewGuid():N}",
            });

        return new CompiledProgramArtifact(PdVmAssemblyLoader.LoadProgram(outputPath), outputPath);
    }

    private static byte[] EncodeVmbc(
        IReadOnlyList<PdVmValue> constants,
        byte[] code,
        IReadOnlyList<PdVmHostImport> imports,
        PdVmTypeMap? typeMap = null,
        Action<BinaryWriter>? writeCallableMetadata = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write("VMBC"u8.ToArray());
        writer.Write((ushort)10);
        writer.Write((ushort)0);
        writer.Write((uint)constants.Count);
        foreach (var constant in constants)
        {
            WriteConstant(writer, constant);
        }

        writer.Write((uint)code.Length);
        writer.Write(code);
        writer.Write((uint)imports.Count);
        foreach (var import in imports)
        {
            WriteString(writer, import.Name);
            writer.Write(import.Arity);
            writer.Write((byte)import.ReturnType);
        }

        WriteTypeMap(writer, typeMap);
        writer.Write((byte)0);
        if (writeCallableMetadata is null)
        {
            writer.Write((uint)0); // script functions
            writer.Write((uint)0); // callable prototypes
            writer.Write((uint)0); // function regions
            writer.Write((uint)0); // root callable bindings
            writer.Write((uint)0); // exported callables
        }
        else
        {
            writeCallableMetadata(writer);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteConstant(BinaryWriter writer, PdVmValue value)
    {
        switch (value.Kind)
        {
            case PdVmValueKind.Null:
                writer.Write((byte)4);
                return;
            case PdVmValueKind.Int:
                writer.Write((byte)0);
                writer.Write(value.IntValue);
                return;
            case PdVmValueKind.Bool:
                writer.Write((byte)1);
                writer.Write((byte)(value.BoolValue ? 1 : 0));
                return;
            case PdVmValueKind.String:
                writer.Write((byte)2);
                WriteString(writer, value.AsString());
                return;
            case PdVmValueKind.Float:
                writer.Write((byte)3);
                writer.Write(value.FloatValue);
                return;
            case PdVmValueKind.Bytes:
                {
                    writer.Write((byte)5);
                    var bytes = value.AsBytes();
                    writer.Write((uint)bytes.Length);
                    writer.Write(bytes);
                    return;
                }
            case PdVmValueKind.Array:
                writer.Write((byte)6);
                writer.Write((uint)value.AsArray().Count);
                foreach (var item in value.AsArray())
                {
                    WriteConstant(writer, item);
                }
                return;
            case PdVmValueKind.Map:
                writer.Write((byte)7);
                writer.Write((uint)value.AsMap().Count);
                foreach (var (key, item) in value.AsMap())
                {
                    WriteConstant(writer, key);
                    WriteConstant(writer, item);
                }
                return;
            default:
                throw new InvalidOperationException($"test constant kind {value.Kind} is not supported");
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteTypeMap(BinaryWriter writer, PdVmTypeMap? typeMap)
    {
        if (typeMap is null)
        {
            writer.Write((byte)0);
            return;
        }

        writer.Write((byte)1);
        writer.Write((byte)(typeMap.StrictTypes ? 1 : 0));
        writer.Write((uint)typeMap.LocalTypes.Count);
        foreach (var localType in typeMap.LocalTypes)
        {
            writer.Write((byte)localType);
        }

        foreach (var schema in typeMap.LocalSchemas)
        {
            if (schema is null)
            {
                writer.Write((byte)0);
            }
            else
            {
                writer.Write((byte)1);
                WriteSchema(writer, schema);
            }
        }

        writer.Write((uint)typeMap.LocalTypes.Count);
        foreach (var value in typeMap.CallableSlots)
        {
            writer.Write((byte)(value ? 1 : 0));
        }

        writer.Write((uint)typeMap.LocalTypes.Count);
        foreach (var value in typeMap.OptionalSlots)
        {
            writer.Write((byte)(value ? 1 : 0));
        }

        writer.Write((uint)typeMap.OperandTypes.Count);
        foreach (var entry in typeMap.OperandTypes.OrderBy(pair => pair.Key))
        {
            writer.Write((uint)entry.Key);
            writer.Write((byte)entry.Value.Lhs);
            writer.Write((byte)entry.Value.Rhs);
        }
    }

    private static void WriteSchema(BinaryWriter writer, PdVmTypeSchema schema)
    {
        writer.Write((byte)schema.Kind);
        switch (schema.Kind)
        {
            case PdVmTypeSchemaKind.Unknown:
            case PdVmTypeSchemaKind.Null:
            case PdVmTypeSchemaKind.Int:
            case PdVmTypeSchemaKind.Float:
            case PdVmTypeSchemaKind.Number:
            case PdVmTypeSchemaKind.Bool:
            case PdVmTypeSchemaKind.String:
            case PdVmTypeSchemaKind.Bytes:
                return;
            case PdVmTypeSchemaKind.GenericParameter:
                WriteString(writer, schema.Name!);
                return;
            case PdVmTypeSchemaKind.Named:
                WriteString(writer, schema.Name!);
                WriteSchemaList(writer, schema.Items);
                return;
            case PdVmTypeSchemaKind.Array:
            case PdVmTypeSchemaKind.Map:
            case PdVmTypeSchemaKind.Optional:
                WriteSchema(writer, schema.Element!);
                return;
            case PdVmTypeSchemaKind.ArrayTuple:
                WriteSchemaList(writer, schema.Items);
                return;
            case PdVmTypeSchemaKind.ArrayTupleRest:
                WriteSchemaList(writer, schema.Items);
                WriteSchema(writer, schema.Element!);
                return;
            case PdVmTypeSchemaKind.Object:
                writer.Write((uint)schema.Fields.Count);
                foreach (var (name, value) in schema.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    WriteString(writer, name);
                    WriteSchema(writer, value);
                }
                return;
            case PdVmTypeSchemaKind.Callable:
                WriteSchemaList(writer, schema.Items);
                WriteSchema(writer, schema.Result!);
                return;
            default:
                throw new InvalidOperationException($"unsupported schema kind {schema.Kind}");
        }
    }

    private static void WriteSchemaList(BinaryWriter writer, IReadOnlyList<PdVmTypeSchema> schemas)
    {
        writer.Write((uint)schemas.Count);
        foreach (var schema in schemas)
        {
            WriteSchema(writer, schema);
        }
    }

    private static IReadOnlyList<MethodBase> ReadCalledMethods(MethodInfo method)
    {
        var body = method.GetMethodBody() ?? throw new InvalidOperationException("RunStep has no method body");
        var bytes = body.GetILAsByteArray() ?? Array.Empty<byte>();
        var calledMethods = new List<MethodBase>();
        var module = method.Module;
        var offset = 0;

        while (offset < bytes.Length)
        {
            var opCode = ReadOpCode(bytes, ref offset);
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, offset);
                if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                {
                    calledMethods.Add(module.ResolveMethod(token)!);
                }
            }

            offset += GetOperandSize(opCode.OperandType, bytes, offset);
        }

        return calledMethods;
    }

    private static IReadOnlyList<OpCode> ReadOpCodes(MethodInfo method)
    {
        var body = method.GetMethodBody() ?? throw new InvalidOperationException("method has no body");
        var bytes = body.GetILAsByteArray() ?? Array.Empty<byte>();
        var opCodes = new List<OpCode>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var opCode = ReadOpCode(bytes, ref offset);
            opCodes.Add(opCode);
            offset += GetOperandSize(opCode.OperandType, bytes, offset);
        }

        return opCodes;
    }

    private static MethodInfo GetCompiledRunStep(IPdVmProgram program) =>
        program.GetType().GetMethod(
            nameof(IPdVmProgram.RunStep),
            new[] { typeof(IPdVmHost), typeof(int) }) ??
        throw new InvalidOperationException("compiled RunStep method not found");

    private static OpCode ReadOpCode(byte[] bytes, ref int offset)
    {
        var value = bytes[offset++];
        if (value != 0xFE)
        {
            return SingleByteOpCodes[value];
        }

        return MultiByteOpCodes[bytes[offset++]];
    }

    private static int GetOperandSize(OperandType operandType, byte[] bytes, int offset)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget => 1,
            OperandType.ShortInlineI => 1,
            OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI => 4,
            OperandType.InlineBrTarget => 4,
            OperandType.InlineField => 4,
            OperandType.InlineMethod => 4,
            OperandType.InlineSig => 4,
            OperandType.InlineString => 4,
            OperandType.InlineTok => 4,
            OperandType.InlineType => 4,
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 => 8,
            OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(bytes, offset) * 4),
            _ => throw new InvalidOperationException($"unsupported operand type {operandType}"),
        };
    }

    private static MethodInfo GetBaseMethod(string name, params Type[] parameterTypes) =>
        typeof(PdVmProgramBase).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            parameterTypes,
            modifiers: null) ?? throw new InvalidOperationException($"PdVmProgramBase.{name} not found");

    private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeTable(multiByte: false);

    private static readonly OpCode[] MultiByteOpCodes = BuildOpCodeTable(multiByte: true);

    private static OpCode[] BuildOpCodeTable(bool multiByte)
    {
        var table = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = unchecked((ushort)opCode.Value);
            if (multiByte)
            {
                if ((value & 0xFF00) == 0xFE00)
                {
                    table[value & 0xFF] = opCode;
                }

                continue;
            }

            if (value <= byte.MaxValue)
            {
                table[value] = opCode;
            }
        }

        return table;
    }

    private readonly record struct CompiledProgramArtifact(IPdVmProgram Program, string AssemblyPath);

    private sealed class BytecodeBuilder
    {
        private readonly List<byte> _code = new();
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int Position, string Label)> _jumps = new();

        public int Position => _code.Count;

        public BytecodeBuilder MarkLabel(string label)
        {
            _labels[label] = _code.Count;
            return this;
        }

        public BytecodeBuilder Emit(PdVmBytecodeOpCode opCode)
        {
            _code.Add((byte)opCode);
            return this;
        }

        public BytecodeBuilder EmitLdc(uint constantIndex)
        {
            _code.Add((byte)PdVmBytecodeOpCode.Ldc);
            _code.AddRange(BitConverter.GetBytes(constantIndex));
            return this;
        }

        public BytecodeBuilder EmitLdloc(byte index)
        {
            _code.Add((byte)PdVmBytecodeOpCode.Ldloc);
            _code.Add(index);
            return this;
        }

        public BytecodeBuilder EmitStloc(byte index)
        {
            _code.Add((byte)PdVmBytecodeOpCode.Stloc);
            _code.Add(index);
            return this;
        }

        public BytecodeBuilder EmitCall(ushort callIndex, byte argCount)
        {
            _code.Add((byte)PdVmBytecodeOpCode.Call);
            _code.AddRange(BitConverter.GetBytes(callIndex));
            _code.Add(argCount);
            return this;
        }

        public BytecodeBuilder EmitCallValue(byte argCount)
        {
            _code.Add((byte)PdVmBytecodeOpCode.CallValue);
            _code.Add(argCount);
            return this;
        }

        public BytecodeBuilder EmitBrfalse(string label)
        {
            _code.Add((byte)PdVmBytecodeOpCode.Brfalse);
            _jumps.Add((_code.Count, label));
            _code.AddRange(new byte[4]);
            return this;
        }

        public BytecodeBuilder EmitBr(string label)
        {
            _code.Add((byte)PdVmBytecodeOpCode.Br);
            _jumps.Add((_code.Count, label));
            _code.AddRange(new byte[4]);
            return this;
        }

        public byte[] Build()
        {
            foreach (var (position, label) in _jumps)
            {
                if (!_labels.TryGetValue(label, out var target))
                {
                    throw new InvalidOperationException($"undefined bytecode label '{label}'");
                }

                var bytes = BitConverter.GetBytes((uint)target);
                for (var index = 0; index < bytes.Length; index++)
                {
                    _code[position + index] = bytes[index];
                }
            }

            return _code.ToArray();
        }
    }
}
