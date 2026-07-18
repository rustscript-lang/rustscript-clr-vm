using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PdVm.Runtime;

public enum PdVmRuntimeCallableKind : byte
{
    FunctionItem = 0,
    Closure = 1,
    HostFunction = 2,
}

public enum PdVmRuntimeCaptureBindingMode : byte
{
    Copy = 0,
    Borrow = 1,
    BorrowMut = 2,
    Move = 3,
}

public enum PdVmRuntimeCallableTargetKind : byte
{
    ScriptFunction = 0,
    HostImport = 1,
}

public enum PdVmRuntimeTypeSchemaKind : byte
{
    Unknown = 0,
    Null = 1,
    Int = 2,
    Float = 3,
    Number = 4,
    Bool = 5,
    String = 6,
    Bytes = 7,
    GenericParameter = 8,
    Named = 9,
    Array = 10,
    ArrayTuple = 11,
    ArrayTupleRest = 12,
    Map = 13,
    Object = 14,
    Callable = 15,
    Optional = 16,
}

public sealed record PdVmRuntimeTypeSchema(
    PdVmRuntimeTypeSchemaKind Kind,
    string? Name,
    PdVmRuntimeTypeSchema[] Items,
    PdVmRuntimeTypeSchema? Element,
    PdVmRuntimeTypeSchema? Result,
    Dictionary<string, PdVmRuntimeTypeSchema> Fields);

public sealed record PdVmRuntimeScriptFunction(uint EntryIp, uint EndIp);

public sealed record PdVmRuntimeHostImport(string Name, byte Arity, PdVmValueType ReturnType);

public sealed record PdVmRuntimeFunctionRegion(uint StartIp, uint EndIp, uint? PrototypeId);

public sealed record PdVmRuntimeCallablePrototype(
    PdVmRuntimeCallableKind Kind,
    PdVmRuntimeCallableTargetKind TargetKind,
    uint TargetId,
    byte Arity,
    int FrameLocalCount,
    ushort[] ParameterSlots,
    ushort[] CaptureSourceSlots,
    ushort[] CaptureSlots,
    PdVmRuntimeCaptureBindingMode[] CaptureModes,
    ushort? SelfSlot,
    PdVmRuntimeTypeSchema? Schema);

public sealed record PdVmRuntimeRootCallableBinding(ushort LocalSlot, uint PrototypeId);

public sealed record PdVmRuntimeExportedCallable(string Name, ushort LocalSlot);

public sealed record PdVmProgramMetadata(
    PdVmRuntimeHostImport[] Imports,
    PdVmRuntimeScriptFunction[] ScriptFunctions,
    PdVmRuntimeCallablePrototype[] CallablePrototypes,
    PdVmRuntimeFunctionRegion[] FunctionRegions,
    PdVmRuntimeRootCallableBinding[] RootCallableBindings,
    PdVmRuntimeExportedCallable[] ExportedCallables)
{
    public static PdVmProgramMetadata Empty { get; } = new([], [], [], [], [], []);
}

public static class PdVmProgramMetadataCodec
{
    public static string Encode(PdVmProgramMetadata metadata) =>
        JsonSerializer.Serialize(metadata ?? throw new ArgumentNullException(nameof(metadata)));

    public static PdVmProgramMetadata Decode(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return PdVmProgramMetadata.Empty;
        }

        return JsonSerializer.Deserialize<PdVmProgramMetadata>(payload) ??
               throw new InvalidOperationException("callable metadata payload decoded to null");
    }
}

public sealed class PdVmCaptureCell
{
    internal PdVmCaptureCell(PdVmValue value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal PdVmValue Value { get; set; }
}

public sealed class PdVmCallableEnvironment
{
    internal PdVmCallableEnvironment(IReadOnlyList<PdVmCaptureCell> cells)
    {
        Cells = cells ?? throw new ArgumentNullException(nameof(cells));
    }

    internal IReadOnlyList<PdVmCaptureCell> Cells { get; }
}

public sealed class PdVmCallableValue : IEquatable<PdVmCallableValue>
{
    internal PdVmCallableValue(
        uint prototypeId,
        PdVmRuntimeCallableKind kind,
        PdVmCallableEnvironment? environment,
        object generation)
    {
        PrototypeId = prototypeId;
        Kind = kind;
        Environment = environment;
        Generation = generation ?? throw new ArgumentNullException(nameof(generation));
    }

    public uint PrototypeId { get; }

    public PdVmRuntimeCallableKind Kind { get; }

    internal PdVmCallableEnvironment? Environment { get; }

    internal object Generation { get; }

    public bool Equals(PdVmCallableValue? other) =>
        other is not null &&
        PrototypeId == other.PrototypeId &&
        Kind == other.Kind &&
        ReferenceEquals(Generation, other.Generation) &&
        (Environment is null
            ? other.Environment is null
            : ReferenceEquals(Environment, other.Environment));

    public override bool Equals(object? obj) => obj is PdVmCallableValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        PrototypeId,
        Kind,
        RuntimeHelpers.GetHashCode(Generation),
        Environment is null ? 0 : RuntimeHelpers.GetHashCode(Environment));
}

public enum PdVmFrameContinuationKind
{
    Halt = 0,
    ResumeBytecode = 1,
    ReturnToManaged = 2,
}

public sealed class PdVmExecutionFrame
{
    internal PdVmExecutionFrame(
        PdVmFrameContinuationKind continuation,
        int returnIp,
        int operandStackBase,
        int localBase,
        int localCount,
        uint? prototypeId)
    {
        Continuation = continuation;
        ReturnIp = returnIp;
        OperandStackBase = operandStackBase;
        LocalBase = localBase;
        LocalCount = localCount;
        PrototypeId = prototypeId;
    }

    public PdVmFrameContinuationKind Continuation { get; internal set; }

    public int ReturnIp { get; }

    public int OperandStackBase { get; }

    public int LocalBase { get; }

    public int LocalCount { get; }

    public uint? PrototypeId { get; }
}
