using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace PdVm.Runtime;

public sealed class PdVmDotNetHost
{
    private const string Prefix = "System::";

    private static readonly string[] CandidateAssemblies =
    [
        "System.Console",
        "System.IO.FileSystem",
        "System.Linq",
        "System.Collections",
        "System.Collections.Concurrent",
        "System.Diagnostics.Process",
        "System.Text.RegularExpressions",
        "System.Text.Json",
        "System.Net.Http",
        "System.Net.Primitives",
        "System.Security.Cryptography",
        "System.Threading",
        "System.Xml.ReaderWriter",
        "System.Xml.XDocument",
        "System.Drawing.Common",
        "System.Drawing",
        "System.Windows.Forms",
    ];

    private readonly Dictionary<long, object> _objects = new();
    private readonly Dictionary<object, long> _handles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Type> _typeCache = new(StringComparer.Ordinal);
    private readonly bool _allowDynamicSystemCalls;
    private static readonly Lazy<string?> WindowsDesktopDirectory = new(FindWindowsDesktopDirectory);
    private static int _windowsDesktopResolverRegistered;
    private long _nextHandle;

    public PdVmDotNetHost(bool allowDynamicSystemCalls = false)
    {
        _allowDynamicSystemCalls = allowDynamicSystemCalls;
        EnsureWindowsDesktopResolver();
    }

    public static bool InitializeWindowsFormsApplication()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var host = new PdVmDotNetHost();
        if (!host.TryResolveType("System.Windows.Forms.Application", out var application))
        {
            return false;
        }

        var highDpiMode = application.Assembly.GetType(
            "System.Windows.Forms.HighDpiMode",
            throwOnError: false,
            ignoreCase: false);
        var setHighDpiMode = highDpiMode is null
            ? null
            : application.GetMethod(
                "SetHighDpiMode",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [highDpiMode],
                modifiers: null);
        if (setHighDpiMode is not null && highDpiMode is not null)
        {
            TryInitializeWindowsForms(() =>
                setHighDpiMode.Invoke(null, [Enum.Parse(highDpiMode, "PerMonitorV2", ignoreCase: false)]));
        }

        application.GetMethod(
            "EnableVisualStyles",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null)!.Invoke(null, null);
        TryInitializeWindowsForms(() =>
            application.GetMethod(
                "SetCompatibleTextRenderingDefault",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(bool)],
                modifiers: null)!.Invoke(null, [false]));

        return application.GetProperty(
            "RenderWithVisualStyles",
            BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
    }

    private static void TryInitializeWindowsForms(Action action)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
        {
            // Another component initialized Windows Forms first. Visual styles can still be enabled.
        }
    }

    public PdVmCallOutcome Call(string name, IReadOnlyList<PdVmValue> args)
    {
        if (PdVmDotNetBindingDescriptor.TryDecodeImportName(name, out var descriptor))
        {
            return PdVmCallOutcome.Returned(PdVmCallReturn.One(CallExact(descriptor, args)));
        }
        if (!_allowDynamicSystemCalls || !name.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"unbound host import '{name}'");
        }

        var value = name switch
        {
            "System::Object::Call" => CallObject(args),
            "System::Object::Get" => GetObjectProperty(args),
            "System::Object::Set" => SetObjectProperty(args),
            "System::Object::Release" => ReleaseObject(args),
            _ => CallStaticOrConstructor(name, args),
        };
        return PdVmCallOutcome.Returned(PdVmCallReturn.One(value));
    }

    private PdVmValue CallExact(
        PdVmDotNetBindingDescriptor descriptor,
        IReadOnlyList<PdVmValue> args)
    {
        var assembly = TryLoadAssembly(descriptor.AssemblyName) ??
            throw new InvalidOperationException(
                $"unable to load CLR assembly '{descriptor.AssemblyName}'");
        if (assembly.ManifestModule.ModuleVersionId != descriptor.ModuleVersionId)
        {
            throw new InvalidOperationException(
                $"CLR assembly '{assembly.FullName}' does not match the binding module identity");
        }
        var type = assembly.GetType(descriptor.TypeName, throwOnError: true, ignoreCase: false)!;
        var parameterTypes = descriptor.ParameterTypeNames.Select(ResolveDescriptorType).ToArray();

        if (RequiresWinFormsDispatcher(type, descriptor))
        {
            return PdVmWinFormsDispatcher.Invoke(() => CallExactResolved(descriptor, args, type, parameterTypes));
        }

        return CallExactResolved(descriptor, args, type, parameterTypes);
    }

    private PdVmValue CallExactResolved(
        PdVmDotNetBindingDescriptor descriptor,
        IReadOnlyList<PdVmValue> args,
        Type type,
        Type[] parameterTypes)
    {

        if (descriptor.Kind == PdVmDotNetMemberKind.Release)
        {
            RequireExactTypes(descriptor, parameterTypes, [], typeof(bool));
            return ReleaseObject(args);
        }

        return descriptor.Kind switch
        {
            PdVmDotNetMemberKind.StaticMethod => InvokeExactMethod(
                type,
                target: null,
                descriptor.MemberName,
                parameterTypes,
                descriptor.ReturnTypeName,
                args),
            PdVmDotNetMemberKind.Constructor => InvokeExactConstructor(
                type,
                parameterTypes,
                descriptor.ReturnTypeName,
                args),
            PdVmDotNetMemberKind.InstanceMethod => InvokeExactInstanceMethod(
                type,
                descriptor.MemberName,
                parameterTypes,
                descriptor.ReturnTypeName,
                args),
            PdVmDotNetMemberKind.InstancePropertyGet => InvokeExactPropertyGet(
                type,
                descriptor.MemberName,
                descriptor.ReturnTypeName,
                args),
            PdVmDotNetMemberKind.InstancePropertySet => InvokeExactPropertySet(
                type,
                descriptor.MemberName,
                parameterTypes,
                descriptor.ReturnTypeName,
                args),
            _ => throw new InvalidOperationException(
                $"unsupported exact CLR binding kind {descriptor.Kind}"),
        };
    }

    private static bool RequiresWinFormsDispatcher(Type type, PdVmDotNetBindingDescriptor descriptor)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        if (string.Equals(type.Assembly.GetName().Name, "System.Windows.Forms", StringComparison.Ordinal))
        {
            return true;
        }
        return type == typeof(PdVmWinFormsEventLoop) &&
               !string.Equals(descriptor.MemberName, nameof(PdVmWinFormsEventLoop.Wait), StringComparison.Ordinal);
    }

    private PdVmValue InvokeExactMethod(
        Type type,
        object? target,
        string memberName,
        Type[] parameterTypes,
        string expectedReturnTypeName,
        IReadOnlyList<PdVmValue> args)
    {
        var flags = BindingFlags.Public |
                    (target is null ? BindingFlags.Static : BindingFlags.Instance);
        var method = type.GetMethod(
            memberName,
            flags,
            binder: null,
            types: parameterTypes,
            modifiers: null) ?? throw new InvalidOperationException(
                $"exact CLR method '{type.FullName}.{memberName}({string.Join(", ", parameterTypes.Select(item => item.FullName))})' was not found");
        VerifyTypeIdentity(method.ReturnType, expectedReturnTypeName, "return type");
        var converted = ConvertExactArguments(args, parameterTypes);
        return FromClrValue(Invoke(method, target, converted));
    }

    private PdVmValue InvokeExactConstructor(
        Type type,
        Type[] parameterTypes,
        string expectedReturnTypeName,
        IReadOnlyList<PdVmValue> args)
    {
        var constructor = type.GetConstructor(parameterTypes) ??
            throw new InvalidOperationException(
                $"exact CLR constructor '{type.FullName}({string.Join(", ", parameterTypes.Select(item => item.FullName))})' was not found");
        VerifyTypeIdentity(type, expectedReturnTypeName, "constructor result type");
        return FromClrValue(Invoke(constructor, null, ConvertExactArguments(args, parameterTypes)));
    }

    private PdVmValue InvokeExactInstanceMethod(
        Type type,
        string memberName,
        Type[] parameterTypes,
        string expectedReturnTypeName,
        IReadOnlyList<PdVmValue> args)
    {
        if (args.Count == 0)
        {
            throw new InvalidOperationException($"instance CLR call '{memberName}' requires an object handle");
        }
        var target = GetObject(args[0]);
        if (!type.IsInstanceOfType(target))
        {
            throw new InvalidOperationException(
                $"object handle type {target.GetType().FullName} is not assignable to {type.FullName}");
        }
        return InvokeExactMethod(
            type,
            target,
            memberName,
            parameterTypes,
            expectedReturnTypeName,
            args.Skip(1).ToArray());
    }

    private PdVmValue InvokeExactPropertyGet(
        Type type,
        string propertyName,
        string expectedReturnTypeName,
        IReadOnlyList<PdVmValue> args)
    {
        RequireArgCount($"{type.FullName}.{propertyName}.get", args, 1);
        var target = GetObject(args[0]);
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"exact CLR property '{type.FullName}.{propertyName}' was not found");
        VerifyTypeIdentity(property.PropertyType, expectedReturnTypeName, "property type");
        return FromClrValue(property.GetValue(target));
    }

    private PdVmValue InvokeExactPropertySet(
        Type type,
        string propertyName,
        Type[] parameterTypes,
        string expectedReturnTypeName,
        IReadOnlyList<PdVmValue> args)
    {
        RequireArgCount($"{type.FullName}.{propertyName}.set", args, 2);
        var target = GetObject(args[0]);
        var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"exact CLR property '{type.FullName}.{propertyName}' was not found");
        RequireExactTypes(
            new PdVmDotNetBindingDescriptor(
                type.Assembly.FullName!,
                type.Assembly.ManifestModule.ModuleVersionId,
                type.FullName!,
                propertyName,
                PdVmDotNetMemberKind.InstancePropertySet,
                parameterTypes.Select(item => item.AssemblyQualifiedName!).ToArray(),
                expectedReturnTypeName),
            parameterTypes,
            [property.PropertyType],
            typeof(void));
        var converted = ConvertExactArguments([args[1]], [property.PropertyType]);
        property.SetValue(target, converted[0]);
        return PdVmValue.Null();
    }

    private object?[] ConvertExactArguments(
        IReadOnlyList<PdVmValue> args,
        IReadOnlyList<Type> parameterTypes)
    {
        if (args.Count != parameterTypes.Count)
        {
            throw new InvalidOperationException(
                $"exact CLR binding expects {parameterTypes.Count} argument(s), got {args.Count}");
        }
        var converted = new object?[args.Count];
        for (var index = 0; index < args.Count; index++)
        {
            if (!TryConvert(args[index], parameterTypes[index], out converted[index], out _))
            {
                throw new InvalidOperationException(
                    $"cannot convert RustScript {args[index].Kind} to {parameterTypes[index].FullName}");
            }
        }
        return converted;
    }

    private static Type ResolveDescriptorType(string typeName) =>
        Type.GetType(typeName, throwOnError: true, ignoreCase: false)!;

    private static void RequireExactTypes(
        PdVmDotNetBindingDescriptor descriptor,
        IReadOnlyList<Type> actualParameters,
        IReadOnlyList<Type> expectedParameters,
        Type expectedReturnType)
    {
        if (actualParameters.Count != expectedParameters.Count ||
            actualParameters.Where((type, index) => type != expectedParameters[index]).Any())
        {
            throw new InvalidOperationException("CLR binding parameter identity does not match the selected member");
        }
        VerifyTypeIdentity(expectedReturnType, descriptor.ReturnTypeName, "return type");
    }

    private static void VerifyTypeIdentity(Type actualType, string expectedTypeName, string displayName)
    {
        if (!string.Equals(actualType.AssemblyQualifiedName, expectedTypeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"CLR binding {displayName} does not match the selected member");
        }
    }

    public bool CanResolveType(string typeName) => TryResolveType(typeName, out _);

    private PdVmValue CallStaticOrConstructor(string hostName, IReadOnlyList<PdVmValue> args)
    {
        var segments = hostName[Prefix.Length..].Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            throw new InvalidOperationException(
                $".NET import '{hostName}' must contain a type and member name");
        }

        for (var memberIndex = segments.Length - 1; memberIndex >= 1; memberIndex--)
        {
            var typeName = NormalizeTypeName(segments[..memberIndex]);
            if (!TryResolveType(typeName, out var type))
            {
                continue;
            }

            var memberName = string.Join(".", segments[memberIndex..]);
            if (string.Equals(memberName, "new", StringComparison.Ordinal))
            {
                var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                var (constructor, converted) = BindBest(constructors, args, $"{type.FullName} constructor");
                return FromClrValue(Invoke(constructor, null, converted));
            }

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method =>
                    string.Equals(method.Name, memberName, StringComparison.Ordinal) &&
                    !method.ContainsGenericParameters)
                .ToArray();
            var (bound, parameters) = BindBest(methods, args, $"{type.FullName}.{memberName}");
            return FromClrValue(Invoke(bound, null, parameters));
        }

        throw new InvalidOperationException($"unable to resolve .NET type in import '{hostName}'");
    }

    private PdVmValue CallObject(IReadOnlyList<PdVmValue> args)
    {
        RequireArgCount("System::Object::Call", args, 3);
        var target = GetObject(args[0]);
        var memberName = args[1].AsString();
        var callArgs = args[2].AsArray();
        var methods = target
            .GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method =>
                string.Equals(method.Name, memberName, StringComparison.Ordinal) &&
                !method.ContainsGenericParameters)
            .ToArray();
        var (bound, parameters) = BindBest(
            methods,
            callArgs,
            $"{target.GetType().FullName}.{memberName}");
        return FromClrValue(Invoke(bound, target, parameters));
    }

    private PdVmValue GetObjectProperty(IReadOnlyList<PdVmValue> args)
    {
        RequireArgCount("System::Object::Get", args, 2);
        var target = GetObject(args[0]);
        var propertyName = args[1].AsString();
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"public property '{target.GetType().FullName}.{propertyName}' was not found");
        if (!property.CanRead || property.GetIndexParameters().Length != 0)
        {
            throw new InvalidOperationException(
                $"property '{target.GetType().FullName}.{propertyName}' is not readable");
        }

        return FromClrValue(property.GetValue(target));
    }

    private PdVmValue SetObjectProperty(IReadOnlyList<PdVmValue> args)
    {
        RequireArgCount("System::Object::Set", args, 3);
        var target = GetObject(args[0]);
        var propertyName = args[1].AsString();
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance) ??
            throw new InvalidOperationException(
                $"public property '{target.GetType().FullName}.{propertyName}' was not found");
        if (!property.CanWrite || property.GetIndexParameters().Length != 0)
        {
            throw new InvalidOperationException(
                $"property '{target.GetType().FullName}.{propertyName}' is not writable");
        }
        if (!TryConvert(args[2], property.PropertyType, out var converted, out _))
        {
            throw new InvalidOperationException(
                $"cannot convert {args[2].Kind} to {property.PropertyType.FullName} for property '{propertyName}'");
        }

        property.SetValue(target, converted);
        return PdVmValue.Null();
    }

    private PdVmValue ReleaseObject(IReadOnlyList<PdVmValue> args)
    {
        RequireArgCount("System::Object::Release", args, 1);
        var handle = args[0].AsInt();
        if (!_objects.Remove(handle, out var value))
        {
            return PdVmValue.FromBool(false);
        }

        _handles.Remove(value);
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
        return PdVmValue.FromBool(true);
    }

    private object GetObject(PdVmValue value)
    {
        var handle = value.AsInt();
        return _objects.TryGetValue(handle, out var target)
            ? target
            : throw new InvalidOperationException($"unknown .NET object handle {handle}");
    }

    private (MethodBase Method, object?[] Args) BindBest(
        IEnumerable<MethodBase> candidates,
        IReadOnlyList<PdVmValue> args,
        string displayName)
    {
        MethodBase? best = null;
        object?[]? bestArgs = null;
        var bestScore = int.MaxValue;
        var ambiguous = false;

        foreach (var candidate in candidates)
        {
            var parameters = candidate.GetParameters();
            if (parameters.Length != args.Count || parameters.Any(parameter => parameter.ParameterType.IsByRef))
            {
                continue;
            }

            var converted = new object?[args.Count];
            var score = 0;
            var valid = true;
            for (var index = 0; index < args.Count; index++)
            {
                if (!TryConvert(args[index], parameters[index].ParameterType, out converted[index], out var itemScore))
                {
                    valid = false;
                    break;
                }
                score += itemScore;
            }

            if (!valid || score > bestScore)
            {
                continue;
            }
            if (score == bestScore)
            {
                ambiguous = true;
                continue;
            }

            best = candidate;
            bestArgs = converted;
            bestScore = score;
            ambiguous = false;
        }

        if (best is null)
        {
            throw new InvalidOperationException(
                $"no public overload of '{displayName}' accepts {args.Count} RustScript argument(s)");
        }
        if (ambiguous)
        {
            throw new InvalidOperationException(
                $"call to '{displayName}' is ambiguous for the supplied RustScript values");
        }
        return (best, bestArgs!);
    }

    private bool TryConvert(PdVmValue value, Type targetType, out object? converted, out int score)
    {
        var nullable = Nullable.GetUnderlyingType(targetType);
        if (nullable is not null)
        {
            if (value.Kind == PdVmValueKind.Null)
            {
                converted = null;
                score = 0;
                return true;
            }
            return TryConvert(value, nullable, out converted, out score);
        }

        if (value.Kind == PdVmValueKind.Null)
        {
            converted = null;
            score = targetType.IsValueType ? int.MaxValue : 0;
            return !targetType.IsValueType;
        }

        if (!IsNumericType(targetType) && value.Kind == PdVmValueKind.Int &&
            _objects.TryGetValue(value.IntValue, out var referenced) && targetType.IsInstanceOfType(referenced))
        {
            converted = referenced;
            score = 0;
            return true;
        }

        if (targetType == typeof(PdVmValue))
        {
            converted = value;
            score = 0;
            return true;
        }
        if (targetType == typeof(string) && value.Kind == PdVmValueKind.String)
        {
            converted = value.AsString();
            score = 0;
            return true;
        }
        if (targetType == typeof(bool) && value.Kind == PdVmValueKind.Bool)
        {
            converted = value.AsBool();
            score = 0;
            return true;
        }
        if (targetType == typeof(byte[]) && value.Kind == PdVmValueKind.Bytes)
        {
            converted = value.AsBytes();
            score = 0;
            return true;
        }
        if (targetType.IsEnum && value.Kind == PdVmValueKind.String)
        {
            try
            {
                converted = Enum.Parse(targetType, value.AsString(), ignoreCase: true);
                score = 2;
                return true;
            }
            catch (ArgumentException)
            {
            }
        }
        if (targetType.IsArray && value.Kind == PdVmValueKind.Array)
        {
            var elementType = targetType.GetElementType()!;
            var values = value.AsArray();
            var array = Array.CreateInstance(elementType, values.Count);
            var totalScore = 1;
            for (var index = 0; index < values.Count; index++)
            {
                if (!TryConvert(values[index], elementType, out var item, out var itemScore))
                {
                    converted = null;
                    score = int.MaxValue;
                    return false;
                }
                array.SetValue(item, index);
                totalScore += itemScore;
            }
            converted = array;
            score = totalScore;
            return true;
        }
        if (IsNumericType(targetType) && value.Kind is PdVmValueKind.Int or PdVmValueKind.Float)
        {
            try
            {
                var input = value.Kind == PdVmValueKind.Int
                    ? (object)value.IntValue
                    : value.FloatValue;
                converted = Convert.ChangeType(input, targetType, CultureInfo.InvariantCulture);
                score = targetType == typeof(long) && value.Kind == PdVmValueKind.Int ||
                        targetType == typeof(double) && value.Kind == PdVmValueKind.Float
                    ? 0
                    : 1;
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
            {
            }
        }
        if (targetType == typeof(object))
        {
            converted = ToClrObject(value);
            score = 10;
            return true;
        }

        converted = null;
        score = int.MaxValue;
        return false;
    }

    private object? ToClrObject(PdVmValue value) => value.Kind switch
    {
        PdVmValueKind.Null => null,
        PdVmValueKind.Int when _objects.TryGetValue(value.IntValue, out var target) => target,
        PdVmValueKind.Int => value.IntValue,
        PdVmValueKind.Float => value.FloatValue,
        PdVmValueKind.Bool => value.BoolValue,
        PdVmValueKind.String => value.AsString(),
        PdVmValueKind.Bytes => value.AsBytes(),
        PdVmValueKind.Array => value.AsArray().Select(ToClrObject).ToArray(),
        PdVmValueKind.Map => value.AsMap().ToDictionary(
            entry => PdVmValue.FormatDisplay(entry.Key),
            entry => ToClrObject(entry.Value),
            StringComparer.Ordinal),
        _ => throw new InvalidOperationException($"cannot convert {value.Kind} to a CLR object"),
    };

    private PdVmValue FromClrValue(object? value)
    {
        if (value is null)
        {
            return PdVmValue.Null();
        }
        if (value is PdVmValue vmValue)
        {
            return vmValue;
        }
        if (value is string text)
        {
            return PdVmValue.FromString(text);
        }
        if (value is char character)
        {
            return PdVmValue.FromString(character.ToString());
        }
        if (value is bool boolean)
        {
            return PdVmValue.FromBool(boolean);
        }
        if (value is byte[] bytes)
        {
            return PdVmValue.FromBytes(bytes);
        }
        if (value.GetType().IsEnum)
        {
            return PdVmValue.FromString(value.ToString()!);
        }
        if (value is sbyte or byte or short or ushort or int or uint or long)
        {
            return PdVmValue.FromInt(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
        if (value is ulong unsigned)
        {
            return unsigned <= long.MaxValue
                ? PdVmValue.FromInt((long)unsigned)
                : PdVmValue.FromString(unsigned.ToString(CultureInfo.InvariantCulture));
        }
        if (value is float or double or decimal)
        {
            return PdVmValue.FromFloat(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }
        if (value is Array array)
        {
            return PdVmValue.FromArray(array.Cast<object?>().Select(FromClrValue));
        }
        if (value is IDictionary dictionary)
        {
            var entries = new List<KeyValuePair<PdVmValue, PdVmValue>>();
            foreach (DictionaryEntry entry in dictionary)
            {
                entries.Add(new KeyValuePair<PdVmValue, PdVmValue>(
                    FromClrValue(entry.Key),
                    FromClrValue(entry.Value)));
            }
            return PdVmValue.FromMap(entries);
        }

        if (_handles.TryGetValue(value, out var existingHandle))
        {
            return PdVmValue.FromInt(existingHandle);
        }
        var handle = checked(++_nextHandle);
        _objects.Add(handle, value);
        _handles.Add(value, handle);
        return PdVmValue.FromInt(handle);
    }

    private bool TryResolveType(string typeName, out Type type)
    {
        if (_typeCache.TryGetValue(typeName, out type!))
        {
            return true;
        }

        type = Type.GetType(typeName, throwOnError: false, ignoreCase: false)!;
        if (type is null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false)!;
                if (type is not null)
                {
                    break;
                }
            }
        }
        if (type is null)
        {
            foreach (var assemblyName in CandidateAssemblies)
            {
                try
                {
                    var assembly = TryLoadAssembly(assemblyName);
                    type = assembly?
                        .GetType(typeName, throwOnError: false, ignoreCase: false)!;
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    continue;
                }
                if (type is not null)
                {
                    break;
                }
            }
        }
        if (type is null)
        {
            return false;
        }

        _typeCache[typeName] = type;
        return true;
    }

    private static Assembly? TryLoadAssembly(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName));
        }
        catch (FileNotFoundException) when (WindowsDesktopDirectory.Value is { } directory)
        {
            var simpleName = new AssemblyName(assemblyName).Name;
            var path = Path.Combine(directory, $"{simpleName}.dll");
            return File.Exists(path)
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
                : null;
        }
    }

    private static void EnsureWindowsDesktopResolver()
    {
        if (!OperatingSystem.IsWindows() || WindowsDesktopDirectory.Value is null ||
            Interlocked.Exchange(ref _windowsDesktopResolverRegistered, 1) != 0)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += ResolveWindowsDesktopAssembly;
    }

    private static Assembly? ResolveWindowsDesktopAssembly(
        AssemblyLoadContext context,
        AssemblyName assemblyName)
    {
        var directory = WindowsDesktopDirectory.Value;
        if (directory is null || string.IsNullOrWhiteSpace(assemblyName.Name))
        {
            return null;
        }

        var path = Path.Combine(directory, $"{assemblyName.Name}.dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static string? FindWindowsDesktopDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var frameworkDirectory = Directory.GetParent(runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var sharedDirectory = frameworkDirectory?.Parent;
        if (sharedDirectory is null)
        {
            return null;
        }

        var desktopRoot = Path.Combine(sharedDirectory.FullName, "Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(desktopRoot))
        {
            return null;
        }

        var exact = Path.Combine(desktopRoot, Environment.Version.ToString());
        if (Directory.Exists(exact))
        {
            return exact;
        }

        return Directory
            .EnumerateDirectories(desktopRoot)
            .Select(path => (Path: path, Version: ParseVersion(Path.GetFileName(path))))
            .Where(item => item.Version is not null && item.Version.Major == Environment.Version.Major)
            .OrderByDescending(item => item.Version)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static Version? ParseVersion(string value) =>
        Version.TryParse(value, out var version) ? version : null;

    private static string NormalizeTypeName(ReadOnlySpan<string> segments)
    {
        var joined = string.Join(".", segments.ToArray());
        return joined.StartsWith("System.", StringComparison.Ordinal) || joined == "System"
            ? joined
            : $"System.{joined}";
    }

    private static object? Invoke(MethodBase method, object? target, object?[] args)
    {
        try
        {
            return method switch
            {
                MethodInfo methodInfo => methodInfo.Invoke(target, args),
                ConstructorInfo constructor => constructor.Invoke(args),
                _ => throw new InvalidOperationException($"unsupported reflected member {method}"),
            };
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static bool IsNumericType(Type type) =>
        type == typeof(sbyte) || type == typeof(byte) ||
        type == typeof(short) || type == typeof(ushort) ||
        type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) ||
        type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    private static void RequireArgCount(string name, IReadOnlyList<PdVmValue> args, int expected)
    {
        if (args.Count != expected)
        {
            throw new InvalidOperationException(
                $"{name} expects {expected} argument(s), got {args.Count}");
        }
    }
}
