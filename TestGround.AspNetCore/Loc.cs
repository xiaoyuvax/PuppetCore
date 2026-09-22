using System.Globalization;

namespace TestGround.AspNetCore;

internal static class Loc
{
    private static readonly string? Forced = Environment.GetEnvironmentVariable("PUPPET_TESTGROUND_LANG");

    internal static bool En => Forced is not null
        ? Forced.StartsWith("en", StringComparison.OrdinalIgnoreCase)
        : !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    internal static string T(string zh, string en) => En ? en : zh;
}
