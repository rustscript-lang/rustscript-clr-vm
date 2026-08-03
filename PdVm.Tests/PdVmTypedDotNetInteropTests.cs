using System.Reflection;
using PdVm.Compiler;
using PdVm.Runtime;

namespace PdVm.Tests;

public sealed class PdVmTypedDotNetInteropTests
{
    [Fact]
    public void NativeLibraryUsesDotNetStylePackageName()
    {
        var expected = OperatingSystem.IsWindows()
            ? "Pdvm.Compiler.Native.dll"
            : OperatingSystem.IsMacOS()
                ? "libPdvm.Compiler.Native.dylib"
                : "libPdvm.Compiler.Native.so";

        Assert.Equal(expected, PdVmNativeCompiler.GetLibraryFileName());
    }

    [Fact]
    public void WindowsAdaptersExcludeTaskSpecificUiTypes()
    {
        var runtime = typeof(PdVmDotNetHost).Assembly;

        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsGrid"));
        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsActionMap"));
        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsPrompt"));
        Assert.Null(runtime.GetType("PdVm.Runtime.PdVmWinFormsDispatcher"));
        Assert.NotNull(runtime.GetType("PdVm.Runtime.PdVmWinFormsScene"));
        Assert.NotNull(typeof(PdVmWinFormsEventLoop).GetMethod("BindPointer"));
        Assert.NotNull(typeof(PdVmWinFormsEventLoop).GetMethod("BindTimer"));
        Assert.Null(typeof(PdVmWinFormsEventLoop).GetMethod("Wait"));
        Assert.Null(typeof(PdVmWinFormsEventLoop).GetMethod("WaitTimeout"));
    }

    [Fact]
    public void NativeCompilerEmitsReadableVmbcWithoutRunnerProcess()
    {
        using var fixture = new SourceFixture("let answer = 40 + 2;\n");

        var bytes = PdVmNativeCompiler.CompileFile(fixture.SourcePath);
        var model = PdVmVmbcReader.ReadBytes(bytes);

        Assert.NotEmpty(model.Code);
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("VMBC"u8));
    }

    [Fact]
    public void GeneratedClrExecutesNamedCallsClosuresAndRecursion()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "tests",
            "fixtures",
            "callable-parity.rss"));
        using var fixture = new SourceFixture(File.ReadAllText(sourcePath));
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));

        var result = PdVmExecution.Run(program, PdVmDefaultHost.CreateConsoleHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal([42L, 15L, 1L], program.Stack.Select(value => value.AsInt()));
        Assert.Empty(((PdVmProgramBase)program).ExecutionFrames);
    }

    [Fact]
    public async Task CSharpResolvesAndInvokesExportedCallable()
    {
        using var fixture = new SourceFixture(
            "pub fn add_one(value: int) -> int { value + 1 }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);

        var callable = program.ResolveCallable("add_one");
        var synchronousStatus = program.StartCallable(
            callable,
            [PdVmValue.FromInt(40)],
            host);
        var synchronousValue = program.TakeCallableResult();
        var value = await program.InvokeCallableAsync(
            callable,
            [PdVmValue.FromInt(41)],
            host);

        Assert.Equal(PdVmStatusKind.Halted, synchronousStatus.Kind);
        Assert.Equal(41, Assert.IsType<PdVmValue>(synchronousValue).AsInt());
        Assert.Equal(42, value.AsInt());
        Assert.Null(program.TakeCallableResult());
        Assert.Throws<InvalidOperationException>(() => program.ResolveCallable("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await program.InvokeCallableAsync(
                callable,
                [PdVmValue.FromString("wrong")],
                host));
        program.ResetForReuse();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await program.InvokeCallableAsync(callable, [PdVmValue.FromInt(1)], host));
    }

    [Fact]
    public void ShutdownInvalidatesCallableAndCallbackHandles()
    {
        using var fixture = new SourceFixture("pub fn on_click() -> null { null }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var callable = program.ResolveCallable("on_click");
        using var callback = program.CreateCallback("on_click", PdVmCallbackAdapters.Unit, host);

        program.Shutdown();

        Assert.Throws<InvalidOperationException>(() =>
            program.StartCallable(callable, Array.Empty<PdVmValue>(), host));
        Assert.Throws<InvalidOperationException>(() => callback.Post(PdVmUnit.Value));
    }

    [Fact]
    public async Task CallbackQueuePreservesFifoOrderAndBorrowedCaptureState()
    {
        using var fixture = new SourceFixture(
            "let mut total = 0;\n" +
            "pub fn append(value: int) -> int {\n" +
            "    total = total * 10 + value;\n" +
            "    total\n" +
            "}\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        using var callback = program.CreateCallback("append", PdVmCallbackAdapters.Value, host);

        var first = callback.InvokeAsync(PdVmValue.FromInt(1)).AsTask();
        var second = callback.InvokeAsync(PdVmValue.FromInt(2)).AsTask();
        var third = callback.InvokeAsync(PdVmValue.FromInt(3)).AsTask();
        var results = await Task.WhenAll(first, second, third);

        Assert.Equal([1L, 12L, 123L], results.Select(value => value.AsInt()));
        callback.Dispose();
        Assert.Throws<ObjectDisposedException>(() => callback.Post(PdVmValue.FromInt(4)));
    }

    [Fact]
    public async Task ManagedCallbackCanCallAClosureKeptInRootLocals()
    {
        using var fixture = new SourceFixture(
            "let mut calls: int = 0;\n" +
            "fn inner() -> int { calls = calls + 1; calls }\n" +
            "pub fn wrapper() -> int { inner() }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var adapter = PdVmCallbackAdapters.Create<PdVmUnit, PdVmValue>(
            _ => Array.Empty<PdVmValue>(),
            value => value,
            Array.Empty<PdVmValueType>(),
            PdVmValueType.Int);
        using var callback = program.CreateCallback(
            "wrapper",
            adapter,
            host);

        var result = await callback.InvokeAsync(PdVmUnit.Value);

        Assert.Equal(1, result.AsInt());
    }

    [Fact]
    public async Task CallbackAdapterSchemaIsValidatedBeforeScriptExecution()
    {
        using var fixture = new SourceFixture(
            "let mut calls: int = 0;\n" +
            "pub fn value() -> int { calls = calls + 1; calls }\n" +
            "pub fn call_count() -> int { calls }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var adapter = PdVmCallbackAdapters.Create<PdVmUnit, string>(
            _ => Array.Empty<PdVmValue>(),
            value => value.AsString(),
            Array.Empty<PdVmValueType>(),
            PdVmValueType.String);

        var error = Assert.Throws<InvalidOperationException>(() =>
            program.CreateCallback("value", adapter, host));
        var callCount = await program.InvokeCallableAsync(
            program.ResolveCallable("call_count"),
            Array.Empty<PdVmValue>(),
            host);

        Assert.Contains("result type String", error.Message);
        Assert.Equal(0, callCount.AsInt());
    }

    [Fact]
    public async Task MapCallbackAdapterMatchesCallableSchema()
    {
        using var fixture = new SourceFixture(
            "pub fn inspect(payload: map<int>) -> int { payload.value }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var adapter = PdVmCallbackAdapters.Map<int, long>(
            value =>
            [
                new KeyValuePair<PdVmValue, PdVmValue>(
                    PdVmValue.FromString("value"),
                    PdVmValue.FromInt(value)),
            ],
            result => result.AsInt());
        using var callback = program.CreateCallback("inspect", adapter, host);

        var result = await callback.InvokeAsync(42);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task EventHandlerPostsCallableOutsideTheEventStack()
    {
        using var fixture = new SourceFixture("pub fn on_click() -> null { null }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRuns = 0;
        var adapter = PdVmCallbackAdapters.Create<PdVmUnit, PdVmUnit>(
            _ => Array.Empty<PdVmValue>(),
            _ =>
            {
                Interlocked.Increment(ref callbackRuns);
                completion.TrySetResult(Environment.CurrentManagedThreadId);
                return PdVmUnit.Value;
            });
        using var callback = program.CreateCallback("on_click", adapter, host);
        var button = new FakeButton();
        _ = callback.Subscribe(
            handler => button.Click += handler,
            handler => button.Click -= handler);

        var eventThreadId = Environment.CurrentManagedThreadId;
        button.RaiseClick();
        var callbackThreadId = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(eventThreadId, callbackThreadId);
        callback.Dispose();
        button.RaiseClick();
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref callbackRuns));
    }

    [Fact]
    public void WinFormsButtonClickInvokesRssCallbackOnTheOwningStaThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signalPath = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-typed-interop-tests",
            $"callable-ui-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(signalPath)!);
        using var fixture = new SourceFixture(
            "use System::IO::File;\n" +
            "use System::Windows::Forms::Form;\n" +
            "use System::Windows::Forms::Button;\n" +
            "use System::Windows::Forms::Label;\n" +
            "use System::Windows::Forms::Control::ControlCollection;\n" +
            "use System::Windows::EventLoop as Ui;\n" +
            "let form = Form::NewForm();\n" +
            "let button = Button::NewButton();\n" +
            "let label = Label::NewLabel();\n" +
            "let controls = Form::GetFormControls(form);\n" +
            "ControlCollection::Add(controls, button);\n" +
            "ControlCollection::Add(controls, label);\n" +
            "fn on_click() -> null {\n" +
            "    Label::SetLabelText(label, \"updated\");\n" +
            $"        File::WriteAllText(\"{signalPath.Replace('\\', '/')}\", Label::GetLabelText(label));\n" +
            "    Ui::Close(form);\n" +
            "    null\n" +
            "}\n" +
            "fn on_shown() -> null { Button::PerformClick(button); null }\n" +
            "Ui::BindClick(form, button, on_click);\n" +
            "Ui::BindShown(form, on_shown);\n" +
            "Ui::Show(form);\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(
            fixture.SourcePath,
            fixture.OutputPath,
            new PdVmDotNetSourceCompileOptions
            {
                Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            });
        Exception? threadError = null;
        string? updatedText = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = PdVmDotNetHost.InitializeWindowsFormsApplication();
                using var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
                    PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
                var host = PdVmDefaultHost.CreateConsoleHost();
                host.RegisterFallback(new PdVmDotNetHost().Call);
                program.CallbackErrorObserver = exception => threadError = exception;
                using var application = PdVmWinFormsApplication.Attach(program, host);
                _ = PdVmExecution.Run(program, host);
                using var watchdog = new System.Threading.Timer(
                    _ => ExitWindowsFormsApplication(),
                    null,
                    TimeSpan.FromSeconds(3),
                    Timeout.InfiniteTimeSpan);
                application.RunMessageLoop();
                updatedText = File.Exists(signalPath) ? File.ReadAllText(signalPath) : null;
            }
            catch (Exception exception)
            {
                threadError = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        File.Delete(signalPath);
        Assert.Null(threadError);
        Assert.Equal("updated", updatedText);
    }

    [Fact]
    public void EscapingClosuresAndCallableIdentityMatchRuntimeContract()
    {
        using var fixture = new SourceFixture(
            "fn make_adder(delta: int) { |value| value + delta; }\n" +
            "fn make_counter() {\n" +
            "    let mut count = 0;\n" +
            "    fn next() { count = count + 1; count }\n" +
            "    next\n" +
            "}\n" +
            "fn add_one(value: int) -> int { value + 1 }\n" +
            "let add = make_adder(1);\n" +
            "let first = make_counter();\n" +
            "let alias = first;\n" +
            "let second = make_counter();\n" +
            "add(41);\n" +
            "first();\n" +
            "alias();\n" +
            "second();\n" +
            "add_one == add_one;\n" +
            "first == alias;\n" +
            "first == second;\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));

        _ = PdVmExecution.Run(program, PdVmDefaultHost.CreateConsoleHost());

        Assert.Equal(42, program.Stack[0].AsInt());
        Assert.Equal([1L, 2L, 1L], program.Stack.Skip(1).Take(3).Select(value => value.AsInt()));
        Assert.Equal([true, true, false], program.Stack.Skip(4).Select(value => value.AsBool()));
    }

    [Fact]
    public void AllCaptureModesAndRecursiveSelfSlotExecuteInGeneratedClr()
    {
        var cases = new[]
        {
            (
                Source: "let a = \"x\";\nlet f = |d| d + a.copy();\nlet d = a;\nf(d);\n",
                Mode: PdVmRuntimeCaptureBindingMode.Copy,
                Expected: "xx"),
            (
                Source: "let a = \"x\";\nlet f = |d| d + &a;\nlet d = a;\nf(d);\n",
                Mode: PdVmRuntimeCaptureBindingMode.Borrow,
                Expected: "xx"),
            (
                Source: "let mut a = \"x\";\nlet f = |d| d + &mut a;\nlet d = a;\nf(d);\n",
                Mode: PdVmRuntimeCaptureBindingMode.BorrowMut,
                Expected: "xx"),
        };

        foreach (var captureCase in cases)
        {
            using var fixture = new SourceFixture(captureCase.Source);
            var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
            var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));

            _ = PdVmExecution.Run(program, PdVmDefaultHost.CreateConsoleHost());
            var metadata = GetProgramMetadata(program);

            Assert.Equal(captureCase.Expected, Assert.Single(program.Stack).AsString());
            Assert.Contains(
                captureCase.Mode,
                metadata.CallablePrototypes.SelectMany(prototype => prototype.CaptureModes));
        }

        using var recursiveFixture = new SourceFixture(
            "let recurse = |value| if value == 0 => { 0 } else => {\n" +
            "    let _nested: int = recurse(value - 1);\n" +
            "    value\n" +
            "};\n" +
            "recurse(5);\n");
        var recursiveOutput = PdVmDotNetSourceCompiler.CompileFile(
            recursiveFixture.SourcePath,
            recursiveFixture.OutputPath);
        var recursiveProgram = PdVmAssemblyLoader.CreateProgram(
            Assembly.Load(File.ReadAllBytes(recursiveOutput)));

        _ = PdVmExecution.Run(recursiveProgram, PdVmDefaultHost.CreateConsoleHost());

        Assert.Equal(5, Assert.Single(recursiveProgram.Stack).AsInt());
        Assert.Contains(
            GetProgramMetadata(recursiveProgram).CallablePrototypes,
            prototype => prototype.SelfSlot.HasValue);
    }

    [Fact]
    public void CallableValuesRoundTripThroughArraysMapsAndBuiltinTargets()
    {
        using var fixture = new SourceFixture(
            "fn add_one(value: int) -> int { value + 1 }\n" +
            "let array = [add_one];\n" +
            "let from_array = array[0];\n" +
            "let map = { f: add_one };\n" +
            "let from_map = map.f;\n" +
            "let length = len;\n" +
            "from_array(40);\n" +
            "from_map(41);\n" +
            "length(\"abc\");\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));

        _ = PdVmExecution.Run(program, PdVmDefaultHost.CreateConsoleHost());

        Assert.Equal([41L, 42L, 3L], program.Stack.Select(value => value.AsInt()));
    }

    [Fact]
    public void BorrowedMapIterationUsesProgramOwnedIteratorState()
    {
        using var fixture = new SourceFixture(
            "let values: map<int> = { a: 1, b: 2, c: 3 };\n" +
            "let mut sum: int = 0;\n" +
            "let mut count: int = 0;\n" +
            "for (_key: string, value: int) in &values {\n" +
            "    sum = sum + value;\n" +
            "    count = count + 1;\n" +
            "}\n" +
            "[sum, count];\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));

        _ = PdVmExecution.Run(program, PdVmDefaultHost.CreateConsoleHost());

        Assert.Equal([6L, 3L], Assert.Single(program.Stack).AsArray().Select(value => value.AsInt()));
    }

    [Fact]
    public async Task PendingHostCallInsideCallableResumesOnceAndResetCancelsIt()
    {
        using var fixture = new SourceFixture(
            "pub fn delayed(value: int) -> int;\n" +
            "pub fn invoke_delayed(value: int) -> int { delayed(value) + 1 }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = new PdVmDelegateHost();
        var callCount = 0;
        var pending = new TaskCompletionSource<PdVmValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterAsyncValue(
            "delayed",
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref callCount);
                return await pending.Task.WaitAsync(cancellationToken);
            });
        _ = PdVmExecution.Run(program, host);
        var callable = program.ResolveCallable("invoke_delayed");

        var invocation = program.InvokeCallableAsync(
            callable,
            [PdVmValue.FromInt(41)],
            host).AsTask();
        await WaitUntilAsync(() => Volatile.Read(ref callCount) == 1);
        pending.SetResult(PdVmValue.FromInt(41));
        var result = await invocation;

        Assert.Equal(42, result.AsInt());
        Assert.Equal(1, callCount);

        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterAsyncValue(
            "delayed",
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref callCount);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return PdVmValue.Null();
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult(true);
                    throw;
                }
            });
        var pendingInvocation = program.InvokeCallableAsync(
            callable,
            [PdVmValue.FromInt(1)],
            host).AsTask();
        await WaitUntilAsync(() => Volatile.Read(ref callCount) == 2);
        program.ResetForReuse();

        Assert.True(await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingInvocation);
    }

    [Fact]
    public async Task PendingHostCallableValueNormalizesUnitResult()
    {
        using var fixture = new SourceFixture(
            "fn delayed() -> null;\n" +
            "let function: fn() -> null = delayed;\n" +
            "function();\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
        var host = new PdVmDelegateHost();
        var callCount = 0;
        host.RegisterAsync(
            "delayed",
            (_, _) =>
            {
                Interlocked.Increment(ref callCount);
                return ValueTask.FromResult(PdVmCallReturn.None);
            });

        var result = await PdVmExecution.RunAsync(program, host);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(PdVmValueKind.Null, Assert.Single(program.Stack).Kind);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task CSharpScriptWrapperInvokesHostCallableAndSyncPathRejectsYield()
    {
        using var fixture = new SourceFixture(
            "fn delayed(value: int) -> int;\n" +
            "pub fn invoke(value: int) -> int {\n" +
            "    let function: fn(int) -> int = delayed;\n" +
            "    function(value)\n" +
            "}\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var rootHost = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, rootHost);
        var callable = program.ResolveCallable("invoke");
        var yieldingHost = new PdVmDelegateHost();
        yieldingHost.Register("delayed", _ => PdVmCallOutcome.Yielded());

        var syncError = Assert.Throws<InvalidOperationException>(() =>
            program.StartCallable(callable, [PdVmValue.FromInt(1)], yieldingHost));

        var asyncHost = new PdVmDelegateHost();
        asyncHost.RegisterAsyncValue(
            "delayed",
            (args, _) => ValueTask.FromResult(PdVmValue.FromInt(args[0].AsInt() + 1)));
        var result = await program.InvokeCallableAsync(
            callable,
            [PdVmValue.FromInt(41)],
            asyncHost);

        Assert.Contains("yielded", syncError.Message);
        Assert.Equal(42, result.AsInt());
    }

    [Fact]
    public async Task ScriptFrameLimitAccepts1024AndRejectsTheNextEntry()
    {
        using var fixture = new SourceFixture(
            "pub fn recurse(depth: int) -> int {\n" +
            "    if depth == 0 => { 0 } else => { recurse(depth - 1) + 1 }\n" +
            "}\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var callable = program.ResolveCallable("recurse");

        var accepted = await program.InvokeCallableAsync(
            callable,
            [PdVmValue.FromInt(1023)],
            host);
        var overflow = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await program.InvokeCallableAsync(
                callable,
                [PdVmValue.FromInt(1024)],
                host));

        Assert.Equal(1023, accepted.AsInt());
        Assert.Contains("limit 1024", overflow.Message);
    }

    [Fact]
    public async Task PostedCallbackFailureUsesErrorObserver()
    {
        using var fixture = new SourceFixture("pub fn fail() -> int { 1 / 0 }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = PdVmDefaultHost.CreateConsoleHost();
        _ = PdVmExecution.Run(program, host);
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        program.CallbackErrorObserver = exception => observed.TrySetResult(exception);
        var adapter = PdVmCallbackAdapters.Create<PdVmUnit, PdVmUnit>(
            _ => Array.Empty<PdVmValue>(),
            _ => PdVmUnit.Value,
            resultType: PdVmValueType.Unknown);
        using var callback = program.CreateCallback("fail", adapter, host);

        callback.Post(PdVmUnit.Value);
        var error = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("division by zero", error.Message);
    }

    [Fact]
    public async Task ResetRejectsRunningAndQueuedCallbackWork()
    {
        using var fixture = new SourceFixture(
            "fn wait_for_reset(value: int) -> int;\n" +
            "pub fn callback(value: int) -> int { wait_for_reset(value) }\n");
        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
            PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
        var host = new PdVmDelegateHost();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RegisterAsyncValue(
            "wait_for_reset",
            async (args, cancellationToken) =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return args[0];
            });
        _ = PdVmExecution.Run(program, host);
        using var callback = program.CreateCallback("callback", PdVmCallbackAdapters.Value, host);
        var running = callback.InvokeAsync(PdVmValue.FromInt(1)).AsTask();
        var queued = callback.InvokeAsync(PdVmValue.FromInt(2)).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        program.ResetForReuse();

        await Assert.ThrowsAnyAsync<Exception>(() => running);
        await Assert.ThrowsAnyAsync<Exception>(() => queued);
        Assert.Throws<InvalidOperationException>(() => callback.Post(PdVmValue.FromInt(3)));
    }

    [Fact]
    public void ExactDescriptorRoundTripsAndInvokesSelectedOverload()
    {
        var method = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(long)])!;
        var descriptor = new PdVmDotNetBindingDescriptor(
            method.DeclaringType!.Assembly.FullName!,
            method.DeclaringType.Assembly.ManifestModule.ModuleVersionId,
            method.DeclaringType.FullName!,
            method.Name,
            PdVmDotNetMemberKind.StaticMethod,
            [typeof(long).AssemblyQualifiedName!],
            typeof(long).AssemblyQualifiedName!);

        var importName = descriptor.EncodeImportName();
        Assert.True(PdVmDotNetBindingDescriptor.TryDecodeImportName(importName, out var decoded));
        Assert.Equal(descriptor.AssemblyName, decoded.AssemblyName);
        Assert.Equal(descriptor.ModuleVersionId, decoded.ModuleVersionId);
        Assert.Equal(descriptor.ParameterTypeNames, decoded.ParameterTypeNames);

        var value = ReturnValue(new PdVmDotNetHost().Call(
            importName,
            [PdVmValue.FromInt(-42)]));
        Assert.Equal(42, value.AsInt());
    }

    [Fact]
    public void DefaultHostRejectsNameBasedDynamicCalls()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new PdVmDotNetHost().Call(
                "System::Math::Sqrt",
                [PdVmValue.FromFloat(4)]));

        Assert.Contains("unbound host import", error.Message);
    }

    [Fact]
    public void SourceWrapperCompilesTypedCommonModuleWithOriginalCompiler()
    {
        using var fixture = new SourceFixture(
            "use System::IO::Path;\n" +
            "let name = Path::GetFileName(\"folder/demo.rss\");\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);

        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "PdVm.Runtime.dll")));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void SourceWrapperReadsClrSignaturesFromRuntimeMetadata()
    {
        using var fixture = new SourceFixture(
            "use System::Math;\n" +
            "Math::Sqrt(81.0);\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
        var host = PdVmDefaultHost.CreateConsoleHost();
        host.RegisterFallback(new PdVmDotNetHost().Call);

        var result = PdVmExecution.Run(program, host);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(9d, Assert.Single(program.Stack).AsFloat());
    }

    [Fact]
    public void SourceWrapperRewritesOnlyQualifiedCodeReferencesToDirectImports()
    {
        using var fixture = new SourceFixture(
            "use System::Math as Numbers;\n" +
            "// Numbers::Sqrt remains a comment.\n" +
            "let label = \"Numbers::Sqrt\";\n" +
            "Numbers::Sqrt(81.0);\n" +
            "label;\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
        var host = PdVmDefaultHost.CreateConsoleHost();
        host.RegisterFallback(new PdVmDotNetHost().Call);

        var result = PdVmExecution.Run(program, host);
        var metadata = GetProgramMetadata(program);

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(9d, program.Stack[0].AsFloat());
        Assert.Equal("Numbers::Sqrt", program.Stack[1].AsString());
        Assert.Empty(metadata.ScriptFunctions);
        Assert.Empty(metadata.CallablePrototypes);
    }

    [Fact]
    public void SourceWrapperReportsReadableErrorForMissingMetadataMember()
    {
        using var fixture = new SourceFixture(
            "use System::Math;\n" +
            "Math::DefinitelyMissing(81.0);\n");

        var error = Assert.Throws<PdVmCompilerException>(() =>
            PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath));

        Assert.Contains("main.rss:2", error.Message);
        Assert.Contains("System.Math", error.Message);
        Assert.Contains("DefinitelyMissing", error.Message);
        Assert.Contains("metadata", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourceCompilerContainsNoManualClrSignatureCatalog()
    {
        var compilerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "PdVm.Compiler",
            "PdVmDotNetSourceCompiler.cs"));
        var source = File.ReadAllText(compilerPath);

        Assert.DoesNotContain("BuildCommonBindings", source);
        Assert.DoesNotContain("BuildWindowsFormsBindings", source);
        Assert.DoesNotContain("ControlBindings", source);
        Assert.DoesNotContain("DialogBindings", source);
        Assert.DoesNotContain("System/Console.rss", source);
        Assert.DoesNotContain("System/Windows/Forms/Form.rss", source);
    }

    [Fact]
    public void SourceWrapperCompilesAndRunsRuntimeSizedRustScriptArrays()
    {
        using var fixture = new SourceFixture(
            "fn filled(size: int, value: int) -> [int] {\n" +
            "    let mut values: [int] = [];\n" +
            "    let mut index = 0;\n" +
            "    while index < size {\n" +
            "        values[values.length] = value;\n" +
            "        index = index + 1;\n" +
            "    }\n" +
            "    values\n" +
            "}\n" +
            "let values = filled(4, 7);\n" +
            "values[0] + values[3] + values.length;\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
        var result = PdVmExecution.Run(program, PdVmDefaultHost.CreateConsoleHost());

        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
        Assert.Equal(18, Assert.Single(program.Stack).AsInt());
    }

    [Fact]
    public void SourceWrapperReportsParameterTypeErrorsAtCompileTime()
    {
        using var fixture = new SourceFixture(
            "use System::Console;\n" +
            "Console::WriteLineString(123);\n");

        var error = Assert.Throws<PdVmCompilerException>(() =>
            PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath));

        Assert.Contains("RustScript compilation failed", error.Message);
        Assert.False(File.Exists(fixture.OutputPath));
    }

    [Fact]
    public void SourceWrapperCompilesTypedWindowsFormsEventLoopProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new SourceFixture(
            "use System::Windows::Forms::Form;\n" +
            "use System::Windows::EventLoop as Ui;\n" +
            "let form = Form::NewForm();\n" +
            "fn on_close() -> null { Ui::Close(form); null }\n" +
            "Ui::BindClosing(form, on_close);\n" +
            "Ui::Show(form);\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(
            fixture.SourcePath,
            fixture.OutputPath,
            new PdVmDotNetSourceCompileOptions
            {
                Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            });

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void SourceWrapperCompilesRustScriptNotepadExample()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var examplePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "examples",
            "dotnet-typed-winforms.rss"));
        var source = File.ReadAllText(examplePath);
        Assert.DoesNotContain("on_action", source);
        Assert.DoesNotContain("Ui::BindAction", source);
        Assert.Contains("Ui::BindClick(form, new_item, || on_new())", source);
        Assert.Contains("Ui::BindClick(form, save_item, || on_save())", source);
        Assert.Contains("Ui::BindClosing(form, || on_close())", source);
        using var fixture = new SourceFixture(source);

        var output = PdVmDotNetSourceCompiler.CompileFile(
            fixture.SourcePath,
            fixture.OutputPath,
            new PdVmDotNetSourceCompileOptions
            {
                Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            });

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void SourceWrapperCompilesRustScriptMinesweeperWithEmbeddedBitmaps()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var examplePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "examples",
            "dotnet-minesweeper.rss"));
        var source = File.ReadAllText(examplePath);
        Assert.DoesNotContain("\"on_event\"", source);
        Assert.DoesNotContain("event_handler", source);
        Assert.DoesNotContain("Ui::BindAction", source);
        Assert.DoesNotContain("Ui::BindPointer", source);
        Assert.DoesNotContain("Ui::BindClosingAction", source);
        Assert.DoesNotContain("Ui::BindTimerAction", source);
        Assert.DoesNotContain("event.action", source);
        Assert.Contains("Ui::BindClick(form, face, reset_game)", source);
        Assert.Contains("Ui::BindMouseDown(form, board_panel, on_board_down)", source);
        Assert.Contains("Ui::BindMouseUp(form, board_panel, on_board_up)", source);
        Assert.Contains("Ui::BindMouseDoubleClick(form, board_panel, on_board_double_click)", source);
        Assert.Contains("Ui::BindMouseLeave(form, board_panel, on_board_leave)", source);
        Assert.Contains("Ui::BindClosing(form, release_game_resources)", source);
        Assert.Contains("Ui::BindTimer(form, 100, on_timer_tick)", source);
        using var fixture = new SourceFixture(source);

        var output = PdVmDotNetSourceCompiler.CompileFile(
            fixture.SourcePath,
            fixture.OutputPath,
            new PdVmDotNetSourceCompileOptions
            {
                Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
            });

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Fact]
    public void WinFormsTimerInvokesRssCallableOnTheOwningStaThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signalPath = Path.Combine(
            Path.GetTempPath(),
            "pd-vm-typed-interop-tests",
            $"event-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(signalPath)!);
        using var fixture = new SourceFixture(
            "use System::IO::File;\n" +
            "use System::Windows::Forms::Form;\n" +
            "use System::Windows::EventLoop as Ui;\n" +
            "let form = Form::NewForm();\n" +
            "fn on_tick() -> null {\n" +
            $"    File::WriteAllText(\"{signalPath.Replace('\\', '/')}\", \"tick\");\n" +
            "    Ui::Close(form);\n" +
            "    null\n" +
            "}\n" +
            "Ui::BindTimer(form, 10, on_tick);\n" +
            "Ui::Show(form);\n");

        try
        {
            var output = PdVmDotNetSourceCompiler.CompileFile(
                fixture.SourcePath,
                fixture.OutputPath,
                new PdVmDotNetSourceCompileOptions
                {
                    Profile = PdVmDotNetInteropProfile.Common | PdVmDotNetInteropProfile.WindowsForms,
                });
            Exception? threadError = null;
            var thread = new Thread(() =>
            {
                try
                {
                    _ = PdVmDotNetHost.InitializeWindowsFormsApplication();
                    using var program = Assert.IsAssignableFrom<IPdVmCallableProgram>(
                        PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output))));
                    var host = PdVmDefaultHost.CreateConsoleHost();
                    host.RegisterFallback(new PdVmDotNetHost().Call);
                    program.CallbackErrorObserver = exception => threadError = exception;
                    using var application = PdVmWinFormsApplication.Attach(program, host);
                    _ = PdVmExecution.Run(program, host);
                    using var watchdog = new System.Threading.Timer(
                        _ => ExitWindowsFormsApplication(),
                        null,
                        TimeSpan.FromSeconds(3),
                        Timeout.InfiniteTimeSpan);
                    application.RunMessageLoop();
                }
                catch (Exception exception)
                {
                    threadError = exception;
                }
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(threadError);
            Assert.Equal("tick", File.ReadAllText(signalPath));
        }
        finally
        {
            File.Delete(signalPath);
        }
    }

    [Fact]
    public void SourceWrapperGeneratesTypedCryptographyBindingsFromSystemUse()
    {
        using var fixture = new SourceFixture(
            "use System::Security::Cryptography::SHA256;\n" +
            "let algorithm = SHA256::Create();\n" +
            "SHA256::ReleaseSHA256(algorithm);\n");

        var output = PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath);

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
        var program = PdVmAssemblyLoader.CreateProgram(Assembly.Load(File.ReadAllBytes(output)));
        Assert.Contains(
            program.GetType().Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "System.Security.Cryptography");

        var host = PdVmDefaultHost.CreateConsoleHost();
        host.RegisterFallback(new PdVmDotNetHost().Call);
        var result = PdVmExecution.Run(program, host);
        Assert.Equal(PdVmStatusKind.Halted, result.Status.Kind);
    }

    [Fact]
    public void SourceWrapperReportsReadableErrorForMissingSystemType()
    {
        using var fixture = new SourceFixture(
            "use System::Security::Cryptography::MissingAlgorithm;\n");

        var error = Assert.Throws<PdVmCompilerException>(() =>
            PdVmDotNetSourceCompiler.CompileFile(fixture.SourcePath, fixture.OutputPath));

        Assert.Contains("main.rss:1", error.Message);
        Assert.Contains("System.Security.Cryptography.MissingAlgorithm", error.Message);
        Assert.Contains("Searched the .NET runtime", error.Message);
    }

    private static PdVmValue ReturnValue(PdVmCallOutcome outcome) =>
        Assert.Single(outcome.ReturnValues.Values);

    private static PdVmProgramMetadata GetProgramMetadata(IPdVmProgram program)
    {
        var metadataField = typeof(PdVmProgramBase).GetField(
            "_metadata",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<PdVmProgramMetadata>(metadataField.GetValue(program));
    }

    private static void ExitWindowsFormsApplication() =>
        Type.GetType(
                "System.Windows.Forms.Application, System.Windows.Forms",
                throwOnError: true)!
            .GetMethod(
                "Exit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null)!
            .Invoke(null, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("condition was not observed before the test deadline");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeButton
    {
        public event EventHandler? Click;

        public void RaiseClick() => Click?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SourceFixture : IDisposable
    {
        public SourceFixture(string source)
        {
            Root = Path.Combine(Path.GetTempPath(), "pd-vm-typed-interop-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SourcePath = Path.Combine(Root, "main.rss");
            OutputPath = Path.Combine(Root, "program.dll");
            File.WriteAllText(SourcePath, source);
        }

        public string Root { get; }

        public string SourcePath { get; }

        public string OutputPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
