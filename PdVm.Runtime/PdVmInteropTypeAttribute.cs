namespace PdVm.Runtime;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PdVmInteropTypeAttribute(string typeName) : Attribute
{
    public string TypeName { get; } = typeName;
}

[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class PdVmInteropSchemaAttribute(string schema) : Attribute
{
    public string Schema { get; } = schema;
}
