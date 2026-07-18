using PdVm.Runtime;

namespace PdVm.Compiler;

internal sealed class PdVmStackLayout
{
    public PdVmStackLayout(IReadOnlyDictionary<int, int> depthByOffset, int maximumDepth)
    {
        DepthByOffset = depthByOffset;
        MaximumDepth = maximumDepth;
    }

    public IReadOnlyDictionary<int, int> DepthByOffset { get; }

    public int MaximumDepth { get; }
}

internal static class PdVmStackAnalyzer
{
    public static PdVmStackLayout Analyze(PdVmProgramModel program)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (program.Instructions.Count == 0)
        {
            throw new PdVmCompilerException("VMBC program contains no instructions");
        }

        var instructionByOffset = program.Instructions.ToDictionary(instruction => instruction.Offset);
        var regions = program.FunctionRegions.Count == 0
            ? new[]
            {
                new PdVmFunctionRegion(
                    checked((uint)program.Instructions[0].Offset),
                    checked((uint)program.Code.Length),
                    null),
            }
            : program.FunctionRegions.ToArray();
        var depthByOffset = new Dictionary<int, int>();
        var worklist = new Queue<(int Offset, PdVmFunctionRegion Region)>();
        foreach (var region in regions)
        {
            var entry = checked((int)region.StartIp);
            if (!instructionByOffset.ContainsKey(entry))
            {
                throw new PdVmCompilerException($"function region starts at invalid offset {entry}");
            }

            depthByOffset.Add(entry, 0);
            worklist.Enqueue((entry, region));
        }

        var maximumDepth = 0;

        while (worklist.TryDequeue(out var item))
        {
            var (offset, region) = item;
            var instruction = instructionByOffset[offset];
            var inputDepth = depthByOffset[offset];
            var outputDepth = ApplyStackEffect(program, instruction, inputDepth);
            maximumDepth = Math.Max(maximumDepth, Math.Max(inputDepth, outputDepth));

            foreach (var successor in GetSuccessors(program, instruction, region))
            {
                if (!instructionByOffset.ContainsKey(successor))
                {
                    throw new PdVmCompilerException(
                        $"instruction at offset {instruction.Offset} falls through to invalid offset {successor}");
                }

                if (successor < region.StartIp || successor >= region.EndIp)
                {
                    throw new PdVmCompilerException(
                        $"instruction at offset {instruction.Offset} leaves function region [{region.StartIp}, {region.EndIp})");
                }

                if (depthByOffset.TryGetValue(successor, out var existingDepth))
                {
                    if (existingDepth != outputDepth)
                    {
                        throw new PdVmCompilerException(
                            $"incompatible stack depths at offset {successor}: {existingDepth} and {outputDepth} " +
                            $"from {instruction.OpCode} at offset {instruction.Offset}. " +
                            DescribeContext(program, depthByOffset, successor));
                    }

                    continue;
                }

                depthByOffset.Add(successor, outputDepth);
                worklist.Enqueue((successor, region));
            }
        }

        return new PdVmStackLayout(depthByOffset, maximumDepth);
    }

    private static string DescribeContext(
        PdVmProgramModel program,
        IReadOnlyDictionary<int, int> depthByOffset,
        int target)
    {
        var entries = program.Instructions
            .Where(instruction => instruction.Offset >= target - 48 && instruction.Offset <= target + 16)
            .Select(instruction =>
                $"{instruction.Offset}:{FormatInstruction(program, instruction)}:depth=" +
                (depthByOffset.TryGetValue(instruction.Offset, out var depth) ? depth : -1));
        return $"context [{string.Join(", ", entries)}]";
    }

    private static string FormatInstruction(PdVmProgramModel program, PdVmInstruction instruction) =>
        instruction.OpCode switch
        {
            PdVmBytecodeOpCode.Ldc => FormatConstant(program, instruction),
            PdVmBytecodeOpCode.Ldloc => $"Ldloc({instruction.LocalIndex})",
            PdVmBytecodeOpCode.Stloc => $"Stloc({instruction.LocalIndex})",
            PdVmBytecodeOpCode.Br or PdVmBytecodeOpCode.Brfalse =>
                $"{instruction.OpCode}({instruction.JumpTarget})",
            PdVmBytecodeOpCode.Call => FormatCall(program, instruction),
            PdVmBytecodeOpCode.CallValue => $"CallValue(argc={instruction.ArgCount})",
            _ => instruction.OpCode.ToString(),
        };

    private static string FormatConstant(PdVmProgramModel program, PdVmInstruction instruction)
    {
        var value = program.Constants[instruction.ConstantIndex!.Value].ToString()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        if (value.Length > 40)
        {
            value = value[..37] + "...";
        }

        return $"Ldc({instruction.ConstantIndex}:{value})";
    }

    private static string FormatCall(PdVmProgramModel program, PdVmInstruction instruction)
    {
        var callIndex = instruction.CallIndex!.Value;
        string name;
        if (PdVmBuiltins.TryGetBuiltin(callIndex, out var builtin))
        {
            name = builtin.ToString();
        }
        else
        {
            var importName = program.Imports[callIndex].Name;
            name = PdVmDotNetBindingDescriptor.TryDecodeImportName(importName, out var descriptor)
                ? $"{descriptor.TypeName}.{descriptor.MemberName}"
                : importName;
        }
        return $"Call({name}, argc={instruction.ArgCount})";
    }

    private static int ApplyStackEffect(
        PdVmProgramModel program,
        PdVmInstruction instruction,
        int inputDepth)
    {
        var (popped, pushed) = instruction.OpCode switch
        {
            PdVmBytecodeOpCode.Nop or PdVmBytecodeOpCode.Ret or PdVmBytecodeOpCode.Br => (0, 0),
            PdVmBytecodeOpCode.Ldc or PdVmBytecodeOpCode.Ldloc => (0, 1),
            PdVmBytecodeOpCode.Add or PdVmBytecodeOpCode.Sub or PdVmBytecodeOpCode.Mul
                or PdVmBytecodeOpCode.Div or PdVmBytecodeOpCode.Ceq or PdVmBytecodeOpCode.Clt
                or PdVmBytecodeOpCode.Cgt or PdVmBytecodeOpCode.Shl or PdVmBytecodeOpCode.Shr
                or PdVmBytecodeOpCode.Mod or PdVmBytecodeOpCode.And or PdVmBytecodeOpCode.Or
                or PdVmBytecodeOpCode.Lshr => (2, 1),
            PdVmBytecodeOpCode.Neg or PdVmBytecodeOpCode.Not => (1, 1),
            PdVmBytecodeOpCode.Brfalse or PdVmBytecodeOpCode.Pop or PdVmBytecodeOpCode.Stloc => (1, 0),
            PdVmBytecodeOpCode.Dup => (1, 2),
            PdVmBytecodeOpCode.Call => GetCallStackEffect(program, instruction),
            PdVmBytecodeOpCode.CallValue => (checked(instruction.ArgCount!.Value + 1), 1),
            _ => throw new PdVmCompilerException($"unsupported opcode {instruction.OpCode}"),
        };

        if (inputDepth < popped)
        {
            throw new PdVmCompilerException(
                $"stack underflow at offset {instruction.Offset}: {instruction.OpCode} requires {popped} value(s), found {inputDepth}");
        }

        return checked(inputDepth - popped + pushed);
    }

    private static (int Popped, int Pushed) GetCallStackEffect(
        PdVmProgramModel program,
        PdVmInstruction instruction)
    {
        var callIndex = instruction.CallIndex!.Value;
        var pushed = 1;
        if (PdVmBuiltins.TryGetBuiltin(callIndex, out var builtin))
        {
            if (builtin is PdVmBuiltin.Assert or PdVmBuiltin.DetachLocal)
            {
                pushed = 0;
            }
        }
        else if (program.Imports[callIndex].ReturnType == PdVmValueType.Null)
        {
            pushed = 0;
        }

        return (instruction.ArgCount!.Value, pushed);
    }

    private static IEnumerable<int> GetSuccessors(
        PdVmProgramModel program,
        PdVmInstruction instruction,
        PdVmFunctionRegion region)
    {
        switch (instruction.OpCode)
        {
            case PdVmBytecodeOpCode.Ret:
                yield break;
            case PdVmBytecodeOpCode.Br:
                yield return instruction.JumpTarget!.Value;
                yield break;
            case PdVmBytecodeOpCode.Brfalse:
                yield return instruction.JumpTarget!.Value;
                if (instruction.NextOffset < region.EndIp)
                {
                    yield return instruction.NextOffset;
                    yield break;
                }
                throw new PdVmCompilerException(
                    $"conditional branch at offset {instruction.Offset} falls out of its function region");
            default:
                if (instruction.NextOffset < region.EndIp)
                {
                    yield return instruction.NextOffset;
                    yield break;
                }
                throw new PdVmCompilerException(
                    $"instruction at offset {instruction.Offset} falls out of its function region");
        }
    }
}
