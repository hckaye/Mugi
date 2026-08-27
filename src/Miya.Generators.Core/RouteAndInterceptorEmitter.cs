using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Miya.Generators.Core;

internal static class RouteAndInterceptorEmitter
{
    internal static string EmitRouteTemplates(ImmutableArray<RouteCall> routes)
    {
        var writer = StartFile();
        writer.Open("internal static partial class RouteTemplates");
        var patterns = DistinctPatterns(routes);
        foreach (var route in patterns)
        {
            EmitRouteTemplateField(writer, route);
        }

        writer.Close();
        return writer.ToString();
    }

    internal static string EmitRouteTemplate(RouteCall route)
    {
        var writer = StartFile();
        writer.Open("internal static partial class RouteTemplates");
        EmitRouteTemplateField(writer, route);
        writer.Close();
        return writer.ToString();
    }

    internal static string RouteFieldName(string pattern) =>
        GeneratedNaming.StableIdentifier("Route_", pattern);

    internal static string EmitInterceptors(
        ImmutableArray<InvocationAnalysis> analyses,
        ImmutableArray<JsonTypeModel> models)
    {
        var modelByType = new Dictionary<ITypeSymbol, JsonTypeModel>(SymbolEqualityComparer.Default);
        foreach (var model in models)
        {
            modelByType[model.Type] = model;
        }

        var writer = new CodeWriter();
        EmitInterceptsLocationAttribute(writer);
        writer.Open("namespace Miya.Generated");
        writer.Open("internal static partial class Interceptors");

        var ordered = analyses
            .Where(static analysis =>
                (analysis.InterceptJson && analysis.JsonInterceptAttribute is not null)
                || (analysis.Route is not null && analysis.Route.InterceptAttribute is not null))
            .OrderBy(static analysis => analysis.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(static analysis => analysis.Syntax.SpanStart)
            .ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var analysis = ordered[index];
            if (analysis.InterceptJson)
            {
                if (analysis.JsonType is null || analysis.JsonTargetMethod is null
                    || !modelByType.TryGetValue(analysis.JsonType, out var model))
                {
                    continue;
                }

                EmitJsonInterceptor(
                    writer,
                    analysis,
                    model,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (analysis.Route is not null)
            {
                EmitRouteInterceptor(writer, analysis.Route, RouteFieldName(analysis.Route.Pattern), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            writer.Line();
        }

        writer.Close();
        writer.Close();
        return writer.ToString();
    }

    internal static string EmitInterceptor(InvocationAnalysis analysis, JsonTypeModel? model)
    {
        var writer = StartFile();
        writer.Open("internal static partial class Interceptors");
        var suffix = InterceptorKey(analysis);
        if (analysis.InterceptJson && model is not null)
        {
            EmitJsonInterceptor(writer, analysis, model, suffix);
        }
        else if (analysis.Route is not null)
        {
            EmitRouteInterceptor(
                writer,
                analysis.Route,
                RouteFieldName(analysis.Route.Pattern),
                suffix);
        }

        writer.Close();
        return writer.ToString();
    }

    internal static string EmitInterceptsLocationAttribute()
    {
        var writer = new CodeWriter();
        EmitInterceptsLocationAttribute(writer);
        return writer.ToString();
    }

    internal static string InterceptorKey(InvocationAnalysis analysis)
    {
        var kind = analysis.InterceptJson ? "Json_" : "Route_";
        var location = analysis.Syntax.SyntaxTree.FilePath + "_" +
            analysis.Syntax.SpanStart.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" +
            analysis.Syntax.Span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return GeneratedNaming.StableIdentifier(kind, location);
    }

    private static void EmitJsonInterceptor(
        CodeWriter writer,
        InvocationAnalysis analysis,
        JsonTypeModel model,
        string suffix)
    {
        var method = analysis.JsonTargetMethod!;
        var receiverType = GetReceiverType(method);
        var typeParameters = CollectTypeParameters(receiverType, method.Parameters.Select(static parameter => parameter.Type));
        writer.Line(analysis.JsonInterceptAttribute!);
        writer.Line(
            "internal static " + TypeNames.Display(method.ReturnType) + " InterceptJson_" + suffix +
            TypeParameterList(typeParameters) + "(");
        writer.Line("    this " + TypeNames.Display(receiverType) + " receiver,");
        writer.Line("    " + TypeNames.Display(method.Parameters[0].Type) + " value)");
        WriteConstraints(writer, typeParameters);

        // The call site may have inferred an annotated reference type (User?). The codec is
        // generated for the underlying type, so forward with the non-nullable type argument.
        var typeArgument = method.TypeArguments[0];
        var forgiveNull = typeArgument.IsReferenceType
            && typeArgument.NullableAnnotation == NullableAnnotation.Annotated;
        writer.Line(
            "    => receiver.Json<" + TypeNames.NonNullableDisplay(typeArgument) + ">(value" +
            (forgiveNull ? "!" : string.Empty) +
            ", global::Miya.Json.Json.ResolveCodec<" + TypeNames.NonNullableDisplay(typeArgument) + ">(" +
            TypeNames.CodecName(model.Type) + ".Instance));");
    }

    private static void EmitRouteInterceptor(
        CodeWriter writer,
        RouteCall route,
        string routeFieldName,
        string suffix)
    {
        var method = route.TargetMethod;
        var receiverType = GetReceiverType(method);
        var typeParameters = CollectTypeParameters(receiverType, method.Parameters.Select(static parameter => parameter.Type));
        writer.Line(route.InterceptAttribute!);
        writer.Line(
            "internal static " + TypeNames.Display(method.ReturnType) + " InterceptRoute_" + suffix +
            TypeParameterList(typeParameters) + "(");
        writer.Line("    this " + TypeNames.Display(receiverType) + " receiver,");
        for (var parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
        {
            var parameter = method.Parameters[parameterIndex];
            var delimiter = parameterIndex == method.Parameters.Length - 1 ? ")" : ",";
            writer.Line(
                "    " + TypeNames.Display(parameter.Type) + " " +
                GeneratedNaming.ParameterIdentifier(parameter.Name) + delimiter);
        }

        WriteConstraints(writer, typeParameters);
        var patternIndex = method.Name == "On" ? 1 : 0;
        var arguments = new List<string>();
        for (var parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
        {
            arguments.Add(parameterIndex == patternIndex
                ? "RouteTemplates." + routeFieldName
                : GeneratedNaming.ParameterIdentifier(method.Parameters[parameterIndex].Name));
        }

        writer.Line("    => receiver." + method.Name + "(" + string.Join(", ", arguments) + ");");
    }

    private static INamedTypeSymbol GetReceiverType(IMethodSymbol method)
    {
        var original = method.OriginalDefinition.ContainingType;
        return original.TypeParameters.Length != 0 ? method.ContainingType : original;
    }

    private static List<ITypeParameterSymbol> CollectTypeParameters(
        ITypeSymbol receiverType,
        IEnumerable<ITypeSymbol> parameterTypes)
    {
        var result = new List<ITypeParameterSymbol>();
        AddTypeParameters(receiverType, result);
        foreach (var parameterType in parameterTypes)
        {
            AddTypeParameters(parameterType, result);
        }

        return result;
    }

    private static void AddTypeParameters(ITypeSymbol type, List<ITypeParameterSymbol> result)
    {
        if (type is ITypeParameterSymbol parameter)
        {
            if (!result.Any(existing => SymbolEqualityComparer.Default.Equals(existing, parameter)))
            {
                result.Add(parameter);
            }

            return;
        }

        if (type is IArrayTypeSymbol array)
        {
            AddTypeParameters(array.ElementType, result);
            return;
        }

        if (type is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments)
            {
                AddTypeParameters(argument, result);
            }
        }
    }

    private static string TypeParameterList(List<ITypeParameterSymbol> parameters)
    {
        return parameters.Count == 0
            ? string.Empty
            : "<" + string.Join(", ", parameters.Select(static parameter => GeneratedNaming.ParameterIdentifier(parameter.Name))) + ">";
    }

    private static void WriteConstraints(CodeWriter writer, List<ITypeParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
        {
            var constraints = new List<string>();
            if (parameter.HasUnmanagedTypeConstraint)
            {
                constraints.Add("unmanaged");
            }
            else if (parameter.HasValueTypeConstraint)
            {
                constraints.Add("struct");
            }
            else if (parameter.HasReferenceTypeConstraint)
            {
                constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
            }

            constraints.AddRange(parameter.ConstraintTypes.Select(TypeNames.Display));
            if (parameter.HasNotNullConstraint)
            {
                constraints.Add("notnull");
            }

            if (parameter.HasConstructorConstraint)
            {
                constraints.Add("new()");
            }

            if (constraints.Count != 0)
            {
                writer.Line(
                    "    where " + GeneratedNaming.ParameterIdentifier(parameter.Name) + " : " +
                    string.Join(", ", constraints));
            }
        }
    }

    private static CodeWriter StartFile()
    {
        var writer = new CodeWriter();
        writer.Line("// <auto-generated/>");
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace Miya.Generated;");
        writer.Line();
        return writer;
    }

    private static void EmitRouteTemplateField(CodeWriter writer, RouteCall route)
    {
        writer.Line("internal static readonly global::Miya.RouteTemplate " + RouteFieldName(route.Pattern) + " =");
        writer.Line("    global::Miya.RouteTemplate.Precompiled(");
        writer.Line("        " + GeneratedNaming.Literal(route.Pattern) + ",");
        writer.Line("        " + StringArray(route.Template.Segments.Select(static segment => segment.Value)) + ",");
        writer.Line("        " + ByteArray(route.Template.Segments.Select(static segment => segment.Kind)) + ",");
        writer.Line("        " + IntArray(route.Template.Segments.Select(static segment => segment.ParameterIndex)) + ",");
        writer.Line("        " + StringArray(route.Template.ParameterNames) + ");");
    }

    private static void EmitInterceptsLocationAttribute(CodeWriter writer)
    {
        writer.Line("// <auto-generated/>");
        writer.Line("#nullable enable");
        writer.Line();
        writer.Open("namespace System.Runtime.CompilerServices");
        writer.Line("[global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        writer.Open("internal sealed class InterceptsLocationAttribute : global::System.Attribute");
        writer.Open("public InterceptsLocationAttribute(int version, string data)");
        writer.Close();
        writer.Close();
        writer.Close();
        writer.Line();
    }

    private static List<RouteCall> DistinctPatterns(ImmutableArray<RouteCall> routes)
    {
        var result = new List<RouteCall>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes.OrderBy(static route => route.Pattern, StringComparer.Ordinal))
        {
            if (seen.Add(route.Pattern))
            {
                result.Add(route);
            }
        }

        return result;
    }

    private static string StringArray(IEnumerable<string> values)
    {
        return "new string[] { " + string.Join(", ", values.Select(GeneratedNaming.Literal)) + " }";
    }

    private static string ByteArray(IEnumerable<byte> values)
    {
        return "new byte[] { " + string.Join(", ", values.Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " }";
    }

    private static string IntArray(IEnumerable<int> values)
    {
        return "new int[] { " + string.Join(", ", values.Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))) + " }";
    }
}
