using System.Text;
using System.Text.Json;

namespace PdVm.Runtime;

public enum PdVmDotNetMemberKind
{
    StaticMethod,
    Constructor,
    InstanceMethod,
    InstancePropertyGet,
    InstancePropertySet,
    Release,
}

public sealed record PdVmDotNetBindingDescriptor(
    string AssemblyName,
    Guid ModuleVersionId,
    string TypeName,
    string MemberName,
    PdVmDotNetMemberKind Kind,
    string[] ParameterTypeNames,
    string ReturnTypeName)
{
    public const string ImportPrefix = "clr::v1::";

    public string EncodeImportName()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return ImportPrefix + Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecodeImportName(
        string importName,
        out PdVmDotNetBindingDescriptor descriptor)
    {
        descriptor = null!;
        if (!importName.StartsWith(ImportPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = importName[ImportPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            descriptor = JsonSerializer.Deserialize<PdVmDotNetBindingDescriptor>(
                Convert.FromBase64String(encoded))!;
            return descriptor is not null &&
                   !string.IsNullOrWhiteSpace(descriptor.AssemblyName) &&
                   descriptor.ModuleVersionId != Guid.Empty &&
                   !string.IsNullOrWhiteSpace(descriptor.TypeName) &&
                   !string.IsNullOrWhiteSpace(descriptor.MemberName) &&
                   descriptor.ParameterTypeNames is not null &&
                   descriptor.ParameterTypeNames.All(name => !string.IsNullOrWhiteSpace(name)) &&
                   !string.IsNullOrWhiteSpace(descriptor.ReturnTypeName);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return false;
        }
    }
}
