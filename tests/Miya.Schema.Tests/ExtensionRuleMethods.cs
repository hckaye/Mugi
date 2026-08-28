namespace Miya.Schema.Tests.ExtensionRules;

internal static class ExtensionRuleMethods
{
    internal static bool IsAllowed(this string value) => value == "allowed";
}
