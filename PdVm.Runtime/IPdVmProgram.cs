namespace PdVm.Runtime;

public interface IPdVmProgram
{
    IReadOnlyList<PdVmValue> Stack { get; }

    IReadOnlyList<PdVmValue> Locals { get; }

    int InstructionPointer { get; }

    long ExecutedInstructionCount { get; }

    PdVmStatus RunStep(IPdVmHost host);

    PdVmStatus RunStep(IPdVmHost host, int instructionBudget);

    void ResumePending(ulong opId, PdVmCallReturn returnValues);
}
