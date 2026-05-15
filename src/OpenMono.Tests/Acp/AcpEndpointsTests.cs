using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Session;
using OpenMono.Tools;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpEndpointsTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "openmono-endpoint-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationTokenSource _cts = new();
    private readonly HangingLlm _llm = new();
    private Task _serverTask = Task.CompletedTask;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _port = GetFreePort();

        var cfg = new AppConfig { DataDirectory = _tempDir };
        cfg.Llm.Model = "test-model";
        var settings = new AcpServerSettings
        {
            Port = _port,
            CorsOrigins = new[] { "*" },
            PendingToolResultsTimeoutMinutes = 1,
        };
        var registry = new ToolRegistry();

        _serverTask = AcpServer.StartAsync(settings, cfg, _llm, registry, _cts.Token);
        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };

        await WaitForServerAsync();
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _llm.Release(); // unblock any hanging stream so the server task can finish
        try { await _serverTask; } catch { /* graceful shutdown */ }
        _client.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task PostSessions_with_client_tools_echoes_them_back()
    {
        var body = new { client_tools = new[] { "FileRead", "Bash" } };
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", body);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("session_id").GetString().Should().StartWith("sess_");
        var echoed = doc.RootElement.GetProperty("client_tools").EnumerateArray().Select(e => e.GetString()).ToArray();
        echoed.Should().BeEquivalentTo("FileRead", "Bash");
    }

    [Fact]
    public async Task PostSessions_with_empty_body_uses_default_model()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", new { });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("model").GetString().Should().Be("test-model");
    }

    [Fact]
    public async Task PostSessions_with_invalid_json_returns_400()
    {
        var res = await _client.PostAsync("/api/v1/sessions",
            new StringContent("{this is bogus", Encoding.UTF8, "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSessions_unknown_returns_404()
    {
        var res = await _client.GetAsync("/api/v1/sessions/sess_doesnotexist");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSessions_known_returns_200()
    {
        var id = await CreateSessionAsync();
        var res = await _client.GetAsync($"/api/v1/sessions/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostTurn_unknown_session_returns_404()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/v1/sessions/sess_unknown/turn",
            new { message = "hi" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostTurn_missing_body_keys_returns_400()
    {
        var id = await CreateSessionAsync();
        var res = await _client.PostAsJsonAsync($"/api/v1/sessions/{id}/turn", new { something = "else" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostTurn_invalid_json_returns_400()
    {
        var id = await CreateSessionAsync();
        var res = await _client.PostAsync($"/api/v1/sessions/{id}/turn",
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostTurn_returns_409_when_session_already_streaming()
    {
        var id = await CreateSessionAsync();

        // Request A holds the lock because the LLM stream hangs.
        var requestA = _client.PostAsJsonAsync($"/api/v1/sessions/{id}/turn", new { message = "first" });

        // Give A time to acquire the lock and start streaming.
        var lockAcquired = await WaitForConditionAsync(
            () => _llm.StreamStarted,
            TimeSpan.FromSeconds(5));
        lockAcquired.Should().BeTrue("the hanging LLM should have started streaming before B is sent");

        var requestB = await _client.PostAsJsonAsync($"/api/v1/sessions/{id}/turn", new { message = "second" });
        requestB.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await requestB.Content.ReadAsStringAsync();
        body.Should().Contain("session_busy");

        // Release A so the test class can shut down cleanly.
        _llm.Release();
        try { await requestA; } catch { /* SSE stream is fine either way */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<string> CreateSessionAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", new { });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("session_id").GetString()!;
    }

    private async Task WaitForServerAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var res = await _client.GetAsync("/api/v1/sessions/sess_warmup");
                if (res.StatusCode == HttpStatusCode.NotFound) return;
            }
            catch { /* server not ready yet */ }
            await Task.Delay(50);
        }
        throw new InvalidOperationException("ACP server did not become ready within timeout");
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(20);
        }
        return predicate();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed class HangingLlm : ILlmClient
    {
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StreamStarted { get; private set; }

        public void Release()
        {
            _release.TrySetResult();
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? tools, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            StreamStarted = true;
            try
            {
                await _release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) { yield break; }
            yield return new StreamChunk { TextDelta = "", IsComplete = true };
        }

        public void Dispose() { }
    }
}
