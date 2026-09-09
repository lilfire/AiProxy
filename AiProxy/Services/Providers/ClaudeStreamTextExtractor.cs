using System.Text.Json;
using AiProxy.Services;

namespace AiProxy.Services.Providers;

/// <summary>
/// Plukker ut assistenttekst fra stream-json-linjene til Claude CLI, og sørger for at
/// hver tekstbit sendes videre nøyaktig én gang. Videresender også TodoWrite-kall til
/// øyeblikksbilde-lageret.
/// </summary>
/// <remarks>
/// Med <c>--include-partial-messages</c> skriver CLI-en samme tekst tre ganger: som
/// <c>content_block_delta</c>, som ferdig <c>assistant</c>-blokk og til slutt i
/// <c>result</c>. Deltaene er kilden vi bruker; <c>assistant</c> og <c>result</c> er
/// bare reserve når deltaene mangler. Klassen holder tilstand og gjelder for én kjøring.
/// </remarks>
internal sealed class ClaudeStreamTextExtractor
{
    private readonly Func<string, CancellationToken, Task> _onChunk;
    private readonly ClaudeTodoBlockReader _todoReader;

    private bool _hasStreamedBlockText;
    private bool _hasEmittedText;

    public ClaudeStreamTextExtractor(Func<string, CancellationToken, Task> onChunk, ITodoSnapshotStore todoStore)
    {
        ArgumentNullException.ThrowIfNull(onChunk);

        _onChunk = onChunk;
        _todoReader = new ClaudeTodoBlockReader(todoStore);
    }

    /// <summary>Returnerer false når linjen ikke er gyldig stream-json.</summary>
    public async Task<bool> ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var streamEvent = ParseStreamJsonLine(line);

        if (streamEvent == null)
            return false;

        // TodoWrite må leses her: EmitAssistantTextAsync hopper over hele assistant-blokken
        // når teksten allerede kom som deltaer, og ville da også hoppet over tool_use-blokken.
        _todoReader.ReadTodoBlocks(streamEvent);

        await EmitTextAsync(streamEvent, cancellationToken);

        return true;
    }

    private async Task EmitTextAsync(JsonStreamEvent streamEvent, CancellationToken cancellationToken)
    {
        if (streamEvent.Type == ClaudeStreamConstants.StreamEventType)
        {
            await EmitPartialTextAsync(streamEvent.Event, cancellationToken);
            return;
        }

        if (streamEvent.Type == ClaudeStreamConstants.AssistantEventType)
        {
            await EmitAssistantTextAsync(streamEvent.Message, cancellationToken);
            return;
        }

        if (streamEvent.Type == ClaudeStreamConstants.ResultEventType)
            await EmitResultTextAsync(streamEvent.Result, cancellationToken);
    }

    private async Task EmitPartialTextAsync(JsonElement streamEventBody, CancellationToken cancellationToken)
    {
        if (streamEventBody.ValueKind != JsonValueKind.Object)
            return;

        if (HasOtherType(streamEventBody, ClaudeStreamConstants.ContentBlockDeltaEventType))
            return;

        if (!streamEventBody.TryGetProperty(ClaudeStreamConstants.DeltaProperty, out var delta) || delta.ValueKind != JsonValueKind.Object)
            return;

        if (HasOtherType(delta, ClaudeStreamConstants.TextDeltaType))
            return;

        var text = GetText(delta);

        if (string.IsNullOrEmpty(text))
            return;

        _hasStreamedBlockText = true;
        await SendAsync(text, cancellationToken);
    }

    /// <summary>
    /// En assistant-hendelse gjentar innholdsblokken som nettopp ble strømmet, så teksten
    /// sendes bare når blokken ikke kom som deltaer.
    /// </summary>
    private async Task EmitAssistantTextAsync(JsonElement message, CancellationToken cancellationToken)
    {
        var wasStreamed = _hasStreamedBlockText;
        _hasStreamedBlockText = false;

        if (wasStreamed || message.ValueKind != JsonValueKind.Object)
            return;

        if (!message.TryGetProperty(ClaudeStreamConstants.ContentProperty, out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var block in content.EnumerateArray())
            await EmitContentBlockTextAsync(block, cancellationToken);
    }

    private async Task EmitContentBlockTextAsync(JsonElement block, CancellationToken cancellationToken)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return;

        if (HasOtherType(block, ClaudeStreamConstants.TextBlockType))
            return;

        var text = GetText(block);

        if (string.IsNullOrEmpty(text))
            return;

        await SendAsync(text, cancellationToken);
    }

    private async Task EmitResultTextAsync(string result, CancellationToken cancellationToken)
    {
        if (_hasEmittedText || string.IsNullOrEmpty(result))
            return;

        await SendAsync(result, cancellationToken);
    }

    private async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        _hasEmittedText = true;
        await _onChunk(text, cancellationToken);
    }

    private JsonStreamEvent? ParseStreamJsonLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonStreamEvent>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Sann bare når feltet finnes og er noe annet enn forventet type.</summary>
    private bool HasOtherType(JsonElement element, string expectedType)
    {
        if (!element.TryGetProperty(ClaudeStreamConstants.TypeProperty, out var typeElement))
            return false;

        if (typeElement.ValueKind != JsonValueKind.String)
            return false;

        return typeElement.GetString() != expectedType;
    }

    private string? GetText(JsonElement element)
    {
        if (element.TryGetProperty(ClaudeStreamConstants.TextProperty, out var textElement) && textElement.ValueKind == JsonValueKind.String)
            return textElement.GetString();

        return null;
    }
}
