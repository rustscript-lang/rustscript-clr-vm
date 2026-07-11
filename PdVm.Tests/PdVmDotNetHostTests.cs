using PdVm.Runtime;

namespace PdVm.Tests;

public sealed class PdVmDotNetHostTests
{
    [Fact]
    public void CallsCommonStaticDotNetMethods()
    {
        var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);

        var sqrt = ReturnValue(host.Call(
            "system::Math::Sqrt",
            [PdVmValue.FromFloat(81d)]));
        var fileName = ReturnValue(host.Call(
            "system::IO::Path::GetFileName",
            [PdVmValue.FromString("folder/example.rss")]));

        Assert.Equal(9d, sqrt.AsFloat());
        Assert.Equal("example.rss", fileName.AsString());
    }

    [Fact]
    public void ConstructsAndInteractsWithDotNetObjects()
    {
        var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);
        var handle = ReturnValue(host.Call(
            "system::System::Text::StringBuilder::new",
            Array.Empty<PdVmValue>()));

        _ = host.Call(
            "system::object::call",
            [
                handle,
                PdVmValue.FromString("Append"),
                PdVmValue.FromArray([PdVmValue.FromString("RustScript")]),
            ]);
        var length = ReturnValue(host.Call(
            "system::object::get",
            [handle, PdVmValue.FromString("Length")]));
        var text = ReturnValue(host.Call(
            "system::object::call",
            [
                handle,
                PdVmValue.FromString("ToString"),
                PdVmValue.FromArray(Array.Empty<PdVmValue>()),
            ]));
        var released = ReturnValue(host.Call(
            "system::object::release",
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
                var host = new PdVmDotNetHost(allowDynamicSystemCalls: true);
                Assert.True(host.CanResolveType("System.Windows.Forms.Form"));
                var handle = ReturnValue(host.Call(
                    "system::System::Windows::Forms::Form::new",
                    Array.Empty<PdVmValue>()));
                var label = ReturnValue(host.Call(
                    "system::System::Windows::Forms::Label::new",
                    Array.Empty<PdVmValue>()));
                var button = ReturnValue(host.Call(
                    "system::System::Windows::Forms::Button::new",
                    Array.Empty<PdVmValue>()));
                _ = host.Call(
                    "system::object::set",
                    [handle, PdVmValue.FromString("Text"), PdVmValue.FromString("RustScript CLR UI")]);
                _ = host.Call(
                    "system::object::set",
                    [label, PdVmValue.FromString("Text"), PdVmValue.FromString("Built by RustScript")]);
                _ = host.Call(
                    "system::object::set",
                    [button, PdVmValue.FromString("DialogResult"), PdVmValue.FromString("OK")]);
                _ = host.Call(
                    "system::object::set",
                    [handle, PdVmValue.FromString("AcceptButton"), button]);
                var controls = ReturnValue(host.Call(
                    "system::object::get",
                    [handle, PdVmValue.FromString("Controls")]));
                _ = host.Call(
                    "system::object::call",
                    [controls, PdVmValue.FromString("Add"), PdVmValue.FromArray([label])]);
                _ = host.Call(
                    "system::object::call",
                    [controls, PdVmValue.FromString("Add"), PdVmValue.FromArray([button])]);
                var title = ReturnValue(host.Call(
                    "system::object::get",
                    [handle, PdVmValue.FromString("Text")]));
                var controlCount = ReturnValue(host.Call(
                    "system::object::get",
                    [controls, PdVmValue.FromString("Count")]));
                Assert.Equal("RustScript CLR UI", title.AsString());
                Assert.Equal(2, controlCount.AsInt());
                Assert.True(ReturnValue(host.Call(
                    "system::object::release",
                    [button])).AsBool());
                Assert.True(ReturnValue(host.Call(
                    "system::object::release",
                    [label])).AsBool());
                Assert.True(ReturnValue(host.Call(
                    "system::object::release",
                    [controls])).AsBool());
                Assert.True(ReturnValue(host.Call(
                    "system::object::release",
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
