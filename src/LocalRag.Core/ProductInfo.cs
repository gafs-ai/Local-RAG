using System.Reflection;

namespace LocalRag.Core;

public static class ProductInfo
{
    public static string Version { get; } = typeof(ProductInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    public static string VersionLabel => "バージョン" + Version;
}
