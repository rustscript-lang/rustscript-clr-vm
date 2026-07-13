using System.Reflection;
using System.Reflection.Emit;
using PdVm.Runtime;

namespace PdVm.Compiler;

public sealed class PdVmCompileOptions
{
    public string? AssemblyName { get; init; }

    public string? ModuleName { get; init; }

    public string TypeName { get; init; } = "PdVm.Generated.Program";

    public bool CopyRuntimeAssembly { get; init; } = true;

    public IReadOnlyList<Type> ReferencedClrTypes { get; init; } = [];

    public IReadOnlyList<string> AdditionalRuntimeAssemblyPaths { get; init; } = [];
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

    private static readonly MethodInfo HaltProgramMethod =
        GetBaseMethod("HaltProgram");

    private static readonly MethodInfo SetInstructionPointerMethod =
        GetBaseMethod("SetInstructionPointer", typeof(int));

    private static readonly MethodInfo AddExecutedInstructionsMethod =
        GetBaseMethod("AddExecutedInstructions", typeof(int));

    private static readonly MethodInfo ThrowInstructionBudgetExceededMethod =
        GetBaseMethod("ThrowInstructionBudgetExceeded", typeof(int));

    private static readonly MethodInfo PushValueMethod =
        GetBaseMethod("PushValue", typeof(PdVmValue));

    private static readonly MethodInfo ResetStackMethod =
        GetBaseMethod("ResetStack");

    private static readonly MethodInfo PopValueMethod =
        GetBaseMethod("PopValue");

    private static readonly MethodInfo SetLocalValueMethod =
        GetBaseMethod("SetLocalValue", typeof(byte), typeof(PdVmValue));

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
        [PdVmBytecodeOpCode.Neg] = GetOpsMethod(nameof(PdVmOps.Neg)),
        [PdVmBytecodeOpCode.Not] = GetOpsMethod(nameof(PdVmOps.Not)),
    };

    private static readonly Dictionary<PdVmBytecodeOpCode, MethodInfo> BinaryOpcodeMethods = new()
    {
        [PdVmBytecodeOpCode.Add] = GetOpsMethod(nameof(PdVmOps.Add), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Sub] = GetOpsMethod(nameof(PdVmOps.Sub), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Mul] = GetOpsMethod(nameof(PdVmOps.Mul), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Div] = GetOpsMethod(nameof(PdVmOps.Div), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Mod] = GetOpsMethod(nameof(PdVmOps.Mod), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Ceq] = GetOpsMethod(nameof(PdVmOps.Ceq), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Clt] = GetOpsMethod(nameof(PdVmOps.Clt), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Cgt] = GetOpsMethod(nameof(PdVmOps.Cgt), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Shl] = GetOpsMethod(nameof(PdVmOps.Shl), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Shr] = GetOpsMethod(nameof(PdVmOps.Shr), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Lshr] = GetOpsMethod(nameof(PdVmOps.Lshr), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.And] = GetOpsMethod(nameof(PdVmOps.And), typeof(PdVmValue), typeof(PdVmValue)),
        [PdVmBytecodeOpCode.Or] = GetOpsMethod(nameof(PdVmOps.Or), typeof(PdVmValue), typeof(PdVmValue)),
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

        var stackLayout = PdVmStackAnalyzer.Analyze(program);

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

        foreach (var (referencedType, index) in options.ReferencedClrTypes
                     .Where(type => type.Assembly != typeof(PdVmProgramBase).Assembly)
                     .Distinct()
                     .OrderBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal)
                     .Select((type, index) => (type, index)))
        {
            // Keep a real AssemblyRef for each typed CLR import even though calls are
            // dispatched through versioned descriptors at runtime.
            typeBuilder.DefineField(
                $"s_clr_reference_{index}",
                referencedType,
                FieldAttributes.Private | FieldAttributes.Static);
        }

        var constantsField = typeBuilder.DefineField(
            "s_constants",
            typeof(PdVmValue[]),
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
        var importsField = typeBuilder.DefineField(
            "s_imports",
            typeof(PdVmHostImport[]),
            FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
        var localFields = Enumerable.Range(0, program.LocalCount)
            .Select(index => typeBuilder.DefineField(
                $"_local{index}",
                typeof(PdVmValue),
                FieldAttributes.Private))
            .ToArray();

        EmitTypeInitializer(typeBuilder, constantsField, importsField, program);
        EmitConstructor(typeBuilder, program.LocalCount, localFields);
        EmitRunStep(typeBuilder, constantsField, importsField, localFields, program, stackLayout);
        typeBuilder.CreateType();
        assemblyBuilder.Save(fullOutputPath);
        if (options.CopyRuntimeAssembly)
        {
            CopyRuntimeAssembly(fullOutputPath);
        }
        CopyAdditionalRuntimeAssemblies(fullOutputPath, options.AdditionalRuntimeAssemblyPaths);
        return fullOutputPath;
    }

    private static void CopyRuntimeAssembly(string programAssemblyPath)
    {
        var runtimeSource = typeof(PdVmProgramBase).Assembly.Location;
        if (string.IsNullOrWhiteSpace(runtimeSource))
        {
            throw new InvalidOperationException("PdVm.Runtime assembly has no physical location");
        }

        var runtimeDestination = Path.Combine(
            Path.GetDirectoryName(programAssemblyPath)!,
            Path.GetFileName(runtimeSource));
        if (!string.Equals(
                Path.GetFullPath(runtimeSource),
                Path.GetFullPath(runtimeDestination),
                StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(runtimeSource, runtimeDestination, overwrite: true);
        }
    }

    private static void CopyAdditionalRuntimeAssemblies(
        string programAssemblyPath,
        IReadOnlyList<string> assemblyPaths)
    {
        var outputDirectory = Path.GetDirectoryName(programAssemblyPath)!;
        foreach (var source in assemblyPaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("CLR reference assembly was not found", source);
            }

            var destination = Path.Combine(outputDirectory, Path.GetFileName(source));
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, destination, overwrite: true);
            }
        }
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

    private static void EmitConstructor(
        TypeBuilder typeBuilder,
        int localCount,
        IReadOnlyList<FieldBuilder> localFields)
    {
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.HasThis,
            Type.EmptyTypes);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        EmitInt32(il, localCount);
        il.Emit(OpCodes.Call, ProgramBaseConstructor);
        foreach (var localField in localFields)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, ValueNullMethod);
            il.Emit(OpCodes.Stfld, localField);
        }
        il.Emit(OpCodes.Ret);
    }

    private static void EmitRunStep(
        TypeBuilder typeBuilder,
        FieldBuilder constantsField,
        FieldBuilder importsField,
        IReadOnlyList<FieldBuilder> localFields,
        PdVmProgramModel program,
        PdVmStackLayout stackLayout)
    {
        var method = typeBuilder.DefineMethod(
            nameof(IPdVmProgram.RunStep),
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual,
            typeof(PdVmStatus),
            new[] { typeof(IPdVmHost), typeof(int) });
        typeBuilder.DefineMethodOverride(
            method,
            typeof(IPdVmProgram).GetMethod(
                nameof(IPdVmProgram.RunStep),
                new[] { typeof(IPdVmHost), typeof(int) })!);

        var il = method.GetILGenerator();
        var instructionPointerLocal = il.DeclareLocal(typeof(int));
        var executedInstructionsLocal = il.DeclareLocal(typeof(int));
        var evaluationStack = Enumerable.Range(0, stackLayout.MaximumDepth)
            .Select(_ => il.DeclareLocal(typeof(PdVmValue)))
            .ToArray();
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
            if (!stackLayout.DepthByOffset.TryGetValue(instruction.Offset, out var stackDepth))
            {
                continue;
            }

            var nextEntry = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, instructionPointerLocal);
            EmitInt32(il, instruction.Offset);
            il.Emit(OpCodes.Bne_Un, nextEntry);
            EmitRestoreEvaluationStack(il, evaluationStack, stackDepth);
            il.Emit(OpCodes.Br, labels[instruction.Offset]);
            il.MarkLabel(nextEntry);
        }

        EmitThrowInvalidInstructionPointer(il);

        foreach (var instruction in program.Instructions)
        {
            il.MarkLabel(labels[instruction.Offset]);
            if (!stackLayout.DepthByOffset.TryGetValue(instruction.Offset, out var stackDepth))
            {
                EmitThrowInvalidInstructionPointer(il);
                continue;
            }

            EmitInstructionPrefix(
                il,
                executedInstructionsLocal,
                evaluationStack,
                stackDepth,
                localFields);
            EmitInstruction(
                il,
                constantsField,
                importsField,
                localFields,
                program,
                stackLayout,
                instruction,
                labels,
                executedInstructionsLocal,
                evaluationStack,
                stackDepth,
                typedTemps);
        }

        EmitThrowInvalidInstructionPointer(il);
    }

    private static void EmitInstruction(
        ILGenerator il,
        FieldBuilder constantsField,
        FieldBuilder importsField,
        IReadOnlyList<FieldBuilder> localFields,
        PdVmProgramModel program,
        PdVmStackLayout stackLayout,
        PdVmInstruction instruction,
        IReadOnlyDictionary<int, Label> labels,
        LocalBuilder executedInstructionsLocal,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        PdVmTypedTemps typedTemps)
    {
        if (TryEmitNativeTypedInstruction(
                il,
                program,
                instruction,
                evaluationStack,
                stackDepth,
                typedTemps))
        {
            return;
        }

        if (BinaryOpcodeMethods.TryGetValue(instruction.OpCode, out var binaryMethod))
        {
            il.Emit(OpCodes.Ldloc, evaluationStack[stackDepth - 2]);
            il.Emit(OpCodes.Ldloc, evaluationStack[stackDepth - 1]);
            il.Emit(OpCodes.Call, binaryMethod);
            il.Emit(OpCodes.Stloc, evaluationStack[stackDepth - 2]);
            return;
        }

        if (UnaryOpcodeMethods.TryGetValue(instruction.OpCode, out var unaryMethod))
        {
            il.Emit(OpCodes.Ldloc, evaluationStack[stackDepth - 1]);
            il.Emit(OpCodes.Call, unaryMethod);
            il.Emit(OpCodes.Stloc, evaluationStack[stackDepth - 1]);
            return;
        }

        switch (instruction.OpCode)
        {
            case PdVmBytecodeOpCode.Nop:
                return;
            case PdVmBytecodeOpCode.Ret:
                EmitPersistExecutionState(il, evaluationStack, stackDepth, localFields);
                il.Emit(OpCodes.Ldarg_0);
                EmitInt32(il, instruction.Offset);
                il.Emit(OpCodes.Call, SetInstructionPointerMethod);
                EmitReturnStatus(il, executedInstructionsLocal, HaltProgramMethod);
                return;
            case PdVmBytecodeOpCode.Ldc:
                il.Emit(OpCodes.Ldsfld, constantsField);
                EmitInt32(il, instruction.ConstantIndex!.Value);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Stloc, evaluationStack[stackDepth]);
                return;
            case PdVmBytecodeOpCode.Br:
                EmitTransfer(il, labels, instruction.JumpTarget!.Value);
                return;
            case PdVmBytecodeOpCode.Brfalse:
            {
                var fallthrough = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, evaluationStack[stackDepth - 1]);
                il.Emit(OpCodes.Call, typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsBool), Type.EmptyTypes)!);
                il.Emit(OpCodes.Brtrue, fallthrough);
                EmitTransfer(
                    il,
                    labels,
                    instruction.JumpTarget!.Value);
                il.MarkLabel(fallthrough);
                return;
            }
            case PdVmBytecodeOpCode.Pop:
                return;
            case PdVmBytecodeOpCode.Dup:
                il.Emit(OpCodes.Ldloc, evaluationStack[stackDepth - 1]);
                il.Emit(OpCodes.Stloc, evaluationStack[stackDepth]);
                return;
            case PdVmBytecodeOpCode.Ldloc:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, localFields[instruction.LocalIndex!.Value]);
                il.Emit(OpCodes.Stloc, evaluationStack[stackDepth]);
                return;
            case PdVmBytecodeOpCode.Stloc:
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, evaluationStack[stackDepth - 1]);
                il.Emit(OpCodes.Stfld, localFields[instruction.LocalIndex!.Value]);
                return;
            case PdVmBytecodeOpCode.Call:
                EmitCallInstruction(
                    il,
                    importsField,
                    stackLayout,
                    instruction,
                    executedInstructionsLocal,
                    evaluationStack,
                    stackDepth,
                    localFields);
                return;
            default:
                throw new PdVmCompilerException($"unsupported opcode {instruction.OpCode}");
        }
    }

    private static bool TryEmitNativeTypedInstruction(
        ILGenerator il,
        PdVmProgramModel program,
        PdVmInstruction instruction,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        PdVmTypedTemps typedTemps)
    {
        var operandTypes = GetOperandTypes(program, instruction.Offset);
        var isUnary = instruction.OpCode is PdVmBytecodeOpCode.Neg or PdVmBytecodeOpCode.Not;
        var resultIndex = stackDepth - (isUnary ? 1 : 2);
        if (resultIndex < 0)
        {
            return false;
        }

        var resultSlot = evaluationStack[resultIndex];
        if (isUnary)
        {
            if (instruction.OpCode == PdVmBytecodeOpCode.Neg && operandTypes.Lhs == PdVmValueType.Int)
            {
                EmitLoadInt(il, evaluationStack[stackDepth - 1], typedTemps.Int0);
                il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                il.Emit(OpCodes.Neg);
                EmitStoreValueFromFactory(il, ValueFromIntMethod, resultSlot);
                return true;
            }

            if (instruction.OpCode == PdVmBytecodeOpCode.Neg && operandTypes.Lhs == PdVmValueType.Float)
            {
                EmitLoadFloat(il, evaluationStack[stackDepth - 1], typedTemps.Float0);
                il.Emit(OpCodes.Ldloc, typedTemps.Float0);
                il.Emit(OpCodes.Neg);
                EmitStoreValueFromFactory(il, ValueFromFloatMethod, resultSlot);
                return true;
            }

            if (instruction.OpCode == PdVmBytecodeOpCode.Not && operandTypes.Lhs == PdVmValueType.Bool)
            {
                EmitLoadBool(il, evaluationStack[stackDepth - 1], typedTemps.Bool0);
                il.Emit(OpCodes.Ldloc, typedTemps.Bool0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                EmitStoreValueFromFactory(il, ValueFromBoolMethod, resultSlot);
                return true;
            }

            return false;
        }

        if (operandTypes.Lhs == PdVmValueType.Int && operandTypes.Rhs == PdVmValueType.Int)
        {
            EmitLoadIntPair(il, evaluationStack, stackDepth, typedTemps);
            switch (instruction.OpCode)
            {
                case PdVmBytecodeOpCode.Add:
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Add, ValueFromIntMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Sub:
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Sub, ValueFromIntMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Mul:
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Mul, ValueFromIntMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Div:
                    EmitGuardIntDivisorNotZero(il, typedTemps.Int1, "division by zero");
                    EmitGuardIntMinValueOverflow(il, typedTemps.Int0, typedTemps.Int1, "integer overflow in division");
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Div, ValueFromIntMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Mod:
                    EmitGuardIntDivisorNotZero(il, typedTemps.Int1, "division by zero");
                    EmitGuardIntMinValueOverflow(il, typedTemps.Int0, typedTemps.Int1, "integer overflow in remainder");
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Rem, ValueFromIntMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Ceq:
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Ceq, ValueFromBoolMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Clt:
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Clt, ValueFromBoolMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Cgt:
                    EmitTypedBinaryOperator(il, typedTemps.Int0, typedTemps.Int1, OpCodes.Cgt, ValueFromBoolMethod, resultSlot);
                    return true;
                case PdVmBytecodeOpCode.Shl:
                case PdVmBytecodeOpCode.Shr:
                case PdVmBytecodeOpCode.Lshr:
                    EmitLoadValidatedShiftAmount(il, typedTemps);
                    il.Emit(OpCodes.Ldloc, typedTemps.Int0);
                    il.Emit(OpCodes.Ldloc, typedTemps.ShiftAmount);
                    il.Emit(
                        instruction.OpCode switch
                        {
                            PdVmBytecodeOpCode.Shl => OpCodes.Shl,
                            PdVmBytecodeOpCode.Shr => OpCodes.Shr,
                            _ => OpCodes.Shr_Un,
                        });
                    EmitStoreValueFromFactory(il, ValueFromIntMethod, resultSlot);
                    return true;
            }
        }

        if (operandTypes.Lhs == PdVmValueType.Float && operandTypes.Rhs == PdVmValueType.Float)
        {
            EmitLoadFloatPair(il, evaluationStack, stackDepth, typedTemps);
            var opCode = instruction.OpCode switch
            {
                PdVmBytecodeOpCode.Add => OpCodes.Add,
                PdVmBytecodeOpCode.Sub => OpCodes.Sub,
                PdVmBytecodeOpCode.Mul => OpCodes.Mul,
                PdVmBytecodeOpCode.Div => OpCodes.Div,
                PdVmBytecodeOpCode.Mod => OpCodes.Rem,
                PdVmBytecodeOpCode.Ceq => OpCodes.Ceq,
                PdVmBytecodeOpCode.Clt => OpCodes.Clt,
                PdVmBytecodeOpCode.Cgt => OpCodes.Cgt,
                _ => default,
            };
            if (opCode.Size != 0)
            {
                var factory = instruction.OpCode is PdVmBytecodeOpCode.Ceq
                    or PdVmBytecodeOpCode.Clt or PdVmBytecodeOpCode.Cgt
                    ? ValueFromBoolMethod
                    : ValueFromFloatMethod;
                EmitTypedBinaryOperator(il, typedTemps.Float0, typedTemps.Float1, opCode, factory, resultSlot);
                return true;
            }
        }

        if (operandTypes.Lhs == PdVmValueType.Bool && operandTypes.Rhs == PdVmValueType.Bool)
        {
            EmitLoadBoolPair(il, evaluationStack, stackDepth, typedTemps);
            var opCode = instruction.OpCode switch
            {
                PdVmBytecodeOpCode.Ceq => OpCodes.Ceq,
                PdVmBytecodeOpCode.And => OpCodes.And,
                PdVmBytecodeOpCode.Or => OpCodes.Or,
                _ => default,
            };
            if (opCode.Size != 0)
            {
                EmitTypedBinaryOperator(il, typedTemps.Bool0, typedTemps.Bool1, opCode, ValueFromBoolMethod, resultSlot);
                return true;
            }
        }

        if (operandTypes.Lhs == PdVmValueType.String && operandTypes.Rhs == PdVmValueType.String)
        {
            EmitLoadStringPair(il, evaluationStack, stackDepth, typedTemps);
            il.Emit(OpCodes.Ldloc, typedTemps.String0);
            il.Emit(OpCodes.Ldloc, typedTemps.String1);
            if (instruction.OpCode == PdVmBytecodeOpCode.Add)
            {
                il.Emit(OpCodes.Call, StringConcatMethod);
                EmitStoreValueFromFactory(il, ValueFromStringMethod, resultSlot);
                return true;
            }

            if (instruction.OpCode == PdVmBytecodeOpCode.Ceq)
            {
                EmitInt32(il, (int)StringComparison.Ordinal);
                il.Emit(OpCodes.Call, StringEqualsMethod);
                EmitStoreValueFromFactory(il, ValueFromBoolMethod, resultSlot);
                return true;
            }
        }

        return false;
    }

    private static void EmitLoadIntPair(
        ILGenerator il,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        PdVmTypedTemps typedTemps)
    {
        EmitLoadInt(il, evaluationStack[stackDepth - 2], typedTemps.Int0);
        EmitLoadInt(il, evaluationStack[stackDepth - 1], typedTemps.Int1);
    }

    private static void EmitLoadInt(ILGenerator il, LocalBuilder source, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldloc, source);
        il.Emit(OpCodes.Call, ValueAsIntMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitLoadFloatPair(
        ILGenerator il,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        PdVmTypedTemps typedTemps)
    {
        EmitLoadFloat(il, evaluationStack[stackDepth - 2], typedTemps.Float0);
        EmitLoadFloat(il, evaluationStack[stackDepth - 1], typedTemps.Float1);
    }

    private static void EmitLoadFloat(ILGenerator il, LocalBuilder source, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldloc, source);
        il.Emit(OpCodes.Call, ValueAsFloatMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitLoadBoolPair(
        ILGenerator il,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        PdVmTypedTemps typedTemps)
    {
        EmitLoadBool(il, evaluationStack[stackDepth - 2], typedTemps.Bool0);
        EmitLoadBool(il, evaluationStack[stackDepth - 1], typedTemps.Bool1);
    }

    private static void EmitLoadBool(ILGenerator il, LocalBuilder source, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldloc, source);
        il.Emit(OpCodes.Call, typeof(PdVmValue).GetMethod(nameof(PdVmValue.AsBool), Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitLoadStringPair(
        ILGenerator il,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        PdVmTypedTemps typedTemps)
    {
        EmitLoadString(il, evaluationStack[stackDepth - 2], typedTemps.String0);
        EmitLoadString(il, evaluationStack[stackDepth - 1], typedTemps.String1);
    }

    private static void EmitLoadString(ILGenerator il, LocalBuilder source, LocalBuilder destination)
    {
        il.Emit(OpCodes.Ldloc, source);
        il.Emit(OpCodes.Call, ValueAsStringMethod);
        il.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitTypedBinaryOperator(
        ILGenerator il,
        LocalBuilder lhs,
        LocalBuilder rhs,
        OpCode opCode,
        MethodInfo factory,
        LocalBuilder result)
    {
        il.Emit(OpCodes.Ldloc, lhs);
        il.Emit(OpCodes.Ldloc, rhs);
        il.Emit(opCode);
        EmitStoreValueFromFactory(il, factory, result);
    }

    private static void EmitStoreValueFromFactory(
        ILGenerator il,
        MethodInfo factory,
        LocalBuilder result)
    {
        il.Emit(OpCodes.Call, factory);
        il.Emit(OpCodes.Stloc, result);
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

    private static void EmitCallInstruction(
        ILGenerator il,
        FieldBuilder importsField,
        PdVmStackLayout stackLayout,
        PdVmInstruction instruction,
        LocalBuilder executedInstructionsLocal,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        IReadOnlyList<FieldBuilder> localFields)
    {
        if (instruction.CallIndex is ushort callIndex &&
            PdVmBuiltins.TryGetBuiltin(callIndex, out var builtin) &&
            IntrinsicBuiltins.TryGetValue(builtin, out var intrinsic))
        {
            var argc = instruction.ArgCount!.Value;
            var resultIndex = stackDepth - argc;
            for (var index = 0; index < argc; index++)
            {
                il.Emit(OpCodes.Ldloc, evaluationStack[resultIndex + index]);
            }

            il.Emit(OpCodes.Call, intrinsic.Method);
            if (intrinsic.ReturnsValue)
            {
                il.Emit(OpCodes.Stloc, evaluationStack[resultIndex]);
            }

            return;
        }

        EmitPersistExecutionState(il, evaluationStack, stackDepth, localFields);
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
        if (!stackLayout.DepthByOffset.TryGetValue(instruction.NextOffset, out var outputDepth))
        {
            throw new PdVmCompilerException(
                $"call at offset {instruction.Offset} has no reachable continuation");
        }

        EmitRestoreEvaluationStack(il, evaluationStack, outputDepth);
    }

    private static void EmitInstructionPrefix(
        ILGenerator il,
        LocalBuilder executedInstructionsLocal,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        IReadOnlyList<FieldBuilder> localFields)
    {
        var withinBudget = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, executedInstructionsLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Blt, withinBudget);
        EmitPersistExecutionState(il, evaluationStack, stackDepth, localFields);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, executedInstructionsLocal);
        il.Emit(OpCodes.Call, AddExecutedInstructionsMethod);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, ThrowInstructionBudgetExceededMethod);
        il.MarkLabel(withinBudget);
        il.Emit(OpCodes.Ldloc, executedInstructionsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, executedInstructionsLocal);
    }

    private static void EmitPersistExecutionState(
        ILGenerator il,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth,
        IReadOnlyList<FieldBuilder> localFields)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, ResetStackMethod);
        for (var index = 0; index < stackDepth; index++)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, evaluationStack[index]);
            il.Emit(OpCodes.Call, PushValueMethod);
        }

        for (var index = 0; index < localFields.Count; index++)
        {
            il.Emit(OpCodes.Ldarg_0);
            EmitInt32(il, index);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, localFields[index]);
            il.Emit(OpCodes.Call, SetLocalValueMethod);
        }
    }

    private static void EmitRestoreEvaluationStack(
        ILGenerator il,
        IReadOnlyList<LocalBuilder> evaluationStack,
        int stackDepth)
    {
        for (var index = stackDepth - 1; index >= 0; index--)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, PopValueMethod);
            il.Emit(OpCodes.Stloc, evaluationStack[index]);
        }
    }

    private static void EmitTransfer(
        ILGenerator il,
        IReadOnlyDictionary<int, Label> labels,
        int targetOffset)
    {
        il.Emit(OpCodes.Br, labels[targetOffset]);
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
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
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

    private static MethodInfo GetOpsMethod(string name, params Type[] parameterTypes) =>
        typeof(PdVmOps).GetMethod(
            name,
            BindingFlags.Static | BindingFlags.Public,
            binder: null,
            types: parameterTypes.Length == 0 ? new[] { typeof(PdVmValue) } : parameterTypes,
            modifiers: null) ?? throw new InvalidOperationException($"PdVmOps.{name} not found");
}
