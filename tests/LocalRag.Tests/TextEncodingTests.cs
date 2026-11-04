using System.Text;
using LocalRag.Core;
using LocalRag.Infrastructure;

namespace LocalRag.Tests;

public class TextEncodingTests
{
    private const string Japanese = "これは文書検索システムの利用手順です。監視するフォルダを選択し、設定を保存してください。変更された文書は自動的に処理されます。検索結果には元の文章が表示されます。\r\n";
    private const string Chinese = "这是文档搜索系统的使用说明。请选择需要监视的文件夹并保存设置。文件更新后将自动处理。搜索结果显示原始内容，可以根据说明查找相关资料。\r\n";
    private const string Traditional = "這是文件搜尋系統的使用說明。請選擇需要監視的資料夾並儲存設定。文件更新後會自動處理。搜尋結果顯示原始內容，可以根據說明查找相關資料。\r\n";
    private const string Korean = "이 문서는 검색 시스템의 사용 방법을 설명합니다. 감시할 폴더를 선택하고 설정을 저장하세요. 파일이 변경되면 자동으로 처리됩니다. 검색 결과에서 원본 문서의 내용을 확인할 수 있습니다.\r\n";

    private static Encoding Encoder(TextEncodingMode mode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(TextEncodings.All.Single(e => e.Mode == mode).CodePage,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    [Theory]
    [InlineData(TextEncodingMode.ShiftJis, Japanese)]
    [InlineData(TextEncodingMode.EucJp, Japanese)]
    [InlineData(TextEncodingMode.Iso2022Jp, Japanese)]
    [InlineData(TextEncodingMode.Gb18030, Chinese)]
    [InlineData(TextEncodingMode.Big5, Traditional)]
    [InlineData(TextEncodingMode.Korean, Korean)]
    public void DetectsRepresentativeEastAsianDocumentsWithoutChangingText(TextEncodingMode mode, string text)
    {
        var bytes = Encoder(mode).GetBytes(text);
        var decoded = TextDecoding.Decode(bytes);
        Assert.Equal(mode, decoded.Mode);
        Assert.True(decoded.Automatic);
        Assert.Equal(text, decoded.Text);
        Assert.Equal(text, TextDecoding.Decode(bytes, mode).Text);
    }

    [Theory]
    [InlineData(TextEncodingMode.Utf8)]
    [InlineData(TextEncodingMode.Utf16Le)]
    [InlineData(TextEncodingMode.Utf16Be)]
    [InlineData(TextEncodingMode.Utf32Le)]
    [InlineData(TextEncodingMode.Utf32Be)]
    public void BomIsAuthoritativeAndContradictoryOverrideIsRejected(TextEncodingMode mode)
    {
        var text = Japanese + Chinese + Korean + "🌸";
        var encoding = Encoder(mode);
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        Assert.Equal(text, TextDecoding.Decode(bytes).Text);
        Assert.Equal(mode, TextDecoding.Decode(bytes).Mode);
        Assert.Equal(text, TextDecoding.Decode(bytes, mode).Text);
        Assert.Equal("encoding_bom_conflict", Assert.Throws<PipelineValidationException>(() =>
            TextDecoding.Decode(bytes, TextEncodingMode.ShiftJis)).Code);
        Assert.Equal(text, TextDecoding.Decode(encoding.GetBytes(text), mode).Text);
    }

    [Fact]
    public void Utf8AsciiShortOverrideAndMalformedInputAreHandledWithoutReplacement()
    {
        Assert.Equal(Japanese + Chinese + Korean, TextDecoding.Decode(Encoding.UTF8.GetBytes(Japanese + Chinese + Korean)).Text);
        Assert.Equal("plain text", TextDecoding.Decode("plain text"u8.ToArray()).Text);
        Assert.Equal("髙①", TextDecoding.Decode(Encoder(TextEncodingMode.ShiftJis).GetBytes("髙①"), TextEncodingMode.ShiftJis).Text);
        Assert.Throws<PipelineValidationException>(() => TextDecoding.Decode([0x81]));
        Assert.Equal("encoding_invalid", Assert.Throws<PipelineValidationException>(() => TextDecoding.Decode([0xFF], TextEncodingMode.Utf8)).Code);
        Assert.Throws<PipelineValidationException>(() => TextDecoding.Decode([0xFF, 0xFE, 0x41]));
        Assert.Throws<PipelineValidationException>(() => TextDecoding.Decode([0, 1, 2, 3, 255]));
    }

    [Fact]
    public async Task EncodingOverridesSurviveSettingsAndInvalidateIndex()
    {
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "LocalRag.Tests", Guid.NewGuid().ToString("N")));
        var original = await store.LoadAsync();
        var routes = original.Management!.Routes!.Select(r => r with { EncodingMode = TextEncodingMode.ShiftJis }).ToList();
        var changed = original with { Management = original.Management with { Routes = routes } };
        Assert.NotEqual(original.PipelineConfigurationId(), changed.PipelineConfigurationId());
        await store.SaveAsync(changed);
        Assert.Equal(TextEncodingMode.ShiftJis, (await store.LoadAsync()).PipelineFor("example.cs").EncodingMode);
        Assert.NotEmpty((changed.Management with { Routes = [routes[0] with { EncodingMode = (TextEncodingMode)999 }] }).Validate());
    }

    [ServerFact]
    public async Task NonUtf8IndexFetchPrivacyAndEncodingChangeUseSameDecoder()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var input = fixture.Root + "-input";
        Directory.CreateDirectory(input);
        var examples = new[] { ("japanese.txt", TextEncodingMode.ShiftJis, Japanese), ("chinese.txt", TextEncodingMode.Gb18030, Chinese),
            ("korean.txt", TextEncodingMode.Korean, Korean), ("private.txt", TextEncodingMode.ShiftJis, "社外秘\r\n" + Japanese),
            ("code.cs", TextEncodingMode.ShiftJis, "// " + Japanese + "public class 計算 { public int 足す(int a, int b) => a + b; }") };
        foreach (var (name, mode, text) in examples) await File.WriteAllBytesAsync(Path.Combine(input, name), Encoder(mode).GetBytes(text));
        var management = ManagementSettings.CreateDefault() with { Routes = [
            new() { Extensions = [".txt"], SourceMode = SourceCodeMode.Text, ClassifyEnabled = false, FormatPromptId = "" },
            new() { Extensions = [".cs"], SourceMode = SourceCodeMode.CSharp, ClassifyEnabled = false }] };
        var settings = TextRagTests.Settings(input) with { Management = management };
        await using var repository = await TextRagRepository.OpenAsync(fixture.Manager.Connection!);
        Guid publicId;
        using (var service = new TextRagService(repository, new FakeTextAi(), settings, fixture.Root))
        {
            var cycle = await service.ScanAsync();
            Assert.Equal(0, cycle.Failed);
            Assert.Equal(examples.Length, cycle.Indexed);
            var documents = await repository.DocumentsAsync(default);
            foreach (var document in documents)
            {
                Assert.Equal(examples.Single(e => e.Item1 == Path.GetFileName(document.Path)).Item3, document.Original);
                Assert.Contains(document.ProcessingNotices, n => n.StartsWith("encoding_auto_", StringComparison.Ordinal));
                var fetched = await service.FetchAsync(document.DocumentId);
                Assert.NotNull(fetched);
                if (Path.GetFileName(document.Path) == "private.txt")
                { Assert.Equal(DisclosureDecision.Withheld, fetched.Decision); Assert.Null(fetched.Content); }
                else Assert.Equal(document.Original, fetched.Content);
            }
            Assert.Contains(documents.Single(d => d.Path.EndsWith("code.cs", StringComparison.Ordinal)).CodeUnits!, u => u.Name == "足す");
            Assert.NotEmpty(await service.SearchAsync("検索", 20));
            publicId = documents.Single(d => d.Path.EndsWith("japanese.txt", StringComparison.Ordinal)).DocumentId;
        }
        // A decoding change makes the old content unavailable immediately; a failed re-read keeps it unavailable.
        var wrong = settings with { Management = management with { Routes = management.Routes!.Select(r => r with { EncodingMode = TextEncodingMode.Utf8 }).ToList() } };
        using var changed = new TextRagService(repository, new FakeTextAi(), wrong, fixture.Root);
        Assert.Null(await changed.FetchAsync(publicId));
        Assert.Equal(examples.Length, (await changed.ScanAsync()).Failed);
        Assert.Null(await changed.FetchAsync(publicId));
    }
}
