using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PdEdge.Http;

namespace PdEdge.Http.Tests;

public sealed class PdEdgeHttpServerTests
{
    [Fact]
    public async Task CliProgramSourceCompilesAndServesRequest()
    {
        var source = """
            use http;

            http::response::set_status(200);
            http::response::set_header("x-method", http::request::get_method());
            http::response::set_header("x-path", http::request::get_path());
            http::response::set_header("x-body", http::request::get_body());
            http::response::set_body("ok");
            """;
        var tempRoot = Path.Combine(Path.GetTempPath(), "pd-edge-http-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var sourcePath = Path.Combine(tempRoot, "program.rss");
        await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var executableName = OperatingSystem.IsWindows()
            ? "pd-edge-http-minimal-clr.exe"
            : "pd-edge-http-minimal-clr";
        var executablePath = Path.Combine(AppContext.BaseDirectory, executableName);
        Assert.True(File.Exists(executablePath), $"CLI executable was not found: {executablePath}");

        var port = ReserveAvailablePort();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--program-source");
        process.StartInfo.ArgumentList.Add(sourcePath);
        process.StartInfo.ArgumentList.Add("--data-addr");
        process.StartInfo.ArgumentList.Add($"127.0.0.1:{port}");
        process.StartInfo.ArgumentList.Add("--disable-logging");

        var processStarted = false;
        try
        {
            processStarted = process.Start();
            Assert.True(processStarted, "CLI process did not start");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(1),
            };

            using var response = await SendWhenReadyAsync(process, client, "/e2e/source?mode=direct", "source-body");
            if (response is null)
            {
                StopProcess(process);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                Assert.Fail($"CLI did not serve a request.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("POST", response.Headers.GetValues("x-method").Single());
            Assert.Equal("/e2e/source", response.Headers.GetValues("x-path").Single());
            Assert.Equal("source-body", response.Headers.GetValues("x-body").Single());
            Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            if (processStarted)
            {
                StopProcess(process);
            }
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task NoProgramReturns404()
    {
        await using var server = await StartServerAsync(program: null);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var response = await client.PostAsync("/perf", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LocalHostCallsProgramCanTerminateRequest()
    {
        var source = """
            use http;

            http::response::set_status(200);
            http::response::set_header("x-method", http::request::get_method());
            http::response::set_header("x-path", http::request::get_path());
            http::response::set_header("x-body", http::request::get_body());
            http::response::set_body("ok");
            """;
        var program = await LoadInlineProgramAsync(source);
        await using var server = await StartServerAsync(program);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var response = await client.PostAsync(
            "/hello",
            new StringContent("payload", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("POST", response.Headers.GetValues("x-method").Single());
        Assert.Equal("/hello", response.Headers.GetValues("x-path").Single());
        Assert.Equal("payload", response.Headers.GetValues("x-body").Single());
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProxyProgramRoundTripsUpstreamAndOverlaysHeaders()
    {
        await using var upstream = await StartUpstreamAsync();
        var source = $$"""
            use http;
            use proxy;

            let downstream_version = http::request::get_http_version();
            let upstream: int = http::exchange::default_upstream();
            http::exchange::set_target(upstream, "127.0.0.1", {{upstream.Port}});
            http::exchange::set_header(upstream, "x-downstream-version", downstream_version);
            http::exchange::set_header(upstream, "x-bench-program-header", "program-proxy");
            let downstream: int = proxy::stream::downstream();
            let upstream_stream: int = proxy::stream::exchange(upstream);
            proxy::forward_native(downstream, upstream_stream);
            http::response::set_header("x-downstream-version", downstream_version);
            http::response::set_header("x-bench-response-header", "program-proxy");
            """;
        var program = await LoadInlineProgramAsync(source);
        await using var server = await StartServerAsync(program);
        using var client = new HttpClient { BaseAddress = server.BaseAddress };

        using var response = await client.PostAsync(
            "/perf",
            new StringContent(string.Empty, Encoding.UTF8, "text/plain"));
        response.EnsureSuccessStatusCode();

        Assert.Equal("1.1", response.Headers.GetValues("x-downstream-version").Single());
        Assert.Equal("program-proxy", response.Headers.GetValues("x-bench-response-header").Single());
        Assert.Equal("1.1", response.Headers.GetValues("x-bench-upstream-version").Single());
        Assert.Equal("upstream-ok", await response.Content.ReadAsStringAsync());
    }

    private static async Task<PdEdgeLoadedProgram> LoadInlineProgramAsync(string source)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pd-edge-http-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var sourcePath = Path.Combine(tempRoot, "program.rss");
        try
        {
            await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return await PdEdgeProgramLoader.LoadFromSourceFileAsync(sourcePath);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static int ReserveAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<HttpResponseMessage?> SendWhenReadyAsync(
        Process process,
        HttpClient client,
        string requestUri,
        string body)
    {
        for (var attempt = 0; attempt < 100 && !process.HasExited; attempt++)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "text/plain");
                return await client.PostAsync(requestUri, content);
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
            catch (TaskCanceledException)
            {
                await Task.Delay(100);
            }
        }

        return null;
    }

    private static void StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private static async Task<PdEdgeHttpServer> StartServerAsync(PdEdgeLoadedProgram? program)
    {
        var server = new PdEdgeHttpServer(
            new PdEdgeHttpOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                DisableLogging = true,
            },
            program,
            loggerFactory: NullLoggerFactory.Instance);
        await server.StartAsync();
        return server;
    }

    private static async Task<UpstreamFixture> StartUpstreamAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, 0);
        });
        var app = builder.Build();
        app.Map("/{**path}", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["x-bench-upstream-version"] = "1.1";
            context.Response.Headers["x-bench-upstream-body-mode"] =
                string.IsNullOrEmpty(body) ? "headers-only" : "body-read";
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(
                string.IsNullOrEmpty(body) ? "upstream-ok" : $"upstream-echo:{body}");
        });

        await app.StartAsync();
        var address = ResolveAddress(app);
        return new UpstreamFixture(app, address.Port);
    }

    private static Uri ResolveAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        return new Uri(addresses!.Single());
    }

    private sealed class UpstreamFixture(WebApplication app, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
