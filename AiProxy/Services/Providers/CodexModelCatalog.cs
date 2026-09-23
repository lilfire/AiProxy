using System.Text.Json;

namespace AiProxy.Services.Providers;

/// <summary>Reads the picker-visible models from the authenticated Codex app server.</summary>
public sealed class CodexModelCatalog
{
    private const int TimeoutSeconds = 30;
    private const int PageSize = 100;
    private readonly ShellCommandRunner _commandRunner;
    private readonly ILogger<CodexModelCatalog> _logger;
    private readonly string _cachePath;

    public CodexModelCatalog(ShellCommandRunner commandRunner, ILogger<CodexModelCatalog> logger)
        : this(commandRunner, logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiProxy", "codex-models.json"))
    {
    }

    internal CodexModelCatalog(ShellCommandRunner commandRunner, ILogger<CodexModelCatalog> logger, string cachePath)
    {
        _commandRunner = commandRunner;
        _logger = logger;
        _cachePath = cachePath;
    }

    public async Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await FetchAsync(cancellationToken);
            try
            {
                await SaveAsync(models, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Kunne ikke lagre Codex-modellkatalogen");
            }

            _logger.LogInformation("Fant {ModelCount} modeller fra Codex", models.Count);
            return models;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kunne ikke hente Codex-modellkatalogen; bruker sist lagrede katalog hvis tilgjengelig");
            return await LoadAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<string>> FetchAsync(CancellationToken cancellationToken)
    {
        using var discoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryTimeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var models = new List<string>();
        var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            const long initializeId = 1;
            const long listId = 2;
            var parameters = new Dictionary<string, object?>
            {
                ["limit"] = PageSize,
                ["includeHidden"] = false
            };
            if (cursor != null)
                parameters["cursor"] = cursor;

            var requests = new[]
            {
                JsonSerializer.Serialize(new { id = initializeId, method = "initialize", @params = new
                {
                    clientInfo = new { name = "AiProxy", title = "AiProxy", version = "1.0" },
                    capabilities = new { experimentalApi = false, requestAttestation = false }
                }}),
                JsonSerializer.Serialize(new { method = "initialized", @params = new { } }),
                JsonSerializer.Serialize(new { id = listId, method = "model/list", @params = parameters })
            };
            var response = await _commandRunner.RunJsonRpcAsync(
                "codex", ["app-server", "--stdio"], requests, listId, TimeoutSeconds, initializeId, discoveryTimeout.Token);
            using var payload = JsonDocument.Parse(response);
            var root = payload.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("Codex returnerte ingen gyldig modelliste.");

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("model", out var modelElement) ||
                    modelElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(modelElement.GetString()))
                    throw new JsonException("Codex returnerte en modell uten gyldig ID.");

                if (entry.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
                    continue;

                var model = modelElement.GetString()!;
                if (seenModels.Add(model))
                    models.Add(model);
            }

            cursor = root.TryGetProperty("nextCursor", out var nextCursor) && nextCursor.ValueKind == JsonValueKind.String
                ? nextCursor.GetString() : null;
            if (cursor != null && !seenCursors.Add(cursor))
                throw new JsonException("Codex returnerte en gjentatt sidepeker for modellisten.");
        } while (cursor != null);

        return models;
    }

    private async Task SaveAsync(IReadOnlyList<string> models, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(new CachedCatalog(models.ToArray()));
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath))
                return [];

            var json = await File.ReadAllTextAsync(_cachePath, cancellationToken);
            var cached = JsonSerializer.Deserialize<CachedCatalog>(json);
            if (cached?.ModelIds == null || cached.ModelIds.Any(string.IsNullOrWhiteSpace))
                throw new JsonException("Den lagrede Codex-modellkatalogen er ugyldig.");

            return cached.ModelIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kunne ikke lese lagret Codex-modellkatalog");
            return [];
        }
    }

    private sealed record CachedCatalog(string[] ModelIds);
}
