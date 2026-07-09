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
        var calledMethods = ReadCalledMethods(artifact.Program.GetType().GetMethod(nameof(IPdVmProgram.RunStep))!);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(42, Assert.Single(artifact.Program.Stack).AsInt());
        Assert.DoesNotContain(calledMethods, method => method == GetBaseMethod("ApplyAdd"));
        Assert.Contains(calledMethods, method => method == typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsInt), Type.EmptyTypes));
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
        var calledMethods = ReadCalledMethods(artifact.Program.GetType().GetMethod(nameof(IPdVmProgram.RunStep))!);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(42, Assert.Single(artifact.Program.Stack).AsInt());
        Assert.Contains(calledMethods, method => method == GetBaseMethod("ApplyAdd"));
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
        var calledMethods = ReadCalledMethods(artifact.Program.GetType().GetMethod(nameof(IPdVmProgram.RunStep))!);

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
        PdVmTypeMap? typeMap = null)
    {
        var payload = EncodeVmbc(constants, code, imports ?? Array.Empty<PdVmHostImport>(), typeMap);
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
        PdVmTypeMap? typeMap = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write("VMBC"u8.ToArray());
        writer.Write((ushort)8);
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
        writer.Write((byte)0);
        writer.Write((uint)typeMap.LocalTypes.Count);
        foreach (var localType in typeMap.LocalTypes)
        {
            writer.Write((byte)localType);
        }

        foreach (var _ in typeMap.LocalTypes)
        {
            writer.Write((byte)0);
        }

        writer.Write((uint)typeMap.LocalTypes.Count);
        foreach (var _ in typeMap.LocalTypes)
        {
            writer.Write((byte)0);
        }

        writer.Write((uint)typeMap.LocalTypes.Count);
        foreach (var _ in typeMap.LocalTypes)
        {
            writer.Write((byte)0);
        }

        writer.Write((uint)typeMap.OperandTypes.Count);
        foreach (var entry in typeMap.OperandTypes.OrderBy(pair => pair.Key))
        {
            writer.Write((uint)entry.Key);
            writer.Write((byte)entry.Value.Lhs);
            writer.Write((byte)entry.Value.Rhs);
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
