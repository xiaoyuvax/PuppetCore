using System.Globalization;
using System.Windows.Markup;

namespace TestGround.Wpf;

internal static class Loc
{
    private static readonly string? Forced = Environment.GetEnvironmentVariable("PUPPET_TESTGROUND_LANG");

    internal static bool En => Forced is not null
        ? Forced.StartsWith("en", StringComparison.OrdinalIgnoreCase)
        : !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    internal static string T(string zh, string en) => En ? en : zh;
}

public sealed class TrExtension : MarkupExtension
{
    public string Zh { get; set; } = "";
    public string En { get; set; } = "";

    public TrExtension() { }

    public TrExtension(string zh, string en) => (Zh, En) = (zh, en);

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Zh, En);
}
