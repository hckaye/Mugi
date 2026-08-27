using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Miya.Generators.Core;

internal sealed class JsonInvocationCandidate
{
    internal JsonInvocationCandidate(InvocationAnalysis analysis)
    {
        Analysis = analysis;
        ShapeKey = CreateShapeKey(analysis.JsonType ?? analysis.SchemaDefinition!.InputType);
    }

    internal InvocationAnalysis Analysis { get; }

    internal string ShapeKey { get; }

    private static string CreateShapeKey(ITypeSymbol type)
    {
        if (!JsonTypeGraphBuilder.TryBuild(type, out var graph, out var error))
        {
            return TypeNames.Key(type) + "|unsupported:" + error;
        }

        var builder = new StringBuilder();
        foreach (var model in graph!.Models)
        {
            builder.Append(TypeNames.Key(model.Type));
            builder.Append('|');
            builder.Append((int)model.Kind);
            foreach (var property in model.Properties)
            {
                builder.Append('|');
                builder.Append(property.Property.Name);
                builder.Append(':');
                builder.Append(TypeNames.Key(property.Property.Type));
                builder.Append(':');
                builder.Append(property.Property.IsRequired ? 'R' : 'O');
                builder.Append(property.IsPrimary ? 'P' : 'S');
            }

            builder.Append(';');
        }

        return builder.ToString();
    }
}

internal sealed class JsonInvocationCandidateComparer : IEqualityComparer<JsonInvocationCandidate>
{
    internal static readonly JsonInvocationCandidateComparer Instance = new();

    public bool Equals(JsonInvocationCandidate? x, JsonInvocationCandidate? y)
    {
        return ReferenceEquals(x, y)
            || (x is not null && y is not null
                && string.Equals(x.ShapeKey, y.ShapeKey, StringComparison.Ordinal));
    }

    public int GetHashCode(JsonInvocationCandidate obj) => StringComparer.Ordinal.GetHashCode(obj.ShapeKey);
}

internal sealed class GeneratedSourceComparer : IEqualityComparer<GeneratedSource>
{
    internal static readonly GeneratedSourceComparer Instance = new();

    public bool Equals(GeneratedSource? x, GeneratedSource? y)
    {
        return ReferenceEquals(x, y)
            || (x is not null && y is not null
                && string.Equals(x.HintName, y.HintName, StringComparison.Ordinal)
                && string.Equals(x.Source, y.Source, StringComparison.Ordinal));
    }

    public int GetHashCode(GeneratedSource obj)
    {
        unchecked
        {
            return (StringComparer.Ordinal.GetHashCode(obj.HintName) * 397)
                ^ StringComparer.Ordinal.GetHashCode(obj.Source);
        }
    }
}
