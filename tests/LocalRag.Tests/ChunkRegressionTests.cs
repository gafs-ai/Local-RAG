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
    public async Task ObservedInvalidEndIsStillRejectedAfterOneRetry()
    {
        var ai = new CapturingAi("```json\n{\"chunks\":[{\"start\":1,\"end\":2}]}\n```");
        var error = await Assert.ThrowsAsync<PipelineValidationException>(() =>
            new TextAiPipeline(ai, new()).ChunkAsync([new(1, "source"), new(3, "next")], new(), default));
        Assert.Equal("chunk_end", error.Code);
        Assert.Equal(2, ai.Calls);
        Assert.Contains("A chunk end does not identify an existing paragraph", ai.Prompt);
        Assert.Contains("終端段落", PipelineErrors.Describe("chunk", error.Code));
    }

    [Fact]
    public void ErrorDescriptionsNeverEchoUnknownValuesOrProviderMessages()
    {
        Assert.Equal("取り込み：処理に失敗しました", PipelineErrors.Describe("private source", "private response"));
        Assert.Equal("connection", PipelineErrors.DetailCode(new HttpRequestException("private provider message")));
    }

    [ServerFact]
    public async Task FailedChunkIsPersistedNotSearchableAndResumesFromSavedParagraphs()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-inputs";
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "monitor.txt"), "公開資料：監視試験。\n\n花壇の水やりは朝に行います。");
        var settings = TextRagTests.Settings(input);
        Guid id;
        await using (var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!))
        using (var service = new TextRagService(repository, new FakeTextAi { InvalidChunkEnd = true }, settings, fixture.Root))
        {
            Assert.Equal(1, (await service.ScanAsync()).Failed);
            var failed = Assert.Single(await repository.DocumentsAsync(default));
            id = failed.DocumentId;
            Assert.Equal("chunk", failed.ErrorStage);
            Assert.Equal("chunk_end", failed.ErrorDetailCode);
            Assert.NotNull(failed.Paragraphs);
            Assert.Null(failed.Chunks);
            Assert.Equal(0, failed.EmbeddedChunks);
            Assert.Empty(await service.SearchAsync("水やり", 5));
            Assert.Null(await service.FetchAsync(id));
        }
        await fixture.Manager.ShutdownAsync(true);
        await fixture.Manager.EnsureRunningAsync(fixture.Settings);
        await using var reopened = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        Assert.Equal("chunk_end", Assert.Single(await reopened.DocumentsAsync(default)).ErrorDetailCode);
        var ai = new FakeTextAi();
        using var resumed = new TextRagService(reopened, ai, settings, fixture.Root);
        Assert.Equal(1, (await resumed.ScanAsync()).Indexed);
        Assert.Equal(1, ai.TransformCalls); // Classification and formatting checkpoints were reused.
        Assert.Equal(2, ai.EmbeddingCalls);
        var ready = Assert.Single(await reopened.DocumentsAsync(default));
        Assert.Equal(id, ready.DocumentId);
        Assert.Null(ready.ErrorCode);
        Assert.Null(ready.ErrorStage);
        Assert.Null(ready.ErrorDetailCode);
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
