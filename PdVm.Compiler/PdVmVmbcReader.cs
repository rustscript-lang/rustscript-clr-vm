using System.Text;
using PdVm.Runtime;

namespace PdVm.Compiler;

public static class PdVmVmbcReader
{
    private const int MaximumConstantDepth = 64;
    private static readonly byte[] Magic = "VMBC"u8.ToArray();

    private const ushort Version = 10;
    private const ushort Flags = 0;

    public static PdVmProgramModel ReadFile(string path)
    {
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        return ReadBytes(File.ReadAllBytes(path));
    }

    public static PdVmProgramModel ReadBytes(byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        var cursor = new Cursor(bytes);
        var magic = cursor.ReadExact(4);
        if (!magic.SequenceEqual(Magic))
        {
            throw new PdVmCompilerException($"invalid VMBC magic: {Convert.ToHexString(magic)}");
        }

        var version = cursor.ReadUInt16();
        if (version != Version)
        {
            throw new PdVmCompilerException($"unsupported VMBC version {version}, expected {Version}");
        }

        var flags = cursor.ReadUInt16();
        if (flags != Flags)
        {
            throw new PdVmCompilerException($"unsupported VMBC flags {flags}, expected {Flags}");
        }

        var constantCount = checked((int)cursor.ReadUInt32());
        var constants = new List<PdVmValue>(constantCount);
        for (var index = 0; index < constantCount; index++)
        {
            constants.Add(ReadConstant(ref cursor, 0));
        }

        var codeLength = checked((int)cursor.ReadUInt32());
        var code = cursor.ReadExact(codeLength).ToArray();

        var importCount = checked((int)cursor.ReadUInt32());
        var imports = new List<PdVmHostImport>(importCount);
        for (var index = 0; index < importCount; index++)
        {
            var name = cursor.ReadString();
            var arity = cursor.ReadByte();
            var returnType = ReadValueType(cursor.ReadByte());
            imports.Add(new PdVmHostImport(name, arity, returnType));
        }

        var typeMap = ReadTypeMap(ref cursor);
        SkipDebugInfo(ref cursor);
        var callableMetadata = ReadCallableMetadata(ref cursor);

        if (!cursor.IsEof)
        {
            throw new PdVmCompilerException("trailing bytes after VMBC payload");
        }

        var instructions = DecodeInstructions(code, constants.Count, imports);
        var localCount = InferRootLocalCount(instructions, typeMap, callableMetadata);
        ValidateCallableMetadata(code, localCount, imports, instructions, callableMetadata);
        return new PdVmProgramModel(
            constants,
            code,
            localCount,
            imports,
            instructions,
            typeMap,
            callableMetadata.ScriptFunctions,
            callableMetadata.CallablePrototypes,
            callableMetadata.FunctionRegions,
            callableMetadata.RootCallableBindings,
            callableMetadata.ExportedCallables);
    }

    private static int InferRootLocalCount(
        IReadOnlyList<PdVmInstruction> instructions,
        PdVmTypeMap? typeMap,
        CallableMetadata metadata)
    {
        var localCount = Math.Max(InferLocalCount(instructions), typeMap?.LocalTypes.Count ?? 0);
        foreach (var slot in metadata.RootCallableBindings.Select(binding => binding.LocalSlot)
                     .Concat(metadata.ExportedCallables.Select(exported => exported.LocalSlot)))
        {
            localCount = Math.Max(localCount, checked(slot + 1));
        }

        return localCount;
    }

    private static PdVmValue ReadConstant(ref Cursor cursor, int depth)
    {
        if (depth >= MaximumConstantDepth)
        {
            throw new PdVmCompilerException(
                $"VMBC constant nesting exceeds {MaximumConstantDepth} levels");
        }

        return cursor.ReadByte() switch
        {
            0 => PdVmValue.FromInt(cursor.ReadInt64()),
            1 => cursor.ReadByte() switch
            {
                0 => PdVmValue.FromBool(false),
                1 => PdVmValue.FromBool(true),
                var value => throw new PdVmCompilerException($"invalid VMBC bool literal {value}"),
            },
            2 => PdVmValue.FromString(cursor.ReadString()),
            3 => PdVmValue.FromFloat(cursor.ReadDouble()),
            4 => PdVmValue.Null(),
            5 => PdVmValue.FromBytes(cursor.ReadExact(checked((int)cursor.ReadUInt32())).ToArray()),
            6 => ReadArrayConstant(ref cursor, depth),
            7 => ReadMapConstant(ref cursor, depth),
            var tag => throw new PdVmCompilerException($"invalid VMBC constant tag {tag}"),
        };
    }

    private static PdVmValue ReadArrayConstant(ref Cursor cursor, int depth)
    {
        var count = checked((int)cursor.ReadUInt32());
        var values = new PdVmValue[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = ReadConstant(ref cursor, depth + 1);
        }

        return PdVmValue.FromArray(values);
    }

    private static PdVmValue ReadMapConstant(ref Cursor cursor, int depth)
    {
        var count = checked((int)cursor.ReadUInt32());
        var entries = new KeyValuePair<PdVmValue, PdVmValue>[count];
        for (var index = 0; index < count; index++)
        {
            entries[index] = new KeyValuePair<PdVmValue, PdVmValue>(
                ReadConstant(ref cursor, depth + 1),
                ReadConstant(ref cursor, depth + 1));
        }

        return PdVmValue.FromMap(entries);
    }

    private static IReadOnlyList<PdVmInstruction> DecodeInstructions(
        byte[] code,
        int constantCount,
        IReadOnlyList<PdVmHostImport> imports)
    {
        var instructions = new List<PdVmInstruction>();
        var instructionStarts = new HashSet<int>();
        var jumpTargets = new List<(int Offset, int Target)>();
        var ip = 0;

        while (ip < code.Length)
        {
            var offset = ip;
            instructionStarts.Add(offset);
            var op = ParseOpcode(code[ip]);
            ip++;

            PdVmInstruction instruction;
            switch (op)
            {
                case PdVmBytecodeOpCode.Nop:
                case PdVmBytecodeOpCode.Ret:
                case PdVmBytecodeOpCode.Add:
                case PdVmBytecodeOpCode.Sub:
                case PdVmBytecodeOpCode.Mul:
                case PdVmBytecodeOpCode.Div:
                case PdVmBytecodeOpCode.Neg:
                case PdVmBytecodeOpCode.Ceq:
                case PdVmBytecodeOpCode.Clt:
                case PdVmBytecodeOpCode.Cgt:
                case PdVmBytecodeOpCode.Pop:
                case PdVmBytecodeOpCode.Dup:
                case PdVmBytecodeOpCode.Shl:
                case PdVmBytecodeOpCode.Shr:
                case PdVmBytecodeOpCode.Mod:
                case PdVmBytecodeOpCode.And:
                case PdVmBytecodeOpCode.Or:
                case PdVmBytecodeOpCode.Not:
                case PdVmBytecodeOpCode.Lshr:
                    instruction = new PdVmInstruction(offset, op, ip);
                    break;
                case PdVmBytecodeOpCode.Ldc:
                    {
                        var constantIndex = checked((int)ReadUInt32Operand(code, ref ip, offset, op, 4));
                        if (constantIndex < 0 || constantIndex >= constantCount)
                        {
                            throw new PdVmCompilerException(
                                $"ldc at offset {offset} references invalid constant index {constantIndex}");
                        }

                        instruction = new PdVmInstruction(offset, op, ip, ConstantIndex: constantIndex);
                        break;
                    }
                case PdVmBytecodeOpCode.Br:
                case PdVmBytecodeOpCode.Brfalse:
                    {
                        var target = checked((int)ReadUInt32Operand(code, ref ip, offset, op, 4));
                        jumpTargets.Add((offset, target));
                        instruction = new PdVmInstruction(offset, op, ip, JumpTarget: target);
                        break;
                    }
                case PdVmBytecodeOpCode.Ldloc:
                case PdVmBytecodeOpCode.Stloc:
                    {
                        var localIndex = ReadByteOperand(code, ref ip, offset, op, 1);
                        instruction = new PdVmInstruction(offset, op, ip, LocalIndex: localIndex);
                        break;
                    }
                case PdVmBytecodeOpCode.Call:
                    {
                        var callIndex = ReadUInt16Operand(code, ref ip, offset, op, 3);
                        var argCount = ReadByteOperand(code, ref ip, offset, op, 3);
                        ValidateCall(offset, callIndex, argCount, imports);
                        instruction = new PdVmInstruction(offset, op, ip, CallIndex: callIndex, ArgCount: argCount);
                        break;
                    }
                case PdVmBytecodeOpCode.CallValue:
                    {
                        var argCount = ReadByteOperand(code, ref ip, offset, op, 1);
                        instruction = new PdVmInstruction(offset, op, ip, ArgCount: argCount);
                        break;
                    }
                default:
                    throw new PdVmCompilerException($"invalid opcode 0x{(byte)op:X2} at offset {offset}");
            }

            instructions.Add(instruction);
        }

        foreach (var (offset, target) in jumpTargets)
        {
            if (!instructionStarts.Contains(target))
            {
                throw new PdVmCompilerException(
                    $"jump at offset {offset} targets invalid instruction boundary {target}");
            }
        }

        return instructions;
    }

    private static void ValidateCall(
        int offset,
        ushort callIndex,
        byte argCount,
        IReadOnlyList<PdVmHostImport> imports)
    {
        if (PdVmBuiltins.TryGetBuiltin(callIndex, out var builtin))
        {
            var expected = PdVmBuiltins.GetArity(builtin);
            if (expected != argCount)
            {
                throw new PdVmCompilerException(
                    $"builtin call 0x{callIndex:X4} at offset {offset} expects arity {expected}, got {argCount}");
            }

            return;
        }

        if (PdVmBuiltins.IsBuiltinIndex(callIndex))
        {
            throw new PdVmCompilerException(
                $"builtin call 0x{callIndex:X4} at offset {offset} is not supported by PdVm.Runtime yet");
        }

        if (callIndex >= imports.Count)
        {
            throw new PdVmCompilerException(
                $"import call at offset {offset} references invalid import index {callIndex}");
        }

        var import = imports[callIndex];
        if (import.Arity != argCount)
        {
            throw new PdVmCompilerException(
                $"import '{import.Name}' at offset {offset} expects arity {import.Arity}, got {argCount}");
        }
    }

    private static int InferLocalCount(IEnumerable<PdVmInstruction> instructions)
    {
        var maxLocal = -1;
        foreach (var instruction in instructions)
        {
            if ((instruction.OpCode == PdVmBytecodeOpCode.Ldloc || instruction.OpCode == PdVmBytecodeOpCode.Stloc) &&
                instruction.LocalIndex is byte localIndex)
            {
                maxLocal = Math.Max(maxLocal, localIndex);
            }
        }

        return maxLocal + 1;
    }

    private static PdVmBytecodeOpCode ParseOpcode(byte raw)
    {
        if (Enum.IsDefined(typeof(PdVmBytecodeOpCode), raw))
        {
            return (PdVmBytecodeOpCode)raw;
        }

        throw new PdVmCompilerException($"invalid opcode 0x{raw:X2}");
    }

    private static PdVmTypeMap? ReadTypeMap(ref Cursor cursor)
    {
        switch (cursor.ReadByte())
        {
            case 0:
                return null;
            case 1:
                {
                    var strictTypes = ReadBool(ref cursor);
                    var localCount = checked((int)cursor.ReadUInt32());
                    var localTypes = new PdVmValueType[localCount];
                    for (var index = 0; index < localCount; index++)
                    {
                        localTypes[index] = ReadValueType(cursor.ReadByte());
                    }

                    var localSchemas = new PdVmTypeSchema?[localCount];
                    for (var index = 0; index < localCount; index++)
                    {
                        localSchemas[index] = ReadOptionalSchema(ref cursor);
                    }

                    var callableSlots = ReadBoolVec(ref cursor, localCount);
                    var optionalSlots = ReadBoolVec(ref cursor, localCount);

                    var operandCount = checked((int)cursor.ReadUInt32());
                    var operandTypes = new Dictionary<int, PdVmOperandTypes>(operandCount);
                    for (var index = 0; index < operandCount; index++)
                    {
                        var offset = checked((int)cursor.ReadUInt32());
                        operandTypes[offset] = new PdVmOperandTypes(
                            ReadValueType(cursor.ReadByte()),
                            ReadValueType(cursor.ReadByte()));
                    }

                    return new PdVmTypeMap(
                        localTypes,
                        operandTypes,
                        localSchemas,
                        callableSlots,
                        optionalSlots,
                        strictTypes);
                }
            default:
                throw new PdVmCompilerException("invalid type map flag in VMBC payload");
        }
    }

    private static bool ReadBool(ref Cursor cursor)
    {
        return cursor.ReadByte() switch
        {
            0 => false,
            1 => true,
            var value => throw new PdVmCompilerException($"invalid bool flag {value} in VMBC payload"),
        };
    }

    private static IReadOnlyList<bool> ReadBoolVec(ref Cursor cursor, int expectedLength)
    {
        var count = checked((int)cursor.ReadUInt32());
        if (count != expectedLength)
        {
            throw new PdVmCompilerException(
                $"invalid type map bool vector length {count}, expected {expectedLength}");
        }

        var values = new bool[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = ReadBool(ref cursor);
        }
        return values;
    }

    private static PdVmTypeSchema? ReadOptionalSchema(ref Cursor cursor)
    {
        return cursor.ReadByte() switch
        {
            0 => null,
            1 => ReadSchema(ref cursor),
            _ => throw new PdVmCompilerException("invalid optional schema flag in VMBC payload"),
        };
    }

    private static PdVmTypeSchema ReadSchema(ref Cursor cursor)
    {
        var kind = cursor.ReadByte();
        return kind switch
        {
            <= 7 => new PdVmTypeSchema((PdVmTypeSchemaKind)kind),
            8 => new PdVmTypeSchema(PdVmTypeSchemaKind.GenericParameter, name: cursor.ReadString()),
            9 => new PdVmTypeSchema(
                PdVmTypeSchemaKind.Named,
                name: cursor.ReadString(),
                items: ReadSchemaList(ref cursor)),
            10 => new PdVmTypeSchema(PdVmTypeSchemaKind.Array, element: ReadSchema(ref cursor)),
            11 => new PdVmTypeSchema(PdVmTypeSchemaKind.ArrayTuple, items: ReadSchemaList(ref cursor)),
            12 => new PdVmTypeSchema(
                PdVmTypeSchemaKind.ArrayTupleRest,
                items: ReadSchemaList(ref cursor),
                element: ReadSchema(ref cursor)),
            13 => new PdVmTypeSchema(PdVmTypeSchemaKind.Map, element: ReadSchema(ref cursor)),
            14 => new PdVmTypeSchema(PdVmTypeSchemaKind.Object, fields: ReadSchemaFields(ref cursor)),
            15 => new PdVmTypeSchema(
                PdVmTypeSchemaKind.Callable,
                items: ReadSchemaList(ref cursor),
                result: ReadSchema(ref cursor)),
            16 => new PdVmTypeSchema(PdVmTypeSchemaKind.Optional, element: ReadSchema(ref cursor)),
            _ => throw new PdVmCompilerException($"invalid schema tag {kind} in VMBC payload"),
        };
    }

    private static IReadOnlyList<PdVmTypeSchema> ReadSchemaList(ref Cursor cursor)
    {
        var count = checked((int)cursor.ReadUInt32());
        var schemas = new PdVmTypeSchema[count];
        for (var index = 0; index < count; index++)
        {
            schemas[index] = ReadSchema(ref cursor);
        }
        return schemas;
    }

    private static IReadOnlyDictionary<string, PdVmTypeSchema> ReadSchemaFields(ref Cursor cursor)
    {
        var count = checked((int)cursor.ReadUInt32());
        var fields = new Dictionary<string, PdVmTypeSchema>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var name = cursor.ReadString();
            if (!fields.TryAdd(name, ReadSchema(ref cursor)))
            {
                throw new PdVmCompilerException($"duplicate object schema field '{name}'");
            }
        }
        return fields;
    }

    private readonly record struct CallableMetadata(
        IReadOnlyList<PdVmScriptFunction> ScriptFunctions,
        IReadOnlyList<PdVmCallablePrototype> CallablePrototypes,
        IReadOnlyList<PdVmFunctionRegion> FunctionRegions,
        IReadOnlyList<PdVmRootCallableBinding> RootCallableBindings,
        IReadOnlyList<PdVmExportedCallable> ExportedCallables);

    private static CallableMetadata ReadCallableMetadata(ref Cursor cursor)
    {
        var scriptFunctionCount = checked((int)cursor.ReadUInt32());
        var scriptFunctions = new PdVmScriptFunction[scriptFunctionCount];
        for (var index = 0; index < scriptFunctionCount; index++)
        {
            scriptFunctions[index] = new PdVmScriptFunction(cursor.ReadUInt32(), cursor.ReadUInt32());
        }

        var prototypeCount = checked((int)cursor.ReadUInt32());
        var prototypes = new PdVmCallablePrototype[prototypeCount];
        for (var index = 0; index < prototypeCount; index++)
        {
            var kind = cursor.ReadByte() switch
            {
                0 => PdVmCallableKind.FunctionItem,
                1 => PdVmCallableKind.Closure,
                2 => PdVmCallableKind.HostFunction,
                var value => throw new PdVmCompilerException($"invalid callable kind {value}"),
            };
            var targetKind = cursor.ReadByte() switch
            {
                0 => PdVmCallableTargetKind.ScriptFunction,
                1 => PdVmCallableTargetKind.HostImport,
                var value => throw new PdVmCompilerException($"invalid callable target kind {value}"),
            };
            var target = new PdVmCallableTarget(targetKind, cursor.ReadUInt32());
            var arity = cursor.ReadByte();
            var frameLocalCount = checked((int)cursor.ReadUInt32());
            var parameterSlots = ReadUInt16List(ref cursor);
            var captureSourceSlots = ReadUInt16List(ref cursor);
            var captureSlots = ReadUInt16List(ref cursor);

            var captureModeCount = checked((int)cursor.ReadUInt32());
            var captureModes = new PdVmCaptureBindingMode[captureModeCount];
            for (var captureIndex = 0; captureIndex < captureModeCount; captureIndex++)
            {
                captureModes[captureIndex] = cursor.ReadByte() switch
                {
                    0 => PdVmCaptureBindingMode.Copy,
                    1 => PdVmCaptureBindingMode.Borrow,
                    2 => PdVmCaptureBindingMode.BorrowMut,
                    3 => PdVmCaptureBindingMode.Move,
                    var value => throw new PdVmCompilerException($"invalid capture binding mode {value}"),
                };
            }

            ushort? selfSlot = cursor.ReadByte() switch
            {
                0 => null,
                1 => cursor.ReadUInt16(),
                var value => throw new PdVmCompilerException($"invalid callable self-slot flag {value}"),
            };
            var schema = ReadOptionalSchema(ref cursor);
            prototypes[index] = new PdVmCallablePrototype(
                kind,
                target,
                arity,
                frameLocalCount,
                parameterSlots,
                captureSourceSlots,
                captureSlots,
                captureModes,
                selfSlot,
                schema);
        }

        var regionCount = checked((int)cursor.ReadUInt32());
        var regions = new PdVmFunctionRegion[regionCount];
        for (var index = 0; index < regionCount; index++)
        {
            var startIp = cursor.ReadUInt32();
            var endIp = cursor.ReadUInt32();
            uint? prototypeId = cursor.ReadByte() switch
            {
                0 => null,
                1 => cursor.ReadUInt32(),
                var value => throw new PdVmCompilerException($"invalid function region prototype flag {value}"),
            };
            regions[index] = new PdVmFunctionRegion(startIp, endIp, prototypeId);
        }

        var bindingCount = checked((int)cursor.ReadUInt32());
        var rootBindings = new PdVmRootCallableBinding[bindingCount];
        for (var index = 0; index < bindingCount; index++)
        {
            rootBindings[index] = new PdVmRootCallableBinding(cursor.ReadUInt16(), cursor.ReadUInt32());
        }

        var exportCount = checked((int)cursor.ReadUInt32());
        var exports = new PdVmExportedCallable[exportCount];
        for (var index = 0; index < exportCount; index++)
        {
            exports[index] = new PdVmExportedCallable(cursor.ReadString(), cursor.ReadUInt16());
        }

        return new CallableMetadata(scriptFunctions, prototypes, regions, rootBindings, exports);
    }

    private static IReadOnlyList<ushort> ReadUInt16List(ref Cursor cursor)
    {
        var count = checked((int)cursor.ReadUInt32());
        var values = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            values[index] = cursor.ReadUInt16();
        }
        return values;
    }

    private static void ValidateCallableMetadata(
        byte[] code,
        int localCount,
        IReadOnlyList<PdVmHostImport> imports,
        IReadOnlyList<PdVmInstruction> instructions,
        CallableMetadata metadata)
    {
        var instructionStarts = instructions.Select(instruction => instruction.Offset).ToHashSet();
        var boundaries = instructionStarts.Append(code.Length).ToHashSet();
        foreach (var function in metadata.ScriptFunctions)
        {
            if (function.EntryIp >= function.EndIp ||
                function.EndIp > code.Length ||
                !instructionStarts.Contains(checked((int)function.EntryIp)) ||
                !boundaries.Contains(checked((int)function.EndIp)))
            {
                throw new PdVmCompilerException("invalid script function instruction range");
            }
        }

        uint previousEnd = 0;
        for (var index = 0; index < metadata.FunctionRegions.Count; index++)
        {
            var region = metadata.FunctionRegions[index];
            if (region.StartIp != previousEnd ||
                region.StartIp >= region.EndIp ||
                region.EndIp > code.Length ||
                !instructionStarts.Contains(checked((int)region.StartIp)) ||
                !boundaries.Contains(checked((int)region.EndIp)))
            {
                throw new PdVmCompilerException(
                    "function regions overlap, leave a gap, or use invalid instruction boundaries");
            }
            if (region.PrototypeId is uint prototypeId && prototypeId >= metadata.CallablePrototypes.Count)
            {
                throw new PdVmCompilerException("function region references an invalid prototype");
            }
            previousEnd = region.EndIp;
        }
        if (metadata.FunctionRegions.Count > 0 &&
            (metadata.FunctionRegions[0].StartIp != 0 || previousEnd != code.Length))
        {
            throw new PdVmCompilerException("function regions do not cover the complete bytecode");
        }

        foreach (var prototype in metadata.CallablePrototypes)
        {
            if ((prototype.Target.Kind == PdVmCallableTargetKind.ScriptFunction &&
                 prototype.Arity != prototype.ParameterSlots.Count) ||
                prototype.CaptureSourceSlots.Count != prototype.CaptureSlots.Count ||
                prototype.CaptureModes.Count != prototype.CaptureSlots.Count)
            {
                throw new PdVmCompilerException("callable prototype layout lengths do not match");
            }
            foreach (var slot in prototype.ParameterSlots
                         .Concat(prototype.CaptureSourceSlots)
                         .Concat(prototype.CaptureSlots))
            {
                if (slot >= prototype.FrameLocalCount)
                {
                    throw new PdVmCompilerException("callable prototype slot exceeds frame locals");
                }
            }
            if (prototype.SelfSlot is ushort selfSlot && selfSlot >= prototype.FrameLocalCount)
            {
                throw new PdVmCompilerException("callable self slot exceeds frame locals");
            }

            switch (prototype.Target.Kind)
            {
                case PdVmCallableTargetKind.ScriptFunction
                    when prototype.Target.Id >= metadata.ScriptFunctions.Count:
                    throw new PdVmCompilerException("callable prototype references an invalid script function");
                case PdVmCallableTargetKind.HostImport:
                    if (prototype.Target.Id > ushort.MaxValue)
                    {
                        throw new PdVmCompilerException("callable prototype host target exceeds u16");
                    }
                    var callIndex = (ushort)prototype.Target.Id;
                    if (callIndex >= imports.Count && !PdVmBuiltins.IsBuiltinIndex(callIndex))
                    {
                        throw new PdVmCompilerException("callable prototype references an invalid host import");
                    }
                    break;
            }
        }

        foreach (var binding in metadata.RootCallableBindings)
        {
            if (binding.LocalSlot >= localCount || binding.PrototypeId >= metadata.CallablePrototypes.Count)
            {
                throw new PdVmCompilerException("root callable binding is invalid");
            }
        }

        var exportNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exported in metadata.ExportedCallables)
        {
            if (exported.LocalSlot >= localCount || !exportNames.Add(exported.Name))
            {
                throw new PdVmCompilerException("exported callable binding is invalid");
            }
        }

        if (metadata.FunctionRegions.Count == 0)
        {
            return;
        }
        foreach (var instruction in instructions.Where(instruction => instruction.JumpTarget.HasValue))
        {
            if (FindFunctionRegion(metadata.FunctionRegions, instruction.Offset) !=
                FindFunctionRegion(metadata.FunctionRegions, instruction.JumpTarget!.Value))
            {
                throw new PdVmCompilerException(
                    $"jump at offset {instruction.Offset} leaves the active function region");
            }
        }
    }

    private static int FindFunctionRegion(IReadOnlyList<PdVmFunctionRegion> regions, int ip)
    {
        for (var index = 0; index < regions.Count; index++)
        {
            if (ip >= regions[index].StartIp && ip < regions[index].EndIp)
            {
                return index;
            }
        }
        return -1;
    }

    private static void SkipDebugInfo(ref Cursor cursor)
    {
        switch (cursor.ReadByte())
        {
            case 0:
                return;
            case 1:
                {
                    switch (cursor.ReadByte())
                    {
                        case 0:
                            break;
                        case 1:
                            _ = cursor.ReadString();
                            break;
                        default:
                            throw new PdVmCompilerException("invalid debug source flag in VMBC payload");
                    }

                    var lineCount = checked((int)cursor.ReadUInt32());
                    cursor.Skip(checked(lineCount * 8));

                    var functionCount = checked((int)cursor.ReadUInt32());
                    for (var functionIndex = 0; functionIndex < functionCount; functionIndex++)
                    {
                        _ = cursor.ReadString();
                        var argCount = checked((int)cursor.ReadUInt32());
                        for (var argIndex = 0; argIndex < argCount; argIndex++)
                        {
                            _ = cursor.ReadString();
                            _ = cursor.ReadByte();
                        }
                    }

                    var localCount = checked((int)cursor.ReadUInt32());
                    for (var localIndex = 0; localIndex < localCount; localIndex++)
                    {
                        _ = cursor.ReadString();
                        _ = cursor.ReadByte();
                        SkipOptionalUInt32(ref cursor);
                        SkipOptionalUInt32(ref cursor);
                    }

                    return;
                }
            default:
                throw new PdVmCompilerException("invalid debug info flag in VMBC payload");
        }
    }

    private static void SkipOptionalUInt32(ref Cursor cursor)
    {
        switch (cursor.ReadByte())
        {
            case 0:
                return;
            case 1:
                _ = cursor.ReadUInt32();
                return;
            default:
                throw new PdVmCompilerException("invalid optional u32 flag in debug payload");
        }
    }

    private static PdVmValueType ReadValueType(byte raw)
    {
        return raw switch
        {
            0 => PdVmValueType.Unknown,
            1 => PdVmValueType.Null,
            2 => PdVmValueType.Int,
            3 => PdVmValueType.Float,
            4 => PdVmValueType.Bool,
            5 => PdVmValueType.String,
            6 => PdVmValueType.Bytes,
            7 => PdVmValueType.Array,
            8 => PdVmValueType.Map,
            9 => PdVmValueType.Callable,
            _ => throw new PdVmCompilerException($"invalid value type tag {raw}"),
        };
    }

    private static byte ReadByteOperand(byte[] code, ref int ip, int offset, PdVmBytecodeOpCode opCode, int expectedBytes)
    {
        if (ip >= code.Length)
        {
            throw new PdVmCompilerException(
                $"truncated operand for {opCode} at offset {offset}, expected {expectedBytes} bytes");
        }

        return code[ip++];
    }

    private static ushort ReadUInt16Operand(byte[] code, ref int ip, int offset, PdVmBytecodeOpCode opCode, int expectedBytes)
    {
        if (ip + 1 >= code.Length)
        {
            throw new PdVmCompilerException(
                $"truncated operand for {opCode} at offset {offset}, expected {expectedBytes} bytes");
        }

        var value = BitConverter.ToUInt16(code, ip);
        ip += 2;
        return value;
    }

    private static uint ReadUInt32Operand(byte[] code, ref int ip, int offset, PdVmBytecodeOpCode opCode, int expectedBytes)
    {
        if (ip + 3 >= code.Length)
        {
            throw new PdVmCompilerException(
                $"truncated operand for {opCode} at offset {offset}, expected {expectedBytes} bytes");
        }

        var value = BitConverter.ToUInt32(code, ip);
        ip += 4;
        return value;
    }

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _offset;

        public Cursor(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _offset = 0;
        }

        public bool IsEof => _offset == _bytes.Length;

        public byte ReadByte()
        {
            if (_offset >= _bytes.Length)
            {
                throw new PdVmCompilerException("unexpected end of VMBC payload");
            }

            return _bytes[_offset++];
        }

        public ushort ReadUInt16()
        {
            var value = ReadExact(2);
            return BitConverter.ToUInt16(value);
        }

        public uint ReadUInt32()
        {
            var value = ReadExact(4);
            return BitConverter.ToUInt32(value);
        }

        public long ReadInt64()
        {
            var value = ReadExact(8);
            return BitConverter.ToInt64(value);
        }

        public double ReadDouble()
        {
            var value = ReadExact(8);
            return BitConverter.ToDouble(value);
        }

        public string ReadString()
        {
            var length = checked((int)ReadUInt32());
            var bytes = ReadExact(length);
            return Encoding.UTF8.GetString(bytes);
        }

        public ReadOnlySpan<byte> ReadExact(int length)
        {
            if (_offset + length > _bytes.Length)
            {
                throw new PdVmCompilerException("unexpected end of VMBC payload");
            }

            var slice = _bytes.Slice(_offset, length);
            _offset += length;
            return slice;
        }

        public void Skip(int length)
        {
            _ = ReadExact(length);
        }
    }
}
