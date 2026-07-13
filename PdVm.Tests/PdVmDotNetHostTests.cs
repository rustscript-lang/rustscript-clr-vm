using PdVm.Runtime;

namespace PdVm.Tests;

public sealed class PdVmDotNetHostTests
{
    [Fact]
    public void CallsCommonStaticDotNetMethods()
    {
        var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);

        var sqrt = ReturnValue(host.Call(
            "System::Math::Sqrt",
            [PdVmValue.FromFloat(81d)]));
        var fileName = ReturnValue(host.Call(
            "System::IO::Path::GetFileName",
            [PdVmValue.FromString("folder/example.rss")]));

        Assert.Equal(9d, sqrt.AsFloat());
        Assert.Equal("example.rss", fileName.AsString());
    }

    [Fact]
    public void ConstructsAndInteractsWithDotNetObjects()
    {
        var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);
        var handle = ReturnValue(host.Call(
            "System::Text::StringBuilder::new",
            Array.Empty<PdVmValue>()));

        _ = host.Call(
            "System::Object::Call",
            [
                handle,
                PdVmValue.FromString("Append"),
                PdVmValue.FromArray([PdVmValue.FromString("RustScript")]),
            ]);
        var length = ReturnValue(host.Call(
            "System::Object::Get",
            [handle, PdVmValue.FromString("Length")]));
        var text = ReturnValue(host.Call(
            "System::Object::Call",
            [
                handle,
                PdVmValue.FromString("ToString"),
                PdVmValue.FromArray(Array.Empty<PdVmValue>()),
            ]));
        var released = ReturnValue(host.Call(
            "System::Object::Release",
            [handle]));

        Assert.Equal(10, length.AsInt());
        Assert.Equal("RustScript", text.AsString());
        Assert.True(released.AsBool());
    }

    [Fact]
    public void ConstructsWindowsFormOnStaThreadWithoutCompileTimeWindowsDependency()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(PdVmDotNetHost.InitializeWindowsFormsApplication());
                var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);
                Assert.True(host.CanResolveType("System.Windows.Forms.Form"));
                var handle = ReturnValue(host.Call(
                    "System::Windows::Forms::Form::new",
                    Array.Empty<PdVmValue>()));
                var label = ReturnValue(host.Call(
                    "System::Windows::Forms::Label::new",
                    Array.Empty<PdVmValue>()));
                var button = ReturnValue(host.Call(
                    "System::Windows::Forms::Button::new",
                    Array.Empty<PdVmValue>()));
                _ = host.Call(
                    "System::Object::Set",
                    [handle, PdVmValue.FromString("Text"), PdVmValue.FromString("RustScript CLR UI")]);
                _ = host.Call(
                    "System::Object::Set",
                    [label, PdVmValue.FromString("Text"), PdVmValue.FromString("Built by RustScript")]);
                _ = host.Call(
                    "System::Object::Set",
                    [button, PdVmValue.FromString("DialogResult"), PdVmValue.FromString("OK")]);
                _ = host.Call(
                    "System::Object::Set",
                    [handle, PdVmValue.FromString("AcceptButton"), button]);
                var controls = ReturnValue(host.Call(
                    "System::Object::Get",
                    [handle, PdVmValue.FromString("Controls")]));
                _ = host.Call(
                    "System::Object::Call",
                    [controls, PdVmValue.FromString("Add"), PdVmValue.FromArray([label])]);
                _ = host.Call(
                    "System::Object::Call",
                    [controls, PdVmValue.FromString("Add"), PdVmValue.FromArray([button])]);
                var title = ReturnValue(host.Call(
                    "System::Object::Get",
                    [handle, PdVmValue.FromString("Text")]));
                var controlCount = ReturnValue(host.Call(
                    "System::Object::Get",
                    [controls, PdVmValue.FromString("Count")]));
                Assert.Equal("RustScript CLR UI", title.AsString());
                Assert.Equal(2, controlCount.AsInt());
                Assert.True(ReturnValue(host.Call(
                    "System::Object::Release",
                    [button])).AsBool());
                Assert.True(ReturnValue(host.Call(
                    "System::Object::Release",
                    [label])).AsBool());
                Assert.True(ReturnValue(host.Call(
                    "System::Object::Release",
                    [controls])).AsBool());
                Assert.True(ReturnValue(host.Call(
                    "System::Object::Release",
                    [handle])).AsBool());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    private static PdVmValue ReturnValue(PdVmCallOutcome outcome)
    {
        Assert.Equal(PdVmCallOutcomeKind.Return, outcome.Kind);
        return Assert.Single(outcome.ReturnValues.Values);
    }
}
