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
        var depthByOffset = new Dictionary<int, int>();
        var worklist = new Queue<int>();
        depthByOffset.Add(program.Instructions[0].Offset, 0);
        worklist.Enqueue(program.Instructions[0].Offset);
        var maximumDepth = 0;

        while (worklist.TryDequeue(out var offset))
        {
            var instruction = instructionByOffset[offset];
            var inputDepth = depthByOffset[offset];
            var outputDepth = ApplyStackEffect(program, instruction, inputDepth);
            maximumDepth = Math.Max(maximumDepth, Math.Max(inputDepth, outputDepth));

            foreach (var successor in GetSuccessors(program, instruction))
            {
                if (!instructionByOffset.ContainsKey(successor))
                {
                    throw new PdVmCompilerException(
                        $"instruction at offset {instruction.Offset} falls through to invalid offset {successor}");
                }

                if (depthByOffset.TryGetValue(successor, out var existingDepth))
                {
                    if (existingDepth != outputDepth)
                    {
                        throw new PdVmCompilerException(
                            $"incompatible stack depths at offset {successor}: {existingDepth} and {outputDepth}");
                    }

                    continue;
                }

                depthByOffset.Add(successor, outputDepth);
                worklist.Enqueue(successor);
            }
        }

        return new PdVmStackLayout(depthByOffset, maximumDepth);
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
            if (builtin == PdVmBuiltin.Assert)
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

    private static IEnumerable<int> GetSuccessors(PdVmProgramModel program, PdVmInstruction instruction)
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
                if (instruction.NextOffset < program.Code.Length)
                {
                    yield return instruction.NextOffset;
                    yield break;
                }
                throw new PdVmCompilerException(
                    $"conditional branch at offset {instruction.Offset} falls off the end of the program");
            default:
                if (instruction.NextOffset < program.Code.Length)
                {
                    yield return instruction.NextOffset;
                    yield break;
                }
                throw new PdVmCompilerException(
                    $"instruction at offset {instruction.Offset} falls off the end of the program");
        }
    }
}
