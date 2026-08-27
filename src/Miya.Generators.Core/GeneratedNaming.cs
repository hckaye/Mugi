using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Miya.Generators.Core;

internal static class GeneratedNaming
{
    internal static string JsonPropertyName(string name, JsonNaming naming)
    {
        return naming == JsonNaming.PascalCase ? name : CamelCase(name);
    }

    internal static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    internal static string Utf8Literal(string value) => Literal(value) + "u8";

    internal static string Identifier(string value)
    {
        return SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None ? value : "@" + value;
    }

    internal static string ParameterIdentifier(string value)
    {
        return SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None ? value : "@" + value;
    }

    internal static string StableIdentifier(string prefix, string value)
    {
        var builder = new StringBuilder(prefix);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('_');
                builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                builder.Append('_');
            }
        }

        return builder.ToString();
    }

    internal static string JsonMemberPrefix(string jsonName, bool first)
    {
        var builder = new StringBuilder();
        builder.Append(first ? "{\"" : ",\"");
        foreach (var character in jsonName)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append("\":");
        return builder.ToString();
    }

    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || !char.IsUpper(name[0]))
        {
            return name;
        }

        var characters = name.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (index > 0 && !char.IsUpper(characters[index]))
            {
                break;
            }

            if (index > 0 && index + 1 < characters.Length && !char.IsUpper(characters[index + 1]))
            {
                break;
            }

            characters[index] = char.ToLowerInvariant(characters[index]);
        }

        return new string(characters);
    }
}
