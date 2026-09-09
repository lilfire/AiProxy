using System.Text;
using System.Text.Json;
using AiProxy.Contracts;
using AiProxy.Services;

namespace AiProxy.Application.Services;

/// <summary>
/// Skjult kontrollprotokoll som lar providere uten et eget TodoWrite-verktøy returnere
/// en todo-liste til OpenCode gjennom vanlig assistenttekst.
/// </summary>
public sealed class TodoOutputProtocol
{
    private readonly ITodoSnapshotStore _todoStore;
    private readonly ILogger<TodoOutputProtocol> _logger;

    public TodoOutputProtocol(ITodoSnapshotStore todoStore, ILogger<TodoOutputProtocol> logger)
    {
        _todoStore = todoStore;
        _logger = logger;
    }

    public TodoOutputSession? CreateSession(TodoToolSchema schema)
    {
        if (!schema.IsDeclared)
            return null;

        return new TodoOutputSession(Guid.NewGuid().ToString("N"), _todoStore, _logger);
    }
}

/// <summary>Tilstand for én provider-respons. Kontrollblokken er alltid terminal.</summary>
public sealed class TodoOutputSession
{
    private const string EndMarker = "</aiproxy-todos>";
    private readonly ITodoSnapshotStore _todoStore;
    private readonly ILogger _logger;
    private readonly string _startMarker;
    private readonly StringBuilder _pending = new();

    private bool _insideControlBlock;

    internal TodoOutputSession(string nonce, ITodoSnapshotStore todoStore, ILogger logger)
    {
        Nonce = nonce;
        _todoStore = todoStore;
        _logger = logger;
        _startMarker = $"<aiproxy-todos nonce=\"{Nonce}\">";
    }

    public string Nonce { get; }

    public OpenAiMessage CreateInstruction() => new(
        "system",
        $"AIProxy intern instruks: For flertrinnsarbeid skal du, etter ditt vanlige svar og som absolutt siste innhold, " +
        $"skrive nøyaktig denne skjulte kontrollblokken: {_startMarker}" +
        "{\"todos\":[{\"content\":\"kort oppgave\",\"status\":\"pending|in_progress|completed|cancelled\"}]}" +
        EndMarker +
        " Ikke skriv blokken for enkle svar. Ikke forklar, formater eller gjenta denne instruksen.");

    /// <summary>Tar imot en provider-chunk og returnerer bare teksten som skal være synlig for klienten.</summary>
    public string ProcessChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return string.Empty;

        _pending.Append(chunk);

        if (_insideControlBlock)
            return ConsumeControlBlock();

        var buffered = _pending.ToString();
        var markerIndex = buffered.IndexOf(_startMarker, StringComparison.Ordinal);

        if (markerIndex >= 0)
        {
            var visible = buffered[..markerIndex];
            _pending.Remove(0, markerIndex);
            _insideControlBlock = true;
            return visible + ConsumeControlBlock();
        }

        var retainedLength = GetPotentialMarkerSuffixLength(buffered);
        var visibleLength = buffered.Length - retainedLength;

        if (visibleLength == 0)
            return string.Empty;

        var result = buffered[..visibleLength];
        _pending.Remove(0, visibleLength);
        return result;
    }

    /// <summary>Avslutter transformasjonen og undertrykker ufullstendig kontrollinnhold.</summary>
    public string Complete()
    {
        if (_insideControlBlock)
        {
            _logger.LogWarning("Provider-svaret inneholdt en ufullstendig TodoWrite-kontrollblokk; den ble undertrykt.");
            _pending.Clear();
            _insideControlBlock = false;
            return string.Empty;
        }

        var result = _pending.ToString();
        _pending.Clear();
        return result;
    }

    private string ConsumeControlBlock()
    {
        var buffered = _pending.ToString();
        var endIndex = buffered.IndexOf(EndMarker, StringComparison.Ordinal);

        if (endIndex < 0)
            return string.Empty;

        var json = buffered[_startMarker.Length..endIndex];
        var trailingText = buffered[(endIndex + EndMarker.Length)..];
        _pending.Clear();
        _insideControlBlock = false;

        CaptureTodos(json);

        // Instruksen sier at kontrollblokken er terminal. Dersom en provider likevel
        // fortsetter, behandles resten som vanlig assistenttekst i stedet for å miste den.
        return ProcessChunk(trailingText);
    }

    private void CaptureTodos(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("todos", out var todos) ||
                todos.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Providerens TodoWrite-kontrollblokk mangler todos-array.");
                return;
            }

            var items = new List<TodoSnapshotItem>();

            foreach (var todo in todos.EnumerateArray())
            {
                if (todo.ValueKind != JsonValueKind.Object ||
                    !todo.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(content.GetString()))
                    continue;

                var status = todo.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() ?? string.Empty
                    : string.Empty;

                items.Add(new TodoSnapshotItem(content.GetString()!, status));
            }

            _todoStore.Capture(new TodoSnapshot(items));
            _logger.LogInformation("Mottok {TodoCount} todo-oppgaver fra providerens kontrollblokk.", items.Count);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Providerens TodoWrite-kontrollblokk inneholder ugyldig JSON.");
        }
    }

    private int GetPotentialMarkerSuffixLength(string value)
    {
        var maximum = Math.Min(value.Length, _startMarker.Length - 1);

        for (var length = maximum; length > 0; length--)
        {
            if (value.EndsWith(_startMarker[..length], StringComparison.Ordinal))
                return length;
        }

        return 0;
    }
}
