using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using LocalRag.Core;

namespace LocalRag.Infrastructure;

internal static class LocalDataSourceIdentity
{
    public static string NewName()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return AppSettings.DefaultDataSourceName + "-" + new string(Enumerable.Range(0, 4)
            .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }

    public static string Owner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var machine = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machine) || identity.User is null)
            throw new InvalidDataException("データソースのPC・ユーザー識別情報を取得できません。");
        return TextRagSettings.Hash(machine + "\0" + identity.User.Value);
    }
}
