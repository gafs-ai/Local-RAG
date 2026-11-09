// Copyright (C) 2026 GAFS Inc.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class ChunkRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SingleParagraphHasUniqueCodeResultWithoutModelCall(bool legacy, bool categoryOverride)
    {
        var settings = new TextRagSettings { ChunkPrompt = legacy ? TextRagSettings.LegacyChunkPrompt : TextRagSettings.DefaultChunkPrompt };
        var category = new TextCategory { ChunkPrompt = categoryOverride ? TextRagSettings.LegacyChunkPrompt : null };
        var ai = new CapturingAi("{\"chunks\":[{\"start\":1,\"end\":99}]}");
        var chunks = await new TextAiPipeline(ai, settings).ChunkAsync([new(1, "公開資料：監視試験。花壇の水やりは朝に行います。")], category, default);
        Assert.Equal(0, ai.Calls);
        Assert.Equal(1, Assert.Single(chunks).End);
    }

    [Fact]
    public async Task CustomCategoryPromptAndNonconsecutiveIdsArePreserved()
    {
        const string custom = "Split each topic separately. Keep the user's classification instructions.";
        var ai = new CapturingAi("{\"chunks\":[{\"start\":3,\"end\":3},{\"start\":8,\"end\":8}]}");
        var chunks = await new TextAiPipeline(ai, new() { MaxChunkCharacters = 50 })
            .ChunkAsync([new(3, new string('a', 30)), new(8, new string('b', 30))], new() { ChunkPrompt = custom }, default);
        Assert.StartsWith(custom, ai.Prompt);
        Assert.Contains("{\"chunks\":[{\"start\":3,\"end\":3},{\"start\":8,\"end\":8}]}", ai.Prompt);
        Assert.Equal(2, chunks.Count);
    }

    [Fact]
    public async Task InvalidModelEndIsDiscardedAfterRetryAndSourceIsSplitByCode()
    {
        var ai = new CapturingAi("```json\n{\"chunks\":[{\"start\":1,\"end\":2}]}\n```");
        var pipeline = new TextAiPipeline(ai, new());
        var chunks = await pipeline.ChunkAsync([new(1, "source"), new(3, "next")], new(), default);
        Assert.Equal(new[] { new TextChunk(1, 1, "source"), new(3, 3, "next") }, chunks);
        Assert.Contains("chunk_code_fallback", pipeline.Notices);
        Assert.Equal(2, ai.Calls);
        Assert.Contains("A chunk end does not identify an existing paragraph", ai.Prompt);
        Assert.Contains("終端段落", PipelineErrors.Describe("chunk", "chunk_end"));
    }

    [Fact]
    public void ErrorDescriptionsNeverEchoUnknownValuesOrProviderMessages()
    {
        Assert.Equal("取り込み：処理に失敗しました", PipelineErrors.Describe("private source", "private response"));
        Assert.Equal("connection", PipelineErrors.DetailCode(new HttpRequestException("private provider message")));
    }

    [ServerFact]
    public async Task FallbackChunksAndNoticesPersistAndResumeAfterEmbeddingFailure()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "monitor.txt"), "公開資料：監視試験。\n\n花壇の水やりは朝に行います。");
        var settings = TextRagTests.Settings(input) with { TextPipeline = new() { ParagraphChildren = false } };
        Guid id;
        await using (var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!))
        using (var service = new TextRagService(repository, new FakeTextAi { InvalidChunkEnd = true, FailEmbeddingCall = 2 }, settings, fixture.Root))
        {
            Assert.Equal(1, (await service.ScanAsync()).Failed);
            var failed = Assert.Single(await repository.DocumentsAsync(default));
            id = failed.DocumentId;
            Assert.Equal("embedding", failed.ErrorStage);
            Assert.Equal("connection", failed.ErrorDetailCode);
            Assert.Contains("chunk_code_fallback", failed.ProcessingNotices);
            Assert.NotNull(failed.Paragraphs);
            Assert.Equal(2, failed.Chunks!.Count);
            Assert.Equal(1, failed.EmbeddedChunks);
            Assert.Empty(await service.SearchAsync("水やり", 5));
            Assert.Null(await service.FetchAsync(id));
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await using var reopened = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        Assert.Equal("connection", Assert.Single(await reopened.DocumentsAsync(default)).ErrorDetailCode);
        var ai = new FakeTextAi();
        using var resumed = new TextRagService(reopened, ai, settings, fixture.Root,
            new RetryTestClock(DateTimeOffset.UtcNow.AddMinutes(6)));
        Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
        Assert.Equal(0, ai.TransformCalls); // All text checkpoints were reused.
        Assert.Equal(1, ai.EmbeddingCalls);
        var ready = Assert.Single(await reopened.DocumentsAsync(default));
        Assert.Equal(id, ready.DocumentId);
        Assert.Null(ready.ErrorCode);
        Assert.Null(ready.ErrorStage);
        Assert.Null(ready.ErrorDetailCode);
        Assert.Contains("chunk_code_fallback", ready.ProcessingNotices);
        Assert.Equal(ready.Original, (await resumed.FetchAsync(id))!.Content);
    }

    private sealed class CapturingAi(string response) : ITextRagAi
    {
        public string Prompt { get; private set; } = "";
        public int Calls { get; private set; }
        public Task<string> CompleteAsync(string system, string input, CancellationToken token)
        {
            Calls++;
            Prompt = system;
            return Task.FromResult(response);
        }
        public Task<VectorBatch> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken token) => throw new NotSupportedException();
    }
}
