using System.Reflection;
using System.Reflection.Emit;
using PdVm.Runtime;

namespace PdVm.Compiler;

public sealed class PdVmCompileOptions
{
    public string? AssemblyName { get; init; }

    public string? ModuleName { get; init; }

    public string TypeName { get; init; } = "PdVm.Generated.Program";
}

public static class PdVmClrCompiler
{
    private static readonly ConstructorInfo ProgramBaseConstructor =
        typeof(PdVmProgramBase).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(int) },
            modifiers: null) ?? throw new InvalidOperationException("PdVmProgramBase(int) constructor not found");

    private static readonly ConstructorInfo HostImportConstructor =
        typeof(PdVmHostImport).GetConstructor(new[] { typeof(string), typeof(byte), typeof(PdVmValueType) }) ??
        throw new InvalidOperationException("PdVmHostImport constructor not found");

    private static readonly ConstructorInfo InvalidOperationConstructor =
        typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }) ??
        throw new InvalidOperationException("InvalidOperationException(string) constructor not found");

    private static readonly MethodInfo EnsureReadyToRunStepMethod =
        GetBaseMethod("EnsureReadyToRunStep");

    private static readonly MethodInfo YieldProgramMethod =
        GetBaseMethod("YieldProgram");

    private static readonly MethodInfo HaltProgramMethod =
        GetBaseMethod("HaltProgram");

    private static readonly MethodInfo SetInstructionPointerMethod =
        GetBaseMethod("SetInstructionPointer", typeof(int));

    private static readonly MethodInfo AddExecutedInstructionsMethod =
        GetBaseMethod("AddExecutedInstructions", typeof(int));

    private static readonly MethodInfo PushValueMethod =
        GetBaseMethod("PushValue", typeof(PdVmValue));

    private static readonly MethodInfo PopValueMethod =
        GetBaseMethod("PopValue");

    private static readonly MethodInfo DuplicateTopMethod =
        GetBaseMethod("DuplicateTop");

    private static readonly MethodInfo DiscardTopMethod =
        GetBaseMethod("DiscardTop");

    private static readonly MethodInfo LoadLocalValueMethod =
        GetBaseMethod("LoadLocalValue", typeof(byte));

    private static readonly MethodInfo StoreLocalValueMethod =
        GetBaseMethod("StoreLocalValue", typeof(byte));

    private static readonly MethodInfo PopBoolMethod =
        GetBaseMethod("PopBool");

    private static readonly MethodInfo DispatchCallMethod =
        GetBaseMethod(
            "DispatchCall",
            typeof(IPdVmHost),
            typeof(PdVmHostImport[]),
            typeof(ushort),
            typeof(byte),
            typeof(int),
            typeof(int));

    private static readonly MethodInfo GetLastStatusMethod =
        GetBaseMethod("GetLastStatus");

    private static readonly MethodInfo InstructionPointerGetter =
        typeof(PdVmProgramBase).GetProperty(nameof(IPdVmProgram.InstructionPointer))?.GetMethod ??
        throw new InvalidOperationException("InstructionPointer getter not found");

    private static readonly MethodInfo ValueNullMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.Null), Type.EmptyTypes) ??
        throw new InvalidOperationException("PdVmValue.Null not found");

    private static readonly MethodInfo ValueFromIntMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.FromInt), new[] { typeof(long) }) ??
        throw new InvalidOperationException("PdVmValue.FromInt not found");

    private static readonly MethodInfo ValueAsIntMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsInt), Type.EmptyTypes) ??
        throw new InvalidOperationException("PdVmValue.AsInt not found");

    private static readonly MethodInfo ValueFromFloatMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.FromFloat), new[] { typeof(double) }) ??
        throw new InvalidOperationException("PdVmValue.FromFloat not found");

    private static readonly MethodInfo ValueAsFloatMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsFloat), Type.EmptyTypes) ??
        throw new InvalidOperationException("PdVmValue.AsFloat not found");

    private static readonly MethodInfo ValueFromBoolMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.FromBool), new[] { typeof(bool) }) ??
        throw new InvalidOperationException("PdVmValue.FromBool not found");

    private static readonly MethodInfo ValueFromStringMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.FromString), new[] { typeof(string) }) ??
        throw new InvalidOperationException("PdVmValue.FromString not found");

    private static readonly MethodInfo ValueAsStringMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsString), Type.EmptyTypes) ??
        throw new InvalidOperationException("PdVmValue.AsString not found");

    private static readonly MethodInfo ValueFromBytesMethod =
        typeof(PdVmValue).GetMethod(nameof(PdVmValue.FromBytes), new[] { typeof(IEnumerable<byte>) }) ??
        throw new InvalidOperationException("PdVmValue.FromBytes not found");

    private static readonly MethodInfo ValidateShiftAmountMethod =
        typeof(PdVmOps).GetMethod(nameof(PdVmOps.ValidateShiftAmount), new[] { typeof(long) }) ??
        throw new InvalidOperationException("PdVmOps.ValidateShiftAmount not found");

    private static readonly MethodInfo StringConcatMethod =
        typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) }) ??
        throw new InvalidOperationException("string.Concat(string, string) not found");

    private static readonly MethodInfo StringEqualsMethod =
        typeof(string).GetMethod(
            nameof(string.Equals),
            new[] { typeof(string), typeof(string), typeof(StringComparison) }) ??
        throw new InvalidOperationException("string.Equals(string, string, StringComparison) not found");

    private static readonly Dictionary<PdVmBytecodeOpCode, MethodInfo> UnaryOpcodeMethods = new()
    {
        [PdVmBytecodeOpCode.Neg] = GetBaseMethod("ApplyNeg"),
        [PdVmBytecodeOpCode.Not] = GetBaseMethod("ApplyNot"),
    };

    private static readonly Dictionary<PdVmBytecodeOpCode, MethodInfo> BinaryOpcodeMethods = new()
    {
        [PdVmBytecodeOpCode.Add] = GetBaseMethod("ApplyAdd"),
        [PdVmBytecodeOpCode.Sub] = GetBaseMethod("ApplySub"),
        [PdVmBytecodeOpCode.Mul] = GetBaseMethod("ApplyMul"),
        [PdVmBytecodeOpCode.Div] = GetBaseMethod("ApplyDiv"),
        [PdVmBytecodeOpCode.Mod] = GetBaseMethod("ApplyMod"),
        [PdVmBytecodeOpCode.Ceq] = GetBaseMethod("ApplyEqual"),
        [PdVmBytecodeOpCode.Clt] = GetBaseMethod("ApplyLessThan"),
        [PdVmBytecodeOpCode.Cgt] = GetBaseMethod("ApplyGreaterThan"),
        [PdVmBytecodeOpCode.Shl] = GetBaseMethod("ApplyShl"),
        [PdVmBytecodeOpCode.Shr] = GetBaseMethod("ApplyShr"),
        [PdVmBytecodeOpCode.Lshr] = GetBaseMethod("ApplyLshr"),
        [PdVmBytecodeOpCode.And] = GetBaseMethod("ApplyAnd"),
        [PdVmBytecodeOpCode.Or] = GetBaseMethod("ApplyOr"),
    };

    private static readonly Dictionary<PdVmBuiltin, (MethodInfo Method, bool ReturnsValue)> IntrinsicBuiltins = new()
    {
        [PdVmBuiltin.Len] = (GetBuiltinMethod(nameof(PdVmBuiltins.LenValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Slice] = (GetBuiltinMethod(nameof(PdVmBuiltins.SliceValue), typeof(PdVmValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Concat] = (GetBuiltinMethod(nameof(PdVmBuiltins.ConcatValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ArrayNew] = (GetBuiltinMethod(nameof(PdVmBuiltins.ArrayNewValue)), true),
        [PdVmBuiltin.ArrayPush] = (GetBuiltinMethod(nameof(PdVmBuiltins.ArrayPushValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.MapNew] = (GetBuiltinMethod(nameof(PdVmBuiltins.MapNewValue)), true),
        [PdVmBuiltin.Get] = (GetBuiltinMethod(nameof(PdVmBuiltins.GetValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Has] = (GetBuiltinMethod(nameof(PdVmBuiltins.HasValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Set] = (GetBuiltinMethod(nameof(PdVmBuiltins.SetValue), typeof(PdVmValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Keys] = (GetBuiltinMethod(nameof(PdVmBuiltins.KeysValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Count] = (GetBuiltinMethod(nameof(PdVmBuiltins.CountValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.FormatTemplate] = (GetBuiltinMethod(nameof(PdVmBuiltins.FormatTemplateValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ToString] = (GetBuiltinMethod(nameof(PdVmBuiltins.ToStringValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.TypeOf] = (GetBuiltinMethod(nameof(PdVmBuiltins.TypeOfValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.Assert] = (GetBuiltinMethod(nameof(PdVmBuiltins.AssertValue), typeof(PdVmValue)), false),
        [PdVmBuiltin.BytesFromUtf8] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesFromUtf8Value), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesToUtf8] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesToUtf8Value), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesToUtf8Lossy] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesToUtf8LossyValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesFromHex] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesFromHexValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesToHex] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesToHexValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesFromBase64] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesFromBase64Value), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesToBase64] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesToBase64Value), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesFromArrayU8] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesFromArrayU8Value), typeof(PdVmValue)), true),
        [PdVmBuiltin.BytesToArrayU8] = (GetBuiltinMethod(nameof(PdVmBuiltins.BytesToArrayU8Value), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoOpen] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoOpenValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoPopen] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoPopenValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoReadAll] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoReadAllValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoReadLine] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoReadLineValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoWrite] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoWriteValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoFlush] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoFlushValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoClose] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoCloseValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.IoExists] = (GetBuiltinMethod(nameof(PdVmBuiltins.IoExistsValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ReMatch] = (GetBuiltinMethod(nameof(PdVmBuiltins.ReMatchValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ReFind] = (GetBuiltinMethod(nameof(PdVmBuiltins.ReFindValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ReReplace] = (GetBuiltinMethod(nameof(PdVmBuiltins.ReReplaceValue), typeof(PdVmValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ReSplit] = (GetBuiltinMethod(nameof(PdVmBuiltins.ReSplitValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.ReCaptures] = (GetBuiltinMethod(nameof(PdVmBuiltins.ReCapturesValue), typeof(PdVmValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.JsonEncode] = (GetBuiltinMethod(nameof(PdVmBuiltins.JsonEncodeValue), typeof(PdVmValue)), true),
        [PdVmBuiltin.JsonDecode] = (GetBuiltinMethod(nameof(PdVmBuiltins.JsonDecodeValue), typeof(PdVmValue)), true),
    };

    private readonly record struct PdVmTypedTemps(
        LocalBuilder Int0,
        LocalBuilder Int1,
        LocalBuilder Float0,
        LocalBuilder Float1,
        LocalBuilder Bool0,
        LocalBuilder Bool1,
        LocalBuilder String0,
        LocalBuilder String1,
        LocalBuilder ShiftAmount);

    public static string CompileFile(string inputPath, string outputPath, PdVmCompileOptions? options = null)
    {
        if (inputPath is null)
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        return Compile(PdVmVmbcReader.ReadFile(inputPath), outputPath, options);
    }

    public static string Compile(byte[] bytes, string outputPath, PdVmCompileOptions? options = null) =>
        Compile(PdVmVmbcReader.ReadBytes(bytes), outputPath, options);

    public static string Compile(PdVmProgramModel program, string outputPath, PdVmCompileOptions? options = null)
    {
        if (program is null)
        {
            throw new ArgumentNullException(nameof(program));
        }

        if (outputPath is null)
        {
            throw new ArgumentNullException(nameof(outputPath));
        }

        options ??= new PdVmCompileOptions();
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);

        var assemblyName = string.IsNullOrWhiteSpace(options.AssemblyName)
            ? Path.GetFileNameWithoutExtension(fullOutputPath)
            : options.AssemblyName;
        var moduleName = string.IsNullOrWhiteSpace(options.ModuleName)
            ? Path.GetFileName(fullOutputPath)
            : options.ModuleName;

        var assemblyBuilder = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleName);
        var typeBuilder = moduleBuilder.DefineType(
            options.TypeName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
            typeof(PdVmProgramBase));

        var constantsField = typeBuilder.DefineField(
            "s_constants",
            typeof(PdVmValue[]),
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
        var importsField = typeBuilder.DefineField(
            "s_imports",
            typeof(PdVmHostImport[]),
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);

        EmitTypeInitializer(typeBuilder, constantsField, importsField, program);
        EmitConstructor(typeBuilder, program.LocalCount);
        EmitRunStep(typeBuilder, constantsField, importsField, program);
        typeBuilder.CreateType();
        assemblyBuilder.Save(fullOutputPath);
        return fullOutputPath;
    }

    private static void EmitTypeInitializer(
        TypeBuilder typeBuilder,
        FieldBuilder constantsField,
        FieldBuilder importsField,
        PdVmProgramModel program)
    {
        var cctor = typeBuilder.DefineTypeInitializer();
        var il = cctor.GetILGenerator();

        EmitInt32(il, program.Constants.Count);
        il.Emit(OpCodes.Newarr, typeof(PdVmValue));
        for (var index = 0; index < program.Constants.Count; index++)
        {
            il.Emit(OpCodes.Dup);
            EmitInt32(il, index);
            EmitConstant(il, program.Constants[index]);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Stsfld, constantsField);

        EmitInt32(il, program.Imports.Count);
        il.Emit(OpCodes.Newarr, typeof(PdVmHostImport));
        for (var index = 0; index < program.Imports.Count; index++)
        {
            var import = program.Imports[index];
            il.Emit(OpCodes.Dup);
            EmitInt32(il, index);
            il.Emit(OpCodes.Ldstr, import.Name);
            EmitInt32(il, import.Arity);
            EmitInt32(il, (int)import.ReturnType);
            il.Emit(OpCodes.Newobj, HostImportConstructor);
            il.Emit(OpCodes.Stelem_Ref);
        }
        il.Emit(OpCodes.Stsfld, importsField);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitConstructor(TypeBuilder typeBuilder, int localCount)
    {
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.HasThis,
            Type.EmptyTypes);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        EmitInt32(il, localCount);
        il.Emit(OpCodes.Call, ProgramBaseConstructor);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitRunStep(
        TypeBuilder typeBuilder,
        FieldBuilder constantsField,
        FieldBuilder importsField,
        PdVmProgramModel program)
    {
        var method = typeBuilder.DefineMethod(
            nameof(IPdVmProgram.RunStep),
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            typeof(PdVmStatus),
            new[] { typeof(IPdVmHost) });
        typeBuilder.DefineMethodOverride(method, typeof(IPdVmProgram).GetMethod(nameof(IPdVmProgram.RunStep))!);

        var il = method.GetILGenerator();
        var instructionPointerLocal = il.DeclareLocal(typeof(int));
        var executedInstructionsLocal = il.DeclareLocal(typeof(int));
        var tmp0 = il.DeclareLocal(typeof(PdVmValue));
        var tmp1 = il.DeclareLocal(typeof(PdVmValue));
        var tmp2 = il.DeclareLocal(typeof(PdVmValue));
        var typedTemps = new PdVmTypedTemps(
            il.DeclareLocal(typeof(long)),
            il.DeclareLocal(typeof(long)),
            il.DeclareLocal(typeof(double)),
            il.DeclareLocal(typeof(double)),
            il.DeclareLocal(typeof(bool)),
            il.DeclareLocal(typeof(bool)),
            il.DeclareLocal(typeof(string)),
            il.DeclareLocal(typeof(string)),
            il.DeclareLocal(typeof(int)));
        var labels = program.Instructions.ToDictionary(instruction => instruction.Offset, _ => il.DefineLabel());

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, EnsureReadyToRunStepMethod);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, InstructionPointerGetter);
        il.Emit(OpCodes.Stloc, instructionPointerLocal);
        EmitInt32(il, 0);
        il.Emit(OpCodes.Stloc, executedInstructionsLocal);

        foreach (var instruction in program.Instructions)
        {
            il.Emit(OpCodes.Ldloc, instructionPointerLocal);
            EmitInt32(il, instruction.Offset);
            il.Emit(OpCodes.Beq, labels[instruction.Offset]);
        }

        EmitThrowInvalidInstructionPointer(il);

        foreach (var instruction in program.Instructions)
        {
            il.MarkLabel(labels[instruction.Offset]);
            EmitInstructionPrefix(il, executedInstructionsLocal, instruction.Offset);
            EmitInstruction(
                il,
                constantsField,
                importsField,
                program,
                instruction,
                labels,
                executedInstructionsLocal,
                typedTemps,
                tmp0,
                tmp1,
                tmp2);
        }

        EmitThrowInvalidInstructionPointer(il);
    }

    private static void EmitInstruction(
        ILGenerator il,
        FieldBuilder constantsField,
        FieldBuilder importsField,
        PdVmProgramModel program,
        PdVmInstruction instruction,
        IReadOnlyDictionary<int, Label> labels,
        LocalBuilder executedInstructionsLocal,
        PdVmTypedTemps typedTemps,
        LocalBuilder tmp0,
        LocalBuilder tmp1,
        LocalBuilder tmp2)
    {
        if (TryEmitTypedInstruction(il, program, instruction, typedTemps))
        {
            return;
        }

        if (BinaryOpcodeMethods.TryGetValue(instruction.OpCode, out var binaryMethod))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, binaryMethod);
            return;
        }

        if (UnaryOpcodeMethods.TryGetValue(instruction.OpCode, out var unaryMethod))
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, unaryMethod);
            return;
        }

        switch (instruction.OpCode)
        {
            case PdVmBytecodeOpCode.Nop:
                return;
            case PdVmBytecodeOpCode.Ret:
                il.Emit(OpCodes.Ldarg_0);
                EmitInt32(il, instruction.Offset);
                il.Emit(OpCodes.Call, SetInstructionPointerMethod);
                EmitReturnStatus(il, executedInstructionsLocal, HaltProgramMethod);
                return;
            case PdVmBytecodeOpCode.Ldc:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldsfld, constantsField);
                EmitInt32(il, instruction.ConstantIndex!.Value);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Call, PushValueMethod);
                return;
            case PdVmBytecodeOpCode.Br:
                EmitTransfer(il, labels, executedInstructionsLocal, instruction.Offset, instruction.JumpTarget!.Value);
                return;
            case PdVmBytecodeOpCode.Brfalse:
            {
                var fallthrough = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, PopBoolMethod);
                il.Emit(OpCodes.Brtrue, fallthrough);
                EmitTransfer(
                    il,
                    labels,
                    executedInstructionsLocal,
                    instruction.Offset,
                    instruction.JumpTarget!.Value);
                il.MarkLabel(fallthrough);
                return;
            }
            case PdVmBytecodeOpCode.Pop:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, DiscardTopMethod);
                return;
            case PdVmBytecodeOpCode.Dup:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, DuplicateTopMethod);
                return;
            case PdVmBytecodeOpCode.Ldloc:
                il.Emit(OpCodes.Ldarg_0);
                EmitInt32(il, instruction.LocalIndex!.Value);
                il.Emit(OpCodes.Call, LoadLocalValueMethod);
                return;
            case PdVmBytecodeOpCode.Stloc:
                il.Emit(OpCodes.Ldarg_0);
                EmitInt32(il, instruction.LocalIndex!.Value);
                il.Emit(OpCodes.Call, StoreLocalValueMethod);
                return;
            case PdVmBytecodeOpCode.Call:
                EmitCallInstruction(
                    il,
                    importsField,
                    instruction,
                    executedInstructionsLocal,
                    tmp0,
                    tmp1,
                    tmp2);
                return;
            default:
                throw new PdVmCompilerException($"unsupported opcode {instruction.OpCode}");
        }
    }

    private static bool TryEmitTypedInstruction(
        ILGenerator il,
        PdVmProgramModel program,
        PdVmInstruction instruction,
        PdVmTypedTemps typedTemps)
    {
        var operandTypes = GetOperandTypes(program, instruction.Offset);

        switch (instruction.OpCode)
        {
            case PdVmBytecodeOpCode.Add:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Add);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Add);
                    EmitPushValueFromFactory(il, ValueFromFloatMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.String && operandTypes.Rhs == PdVmValueType.String)
                {
                    EmitPopStringPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.String0);
                    il.Emit(OpCodes.Ldloc, typedTemps.String1);
                    il.Emit(OpCodes.Call, StringConcatMethod);
                    EmitPushValueFromFactory(il, ValueFromStringMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Sub:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Sub);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Sub);
                    EmitPushValueFromFactory(il, ValueFromFloatMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Mul:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Mul);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Mul);
                    EmitPushValueFromFactory(il, ValueFromFloatMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Div:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    EmitGuardIntDivisorNotZero(il, typedTemps.Int1, "division by zero");
                    EmitGuardIntMinValueOverflow(il, typedTemps.Int0, typedTemps.Int1, "integer overflow in division");
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Div);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Div);
                    EmitPushValueFromFactory(il, ValueFromFloatMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Mod:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    EmitGuardIntDivisorNotZero(il, typedTemps.Int1, "division by zero");
                    EmitGuardIntMinValueOverflow(il, typedTemps.Int0, typedTemps.Int1, "integer overflow in remainder");
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Rem);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Rem);
                    EmitPushValueFromFactory(il, ValueFromFloatMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Neg:
                if (operandTypes.Lhs == PdVmValueType.Int)
                {
                    EmitPopInt(il, typedTemps.Int0);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Neg);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float)
                {
                    EmitPopFloat(il, typedTemps.Float0);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Neg);
                    EmitPushValueFromFactory(il, ValueFromFloatMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Ceq:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Ceq);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Ceq);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Bool && operandTypes.Rhs == PdVmValueType.Bool)
                {
                    EmitPopBoolPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool1);
                    il.Emit(OpCodes.Ceq);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.String && operandTypes.Rhs == PdVmValueType.String)
                {
                    EmitPopStringPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.String0);
                    il.Emit(OpCodes.Ldloc, typedTemps.String1);
                    EmitInt32(il, (int)StringComparison.Ordinal);
                    il.Emit(OpCodes.Call, StringEqualsMethod);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Clt:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Clt);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Clt);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Cgt:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int1);
                    il.Emit(OpCodes.Cgt);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
                {
                    EmitPopFloatPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Float1);
                    il.Emit(OpCodes.Cgt);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Shl:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    EmitLoadValidatedShiftAmount(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.ShiftAmount);
                    il.Emit(OpCodes.Shl);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Shr:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    EmitLoadValidatedShiftAmount(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.ShiftAmount);
                    il.Emit(OpCodes.Shr);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Lshr:
                if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
                {
                    EmitPopIntPair(il, typedTemps);
                    EmitLoadValidatedShiftAmount(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.ShiftAmount);
                    il.Emit(OpCodes.Shr_Un);
                    EmitPushValueFromFactory(il, ValueFromIntMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.And:
                if (operandTypes.Lhs == PdVmValueType.Bool && operandTypes.Rhs == PdVmValueType.Bool)
                {
                    EmitPopBoolPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool1);
                    il.Emit(OpCodes.And);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Or:
                if (operandTypes.Lhs == PdVmValueType.Bool && operandTypes.Rhs == PdVmValueType.Bool)
                {
                    EmitPopBoolPair(il, typedTemps);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool1);
                    il.Emit(OpCodes.Or);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                return false;
            case PdVmBytecodeOpCode.Not:
                if (operandTypes.Lhs == PdVmValueType.Bool)
                {
                    EmitPopBool(il, typedTemps.Bool0);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldloc, typedTemps.Bool0);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ceq);
                    EmitPushValueFromFactory(il, ValueFromBoolMethod);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static PdVmOperandTypes GetOperandTypes(PdVmProgramModel program, int offset)
    {
        if (program.TypeMap is not null &&
            program.TypeMap.OperandTypes.TryGetValue(offset, out var operandTypes))
        {
            return operandTypes;
        }

        return new PdVmOperandTypes(PdVmValueType.Unknown, PdVmValueType.Unknown);
    }

    private static void EmitPopIntPair(ILGenerator il, PdVmTypedTemps typedTemps)
    {
        EmitPopInt(il, typedTemps.Int1);
        EmitPopInt(il, typedTemps.Int0);
    }

    private static void EmitPopInt(ILGenerator il, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, PopValueMethod);
        il.Emit(OpCodes.Call, ValueAsIntMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitPopFloatPair(ILGenerator il, PdVmTypedTemps typedTemps)
    {
        EmitPopFloat(il, typedTemps.Float1);
        EmitPopFloat(il, typedTemps.Float0);
    }

    private static void EmitPopFloat(ILGenerator il, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, PopValueMethod);
        il.Emit(OpCodes.Call, ValueAsFloatMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitPopBoolPair(ILGenerator il, PdVmTypedTemps typedTemps)
    {
        EmitPopBool(il, typedTemps.Bool1);
        EmitPopBool(il, typedTemps.Bool0);
    }

    private static void EmitPopBool(ILGenerator il, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, PopBoolMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitPopStringPair(ILGenerator il, PdVmTypedTemps typedTemps)
    {
        EmitPopString(il, typedTemps.String1);
        EmitPopString(il, typedTemps.String0);
    }

    private static void EmitPopString(ILGenerator il, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, PopValueMethod);
        il.Emit(OpCodes.Call, ValueAsStringMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitLoadValidatedShiftAmount(ILGenerator il, PdVmTypedTemps typedTemps)
    {
        il.Emit(OpCodes.Ldloc, typedTemps.Int1);
        il.Emit(OpCodes.Call, ValidateShiftAmountMethod);
        il.Emit(OpCodes.Stloc, typedTemps.ShiftAmount);
    }

    private static void EmitGuardIntDivisorNotZero(ILGenerator il, LocalBuilder divisor, string message)
    {
        var safeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, divisor);
        il.Emit(OpCodes.Ldc_I8, 0L);
        il.Emit(OpCodes.Bne_Un, safeLabel);
        EmitThrowInvalidOperation(il, message);
        il.MarkLabel(safeLabel);
    }

    private static void EmitGuardIntMinValueOverflow(
        ILGenerator il,
        LocalBuilder dividend,
        LocalBuilder divisor,
        string message)
    {
        var safeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dividend);
        il.Emit(OpCodes.Ldc_I8, long.MinValue);
        il.Emit(OpCodes.Bne_Un, safeLabel);
        il.Emit(OpCodes.Ldloc, divisor);
        il.Emit(OpCodes.Ldc_I8, -1L);
        il.Emit(OpCodes.Bne_Un, safeLabel);
        EmitThrowInvalidOperation(il, message);
        il.MarkLabel(safeLabel);
    }

    private static void EmitPushValueFromFactory(ILGenerator il, MethodInfo factoryMethod)
    {
        il.Emit(OpCodes.Call, factoryMethod);
        il.Emit(OpCodes.Call, PushValueMethod);
    }

    private static void EmitCallInstruction(
        ILGenerator il,
        FieldBuilder importsField,
        PdVmInstruction instruction,
        LocalBuilder executedInstructionsLocal,
        LocalBuilder tmp0,
        LocalBuilder tmp1,
        LocalBuilder tmp2)
    {
        if (instruction.CallIndex is ushort callIndex &&
            PdVmBuiltins.TryGetBuiltin(callIndex, out var builtin) &&
            IntrinsicBuiltins.TryGetValue(builtin, out var intrinsic))
        {
            EmitPopArgs(il, instruction.ArgCount!.Value, tmp0, tmp1, tmp2);
            if (intrinsic.ReturnsValue)
            {
                il.Emit(OpCodes.Ldarg_0);
                EmitIntrinsicArgs(il, instruction.ArgCount!.Value, tmp0, tmp1, tmp2);
                il.Emit(OpCodes.Call, intrinsic.Method);
                il.Emit(OpCodes.Call, PushValueMethod);
            }
            else
            {
                EmitIntrinsicArgs(il, instruction.ArgCount!.Value, tmp0, tmp1, tmp2);
                il.Emit(OpCodes.Call, intrinsic.Method);
            }

            return;
        }

        var continueLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldsfld, importsField);
        EmitInt32(il, instruction.CallIndex!.Value);
        EmitInt32(il, instruction.ArgCount!.Value);
        EmitInt32(il, instruction.Offset);
        EmitInt32(il, instruction.NextOffset);
        il.Emit(OpCodes.Call, DispatchCallMethod);
        il.Emit(OpCodes.Brfalse, continueLabel);
        EmitReturnStatus(il, executedInstructionsLocal, GetLastStatusMethod);
        il.MarkLabel(continueLabel);
    }

    private static void EmitPopArgs(
        ILGenerator il,
        int argc,
        LocalBuilder tmp0,
        LocalBuilder tmp1,
        LocalBuilder tmp2)
    {
        var locals = new[] { tmp0, tmp1, tmp2 };
        if (argc < 0 || argc > locals.Length)
        {
            throw new PdVmCompilerException($"intrinsic arity {argc} is not supported");
        }

        for (var index = argc - 1; index >= 0; index--)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, PopValueMethod);
            il.Emit(OpCodes.Stloc, locals[index]);
        }
    }

    private static void EmitIntrinsicArgs(
        ILGenerator il,
        int argc,
        LocalBuilder tmp0,
        LocalBuilder tmp1,
        LocalBuilder tmp2)
    {
        if (argc >= 1)
        {
            il.Emit(OpCodes.Ldloc, tmp0);
        }

        if (argc >= 2)
        {
            il.Emit(OpCodes.Ldloc, tmp1);
        }

        if (argc >= 3)
        {
            il.Emit(OpCodes.Ldloc, tmp2);
        }
    }

    private static void EmitInstructionPrefix(
        ILGenerator il,
        LocalBuilder executedInstructionsLocal,
        int offset)
    {
        il.Emit(OpCodes.Ldloc, executedInstructionsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, executedInstructionsLocal);
    }

    private static void EmitTransfer(
        ILGenerator il,
        IReadOnlyDictionary<int, Label> labels,
        LocalBuilder executedInstructionsLocal,
        int currentOffset,
        int targetOffset)
    {
        if (targetOffset > currentOffset)
        {
            il.Emit(OpCodes.Br, labels[targetOffset]);
            return;
        }

        il.Emit(OpCodes.Ldarg_0);
        EmitInt32(il, targetOffset);
        il.Emit(OpCodes.Call, SetInstructionPointerMethod);
        EmitReturnStatus(il, executedInstructionsLocal, YieldProgramMethod);
    }

    private static void EmitReturnStatus(
        ILGenerator il,
        LocalBuilder executedInstructionsLocal,
        MethodInfo statusMethod)
    {
        EmitCommitExecutedInstructions(il, executedInstructionsLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, statusMethod);
        il.Emit(OpCodes.Ret);
    }

    private static void EmitCommitExecutedInstructions(ILGenerator il, LocalBuilder executedInstructionsLocal)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, executedInstructionsLocal);
        il.Emit(OpCodes.Call, AddExecutedInstructionsMethod);
    }

    private static void EmitConstant(ILGenerator il, PdVmValue value)
    {
        switch (value.Kind)
        {
            case PdVmValueKind.Null:
                il.Emit(OpCodes.Call, ValueNullMethod);
                return;
            case PdVmValueKind.Int:
                il.Emit(OpCodes.Ldc_I8, value.IntValue);
                il.Emit(OpCodes.Call, ValueFromIntMethod);
                return;
            case PdVmValueKind.Float:
                il.Emit(OpCodes.Ldc_R8, value.FloatValue);
                il.Emit(OpCodes.Call, ValueFromFloatMethod);
                return;
            case PdVmValueKind.Bool:
                EmitInt32(il, value.BoolValue ? 1 : 0);
                il.Emit(OpCodes.Call, ValueFromBoolMethod);
                return;
            case PdVmValueKind.String:
                il.Emit(OpCodes.Ldstr, value.AsString());
                il.Emit(OpCodes.Call, ValueFromStringMethod);
                return;
            case PdVmValueKind.Bytes:
            {
                var bytes = value.AsBytes();
                EmitInt32(il, bytes.Length);
                il.Emit(OpCodes.Newarr, typeof(byte));
                for (var index = 0; index < bytes.Length; index++)
                {
                    il.Emit(OpCodes.Dup);
                    EmitInt32(il, index);
                    EmitInt32(il, bytes[index]);
                    il.Emit(OpCodes.Stelem_I1);
                }
                il.Emit(OpCodes.Call, ValueFromBytesMethod);
                return;
            }
            default:
                throw new PdVmCompilerException($"VMBC constant kind {value.Kind} is not supported");
        }
    }

    private static void EmitThrowInvalidInstructionPointer(ILGenerator il)
    {
        il.Emit(OpCodes.Ldstr, "invalid instruction pointer");
        il.Emit(OpCodes.Newobj, InvalidOperationConstructor);
        il.Emit(OpCodes.Throw);
    }

    private static void EmitThrowInvalidOperation(ILGenerator il, string message)
    {
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, InvalidOperationConstructor);
        il.Emit(OpCodes.Throw);
    }

    private static void EmitInt32(ILGenerator il, int value)
    {
        switch (value)
        {
            case -1:
                il.Emit(OpCodes.Ldc_I4_M1);
                return;
            case 0:
                il.Emit(OpCodes.Ldc_I4_0);
                return;
            case 1:
                il.Emit(OpCodes.Ldc_I4_1);
                return;
            case 2:
                il.Emit(OpCodes.Ldc_I4_2);
                return;
            case 3:
                il.Emit(OpCodes.Ldc_I4_3);
                return;
            case 4:
                il.Emit(OpCodes.Ldc_I4_4);
                return;
            case 5:
                il.Emit(OpCodes.Ldc_I4_5);
                return;
            case 6:
                il.Emit(OpCodes.Ldc_I4_6);
                return;
            case 7:
                il.Emit(OpCodes.Ldc_I4_7);
                return;
            case 8:
                il.Emit(OpCodes.Ldc_I4_8);
                return;
        }

        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
            return;
        }

        il.Emit(OpCodes.Ldc_I4, value);
    }

    private static MethodInfo GetBaseMethod(string name, params Type[] parameterTypes) =>
        typeof(PdVmProgramBase).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null) ?? throw new InvalidOperationException($"PdVmProgramBase.{name} not found");

    private static MethodInfo GetBuiltinMethod(string name, params Type[] parameterTypes) =>
        typeof(PdVmBuiltins).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: parameterTypes,
            modifiers: null) ?? throw new InvalidOperationException($"PdVmBuiltins.{name} not found");
}
