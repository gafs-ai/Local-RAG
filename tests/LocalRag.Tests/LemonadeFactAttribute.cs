namespace LocalRag.Tests;

public sealed class LemonadeFactAttribute : FactAttribute
{
    public LemonadeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALRAG_TEST_LEMONADE")))
            Skip = "実Lemonade試験はLOCALRAG_TEST_LEMONADEにLemonadeServer.exeの絶対パスを指定します。";
    }
}
