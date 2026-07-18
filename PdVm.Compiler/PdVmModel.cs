using PdVm.Runtime;

namespace PdVm.Compiler;

public enum PdVmBytecodeOpCode : byte
{
    Nop = 0x00,
    Ret = 0x01,
    Ldc = 0x02,
    Add = 0x03,
    Sub = 0x04,
    Mul = 0x05,
    Div = 0x06,
    Neg = 0x07,
    Ceq = 0x08,
    Clt = 0x09,
    Cgt = 0x0A,
    Br = 0x0B,
    Brfalse = 0x0C,
    Pop = 0x0D,
    Dup = 0x0E,
    Ldloc = 0x0F,
    Stloc = 0x10,
    Call = 0x11,
    Shl = 0x12,
    Shr = 0x13,
    Mod = 0x14,
    And = 0x15,
    Or = 0x16,
    Not = 0x17,
    Lshr = 0x18,
    CallValue = 0x19,
}

public sealed record PdVmInstruction(
    int Offset,
    PdVmBytecodeOpCode OpCode,
    int NextOffset,
    int? ConstantIndex = null,
    int? JumpTarget = null,
    byte? LocalIndex = null,
    ushort? CallIndex = null,
    byte? ArgCount = null);

public readonly record struct PdVmOperandTypes(PdVmValueType Lhs, PdVmValueType Rhs);

public enum PdVmTypeSchemaKind : byte
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

public sealed record PdVmTypeSchema
{
    public PdVmTypeSchema(
        PdVmTypeSchemaKind kind,
        string? name = null,
        IReadOnlyList<PdVmTypeSchema>? items = null,
        PdVmTypeSchema? element = null,
        PdVmTypeSchema? result = null,
        IReadOnlyDictionary<string, PdVmTypeSchema>? fields = null)
    {
        Kind = kind;
        Name = name;
        Items = items ?? Array.Empty<PdVmTypeSchema>();
        Element = element;
        Result = result;
        Fields = fields ?? new Dictionary<string, PdVmTypeSchema>(StringComparer.Ordinal);
    }

    public PdVmTypeSchemaKind Kind { get; }

    public string? Name { get; }

    public IReadOnlyList<PdVmTypeSchema> Items { get; }

    public PdVmTypeSchema? Element { get; }

    public PdVmTypeSchema? Result { get; }

    public IReadOnlyDictionary<string, PdVmTypeSchema> Fields { get; }
}

public enum PdVmCallableKind : byte
{
    FunctionItem = 0,
    Closure = 1,
    HostFunction = 2,
}

public enum PdVmCaptureBindingMode : byte
{
    Copy = 0,
    Borrow = 1,
    BorrowMut = 2,
    Move = 3,
}

public enum PdVmCallableTargetKind : byte
{
    ScriptFunction = 0,
    HostImport = 1,
}

public readonly record struct PdVmCallableTarget(PdVmCallableTargetKind Kind, uint Id);

public readonly record struct PdVmScriptFunction(uint EntryIp, uint EndIp);

public sealed record PdVmCallablePrototype(
    PdVmCallableKind Kind,
    PdVmCallableTarget Target,
    byte Arity,
    int FrameLocalCount,
    IReadOnlyList<ushort> ParameterSlots,
    IReadOnlyList<ushort> CaptureSourceSlots,
    IReadOnlyList<ushort> CaptureSlots,
    IReadOnlyList<PdVmCaptureBindingMode> CaptureModes,
    ushort? SelfSlot,
    PdVmTypeSchema? Schema);

public readonly record struct PdVmFunctionRegion(uint StartIp, uint EndIp, uint? PrototypeId);

public readonly record struct PdVmRootCallableBinding(ushort LocalSlot, uint PrototypeId);

public sealed record PdVmExportedCallable(string Name, ushort LocalSlot);

public sealed class PdVmTypeMap
{
    public PdVmTypeMap(
        IReadOnlyList<PdVmValueType> localTypes,
        IReadOnlyDictionary<int, PdVmOperandTypes> operandTypes,
        IReadOnlyList<PdVmTypeSchema?>? localSchemas = null,
        IReadOnlyList<bool>? callableSlots = null,
        IReadOnlyList<bool>? optionalSlots = null,
        bool strictTypes = false)
    {
        LocalTypes = localTypes ?? throw new ArgumentNullException(nameof(localTypes));
        OperandTypes = operandTypes ?? throw new ArgumentNullException(nameof(operandTypes));
        LocalSchemas = localSchemas ?? Enumerable.Repeat<PdVmTypeSchema?>(null, localTypes.Count).ToArray();
        CallableSlots = callableSlots ?? Enumerable.Repeat(false, localTypes.Count).ToArray();
        OptionalSlots = optionalSlots ?? Enumerable.Repeat(false, localTypes.Count).ToArray();
        StrictTypes = strictTypes;
        if (LocalSchemas.Count != LocalTypes.Count ||
            CallableSlots.Count != LocalTypes.Count ||
            OptionalSlots.Count != LocalTypes.Count)
        {
            throw new ArgumentException("type map local metadata lengths must match local types");
        }
    }

    public IReadOnlyList<PdVmValueType> LocalTypes { get; }

    public IReadOnlyDictionary<int, PdVmOperandTypes> OperandTypes { get; }

    public IReadOnlyList<PdVmTypeSchema?> LocalSchemas { get; }

    public IReadOnlyList<bool> CallableSlots { get; }

    public IReadOnlyList<bool> OptionalSlots { get; }

    public bool StrictTypes { get; }
}

public sealed class PdVmProgramModel
{
    public PdVmProgramModel(
        IReadOnlyList<PdVmValue> constants,
        byte[] code,
        int localCount,
        IReadOnlyList<PdVmHostImport> imports,
        IReadOnlyList<PdVmInstruction> instructions,
        PdVmTypeMap? typeMap = null,
        IReadOnlyList<PdVmScriptFunction>? scriptFunctions = null,
        IReadOnlyList<PdVmCallablePrototype>? callablePrototypes = null,
        IReadOnlyList<PdVmFunctionRegion>? functionRegions = null,
        IReadOnlyList<PdVmRootCallableBinding>? rootCallableBindings = null,
        IReadOnlyList<PdVmExportedCallable>? exportedCallables = null)
    {
        Constants = constants ?? throw new ArgumentNullException(nameof(constants));
        Code = code ?? throw new ArgumentNullException(nameof(code));
        LocalCount = localCount;
        Imports = imports ?? throw new ArgumentNullException(nameof(imports));
        Instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        TypeMap = typeMap;
        ScriptFunctions = scriptFunctions ?? Array.Empty<PdVmScriptFunction>();
        CallablePrototypes = callablePrototypes ?? Array.Empty<PdVmCallablePrototype>();
        FunctionRegions = functionRegions ?? Array.Empty<PdVmFunctionRegion>();
        RootCallableBindings = rootCallableBindings ?? Array.Empty<PdVmRootCallableBinding>();
        ExportedCallables = exportedCallables ?? Array.Empty<PdVmExportedCallable>();
    }

    public IReadOnlyList<PdVmValue> Constants { get; }

    public byte[] Code { get; }

    public int LocalCount { get; }

    public IReadOnlyList<PdVmHostImport> Imports { get; }

    public IReadOnlyList<PdVmInstruction> Instructions { get; }

    public PdVmTypeMap? TypeMap { get; }

    public IReadOnlyList<PdVmScriptFunction> ScriptFunctions { get; }

    public IReadOnlyList<PdVmCallablePrototype> CallablePrototypes { get; }

    public IReadOnlyList<PdVmFunctionRegion> FunctionRegions { get; }

    public IReadOnlyList<PdVmRootCallableBinding> RootCallableBindings { get; }

    public IReadOnlyList<PdVmExportedCallable> ExportedCallables { get; }
}
