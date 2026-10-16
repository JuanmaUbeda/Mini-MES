using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Api;
using MiniMes.Core.Models;
using MiniMes.ErpSimulator;

namespace MiniMes.Tests.Integration;

/// <summary>
/// The whole system, running in memory: the real Mini-MES API and the real ERP simulator, wired together.
/// Each instance has its own SQLite file and XML folder, so tests never interfere with each other.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    /// <summary>Same JSON rules as the app: camelCase, enums as text.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "minimes-it-" + Guid.NewGuid());
    private readonly WebApplicationFactory<ErpState> _erpFactory;           // any public type of the ERP assembly picks the app
    private readonly WebApplicationFactory<BatchSubmissionService> _apiFactory;   // ...and of the API assembly

    public HttpClient Api { get; }
    public HttpClient Erp { get; }
    public string XmlFolder => Path.Combine(_folder, "xml");

    /// <param name="mixerCycleSeconds">
    /// Length of the mixing cycle. Use 1 when a test must watch a batch finish, and a long value when it needs
    /// the batch to stay Running.
    /// </param>
    public TestApp(int mixerCycleSeconds = 60)
    {
        Directory.CreateDirectory(_folder);

        _erpFactory = new WebApplicationFactory<ErpState>();
        Erp = _erpFactory.CreateClient();

        _apiFactory = new WebApplicationFactory<BatchSubmissionService>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:MiniMes", $"Data Source={Path.Combine(_folder, "test.db")}");
            builder.UseSetting("Output:Folder", XmlFolder);
            builder.UseSetting("Mixer:CycleSeconds", mixerCycleSeconds.ToString());

            // Point the API's ERP client at the in-memory simulator instead of http://localhost:5063.
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<IErpClient, ErpClient>(client => client.BaseAddress = _erpFactory.Server.BaseAddress)
                        .ConfigurePrimaryHttpMessageHandler(() => _erpFactory.Server.CreateHandler()));
        });
        Api = _apiFactory.CreateClient();
    }

    // ----- small helpers so the tests read like a story -----

    public async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public async Task<T> GetAsync<T>(string path)
    {
        var response = await Api.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response);
    }

    public Task<HttpResponseMessage> PostAsync(string path) => Api.PostAsync(path, content: null);

    public async Task<int> RecipeIdAsync(string name) =>
        (await GetAsync<List<RecipeDto>>("/api/recipes")).Single(r => r.Name == name).Id;

    public async Task<BatchDto> CreateBatchAsync(decimal quantityKg, string recipe = "Standard Mix")
    {
        var response = await Api.PostAsJsonAsync("/api/batches",
            new CreateBatchRequest(await RecipeIdAsync(recipe), quantityKg), Json);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<BatchDto>(response);
    }

    /// <summary>Creates a batch and sends it to the ERP, so it ends up Ready (when the ERP accepts it).</summary>
    public async Task<BatchDto> CreateReadyBatchAsync(decimal quantityKg, string recipe = "Standard Mix")
    {
        var batch = await CreateBatchAsync(quantityKg, recipe);
        var response = await PostAsync($"/api/batches/{batch.BatchId}/submit");
        response.EnsureSuccessStatusCode();
        return (await ReadAsync<SubmitResultDto>(response)).Batch;
    }

    public async Task<BatchDto> GetBatchAsync(string id) =>
        (await GetAsync<BatchDetailDto>($"/api/batches/{id}")).Batch;

    public async Task<IReadOnlyList<TankDto>> GetTanksAsync() =>
        await GetAsync<List<TankDto>>("/api/tanks");

    public async Task<decimal> TankLevelAsync(Container container) =>
        (await GetTanksAsync()).Single(t => t.Container == container).LevelKg;

    public async Task SetErpModeAsync(ErpMode mode) =>
        (await Erp.PutAsJsonAsync("/admin/mode", new ModeRequest(mode), Json)).EnsureSuccessStatusCode();

    /// <summary>Polls until the batch reaches a status, or fails the test after a timeout.</summary>
    public async Task<BatchDto> WaitForStatusAsync(string id, BatchStatus expected, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        BatchDto batch;
        do
        {
            batch = await GetBatchAsync(id);
            if (batch.Status == expected) return batch;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException(
            $"Batch {id} did not reach {expected} within {timeoutSeconds} s (it is {batch.Status}).");
    }

    public async ValueTask DisposeAsync()
    {
        Api.Dispose();
        Erp.Dispose();
        await _apiFactory.DisposeAsync();
        await _erpFactory.DisposeAsync();

        SqliteConnection.ClearAllPools();   // release the database file so it can be deleted
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder; the OS cleans it up */ }
    }
}

/// <summary>Shape of the API's error responses: { "error": "..." }.</summary>
public record ErrorBody(string Error);
