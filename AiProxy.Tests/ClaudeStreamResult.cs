using AiProxy.Services;

namespace AiProxy.Tests;

internal sealed record ClaudeStreamResult(List<string> Chunks, TodoSnapshot? Snapshot);
