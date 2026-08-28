using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Mugi.Generators.Core;

internal sealed class SchemaBindingModel
{
    internal SchemaBindingModel(
        ITypeSymbol inputType,
        JsonTypeModel inputModel,
        ImmutableArray<SchemaBoundField> fields)
    {
        InputType = inputType;
        InputModel = inputModel;
        Fields = fields;
    }

    internal ITypeSymbol InputType { get; }

    internal JsonTypeModel InputModel { get; }

    internal ImmutableArray<SchemaBoundField> Fields { get; }

    internal string ShapeKey => string.Join(
        ";",
        Fields.Select(static boundField =>
            boundField.Property.Name + ":" + boundField.Source + ":" + boundField.HeaderName + ":" +
            string.Join(",", boundField.Rules.Select(static rule =>
                rule.Kind + "(" + string.Join("|", rule.Values.Select(static value =>
                    Convert.ToString(value, CultureInfo.InvariantCulture))) + ")" +
                rule.Predicate + ":" + rule.Message))));
}

internal sealed class SchemaBoundField
{
    internal SchemaBoundField(
        IPropertySymbol property,
        SchemaFieldSource source,
        string? headerName,
        ImmutableArray<SchemaRuleDeclaration> rules,
        Location location)
    {
        Property = property;
        Source = source;
        HeaderName = headerName;
        Rules = rules;
        Location = location;
    }

    internal IPropertySymbol Property { get; }

    internal SchemaFieldSource Source { get; }

    internal string? HeaderName { get; }

    internal ImmutableArray<SchemaRuleDeclaration> Rules { get; }

    internal Location Location { get; }
}

internal static class SchemaBindingBuilder
{
    internal static ImmutableArray<SchemaBindingModel> Build(
        ImmutableArray<InvocationAnalysis> analyses,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var partDefinitions = new Dictionary<ITypeSymbol, SchemaPartDefinition>(SymbolEqualityComparer.Default);
        foreach (var partDefinition in analyses
                     .Where(static analysis => analysis.SchemaPartDefinition is not null)
                     .Select(static analysis => analysis.SchemaPartDefinition!))
        {
            if (!partDefinitions.ContainsKey(partDefinition.PartType))
            {
                partDefinitions.Add(partDefinition.PartType, partDefinition);
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.DuplicateSchemaPart,
                    partDefinition.Location,
                    partDefinition.PartType.ToDisplayString()));
            }
        }

        var definitions = new Dictionary<ITypeSymbol, SchemaDefinition>(SymbolEqualityComparer.Default);
        foreach (var definition in analyses
                     .Where(static analysis => analysis.SchemaDefinition is not null)
                     .Select(static analysis => analysis.SchemaDefinition!))
        {
            if (!definitions.ContainsKey(definition.InputType))
            {
                definitions.Add(
                    definition.InputType,
                    MergeParts(definition, partDefinitions, diagnostics));
            }
            else
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.AmbiguousSchemaBinding,
                    definition.Location,
                    definition.InputType.ToDisplayString()));
            }
        }

        var result = new Dictionary<ITypeSymbol, SchemaBindingModel>(SymbolEqualityComparer.Default);
        foreach (var endpoint in analyses
                     .Where(static analysis => analysis.SchemaEndpoint is not null)
                     .Select(static analysis => analysis.SchemaEndpoint!)
                     .OrderBy(static endpoint => TypeNames.Key(endpoint.InputType), StringComparer.Ordinal)
                     .ThenBy(static endpoint => endpoint.Pattern, StringComparer.Ordinal))
        {
            definitions.TryGetValue(endpoint.InputType, out var definition);
            var model = TryBuild(endpoint, definition, diagnostics);
            if (model is null)
            {
                continue;
            }

            if (result.TryGetValue(endpoint.InputType, out var previous))
            {
                if (!string.Equals(previous.ShapeKey, model.ShapeKey, StringComparison.Ordinal))
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticCatalog.AmbiguousSchemaBinding,
                        endpoint.Location,
                        endpoint.InputType.ToDisplayString()));
                }

                continue;
            }

            result.Add(endpoint.InputType, model);
        }

        return result.Values
            .OrderBy(static model => TypeNames.Key(model.InputType), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static SchemaDefinition MergeParts(
        SchemaDefinition definition,
        Dictionary<ITypeSymbol, SchemaPartDefinition> partDefinitions,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (definition.Parts.Length == 0)
        {
            return definition;
        }

        var fields = ImmutableArray.CreateBuilder<SchemaFieldDeclaration>();
        fields.AddRange(definition.Fields);
        var directNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in definition.Fields)
        {
            directNames.Add(field.Property.Name);
        }

        var partNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in definition.Parts)
        {
            if (!partDefinitions.TryGetValue(part.PartType, out var partDefinition))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.UndeclaredSchemaPart,
                    part.Location,
                    part.PartType.ToDisplayString()));
                continue;
            }

            foreach (var field in partDefinition.Fields)
            {
                var name = field.Property.Name;
                if (directNames.Contains(name))
                {
                    continue;
                }

                if (!partNames.Add(name))
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticCatalog.AmbiguousSchemaPartMember,
                        part.Location,
                        name,
                        definition.InputType.ToDisplayString()));
                    continue;
                }

                var property = FindPublicInstanceProperty(definition.InputType, field.Property);
                if (property is null)
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticCatalog.ExplicitSchemaPartMember,
                        part.Location,
                        name,
                        definition.InputType.ToDisplayString()));
                    continue;
                }

                fields.Add(new SchemaFieldDeclaration(
                    property,
                    field.Source,
                    field.HeaderName,
                    field.Rules,
                    field.Location));
            }
        }

        return new SchemaDefinition(
            definition.InputType,
            fields.ToImmutable(),
            ImmutableArray<SchemaPartUse>.Empty,
            definition.Diagnostics,
            definition.Location);
    }

    private static IPropertySymbol? FindPublicInstanceProperty(
        ITypeSymbol inputType,
        IPropertySymbol partProperty)
    {
        if (!(inputType is INamedTypeSymbol namedType))
        {
            return null;
        }

        for (var current = namedType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(partProperty.Name))
            {
                if (member is IPropertySymbol property
                    && !property.IsStatic
                    && !property.IsIndexer
                    && property.DeclaredAccessibility == Accessibility.Public
                    && property.GetMethod?.DeclaredAccessibility == Accessibility.Public
                    && SymbolEqualityComparer.Default.Equals(property.Type, partProperty.Type))
                {
                    return property;
                }
            }
        }

        return null;
    }

    private static SchemaBindingModel? TryBuild(
        SchemaEndpointCall endpoint,
        SchemaDefinition? definition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (definition is not null)
        {
            foreach (var field in definition.Fields)
            {
                if (field.Source == SchemaFieldSource.Form
                    && IsFormFile(field.Property.Type))
                {
                    diagnostics.Add(Diagnostic.Create(
                        DiagnosticCatalog.UnsupportedSchemaFieldType,
                        field.Location,
                        field.Property.Name,
                        "form",
                        "file uploads are not supported by form field binding"));
                    return null;
                }
            }
        }

        if (!JsonTypeGraphBuilder.TryBuild(endpoint.InputType, out var graph, out var error))
        {
            diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.InvalidSchemaDefinition,
                endpoint.Location,
                endpoint.InputType.ToDisplayString(),
                error));
            return null;
        }

        var inputModel = graph!.Models.FirstOrDefault(model =>
            SymbolEqualityComparer.Default.Equals(model.Type, endpoint.InputType));
        if (inputModel is null || inputModel.Kind != JsonTypeKind.Object)
        {
            diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.InvalidSchemaDefinition,
                endpoint.Location,
                endpoint.InputType.ToDisplayString(),
                "input types must be records, classes, or structs with constructible properties"));
            return null;
        }

        var declarations = new Dictionary<IPropertySymbol, SchemaFieldDeclaration>(SymbolEqualityComparer.Default);
        if (definition is not null)
        {
            foreach (var field in definition.Fields)
            {
                declarations[field.Property] = field;
            }
        }

        var routeNames = new HashSet<string>(endpoint.Template.ParameterNames, StringComparer.Ordinal);
        var bodyByDefault = endpoint.Method is "POST" or "PUT" or "PATCH";
        var fields = ImmutableArray.CreateBuilder<SchemaBoundField>(inputModel.Properties.Length);
        var valid = true;
        var orderedProperties = ImmutableArray.CreateBuilder<JsonPropertyModel>(inputModel.Properties.Length);
        if (definition is not null)
        {
            foreach (var declaration in definition.Fields)
            {
                var declaredProperty = inputModel.Properties.FirstOrDefault(propertyModel =>
                    SymbolEqualityComparer.Default.Equals(propertyModel.Property, declaration.Property));
                if (declaredProperty is not null)
                {
                    orderedProperties.Add(declaredProperty);
                }
            }
        }

        foreach (var propertyModel in inputModel.Properties)
        {
            if (!orderedProperties.Any(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate.Property, propertyModel.Property)))
            {
                orderedProperties.Add(propertyModel);
            }
        }

        foreach (var propertyModel in orderedProperties)
        {
            var property = propertyModel.Property;
            declarations.TryGetValue(property, out var declaration);
            var source = declaration?.Source
                ?? (routeNames.Contains(property.Name)
                    ? SchemaFieldSource.Route
                    : bodyByDefault ? SchemaFieldSource.Body : SchemaFieldSource.Query);
            var location = declaration?.Location ?? endpoint.Location;
            var rules = declaration?.Rules ?? ImmutableArray<SchemaRuleDeclaration>.Empty;
            var headerName = declaration?.HeaderName;

            if (source == SchemaFieldSource.Route && !routeNames.Contains(property.Name))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.SchemaRouteFieldMissing,
                    location,
                    property.Name,
                    endpoint.Pattern));
                valid = false;
            }

            if (source != SchemaFieldSource.Body && !IsTextParsable(property.Type, out var typeError))
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.UnsupportedSchemaFieldType,
                    location,
                    property.Name,
                    source.ToString().ToLowerInvariant(),
                    typeError));
                valid = false;
            }

            valid &= ValidateRules(property, rules, diagnostics);
            fields.Add(new SchemaBoundField(property, source, headerName, rules, location));
        }

        var hasForm = fields.Any(static field => field.Source == SchemaFieldSource.Form);
        var hasBody = fields.Any(static field => field.Source == SchemaFieldSource.Body);
        if (hasForm && hasBody)
        {
            var formField = fields.First(static field => field.Source == SchemaFieldSource.Form);
            diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.FormBodyConflict,
                formField.Location,
                endpoint.InputType.ToDisplayString()));
            valid = false;
        }

        foreach (var routeName in endpoint.Template.ParameterNames)
        {
            var field = fields.FirstOrDefault(candidate =>
                candidate.Property.Name == routeName && candidate.Source == SchemaFieldSource.Route);
            if (field is null)
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.SchemaRouteParameterMissing,
                    endpoint.Location,
                    routeName,
                    endpoint.Pattern,
                    endpoint.InputType.ToDisplayString()));
                valid = false;
            }
        }

        return valid
            ? new SchemaBindingModel(endpoint.InputType, inputModel, fields.ToImmutable())
            : null;
    }

    private static bool ValidateRules(
        IPropertySymbol property,
        ImmutableArray<SchemaRuleDeclaration> rules,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var type = UnwrapNullable(property.Type);
        var valid = true;
        foreach (var rule in rules)
        {
            string? error = null;
            if (rule.Kind is SchemaRuleKind.Min or SchemaRuleKind.Max or SchemaRuleKind.Range
                or SchemaRuleKind.Positive or SchemaRuleKind.NonNegative)
            {
                if (!IsNumeric(type))
                {
                    error = "numeric rules require a numeric field";
                }
            }
            else if (rule.Kind is SchemaRuleKind.NotEmpty or SchemaRuleKind.Length
                     or SchemaRuleKind.MinLength or SchemaRuleKind.MaxLength or SchemaRuleKind.Pattern)
            {
                if (type.SpecialType != SpecialType.System_String)
                {
                    error = "string rules require a string field";
                }
            }

            if (error is null && rule.Kind == SchemaRuleKind.Pattern)
            {
                try
                {
                    _ = new Regex((string)rule.Values[0]!);
                }
                catch (ArgumentException)
                {
                    error = "the regular expression is invalid";
                }
            }

            if (error is null && rule.Kind == SchemaRuleKind.Length
                && ((int)rule.Values[0]! < 0 || (int)rule.Values[1]! < (int)rule.Values[0]!))
            {
                error = "Length requires a non-negative minimum no greater than the maximum";
            }

            if (error is null && rule.Kind is SchemaRuleKind.MinLength or SchemaRuleKind.MaxLength
                && (int)rule.Values[0]! < 0)
            {
                error = rule.Kind + " requires a non-negative length";
            }

            if (error is null && rule.Kind == SchemaRuleKind.Range
                && rule.Values[0] is IComparable minimum
                && minimum.CompareTo(rule.Values[1]) > 0)
            {
                error = "Range requires a minimum no greater than the maximum";
            }

            if (error is not null)
            {
                diagnostics.Add(Diagnostic.Create(
                    DiagnosticCatalog.InvalidSchemaRule,
                    rule.Location,
                    rule.Kind.ToString(),
                    property.Name,
                    error));
                valid = false;
            }
        }

        return valid;
    }

    internal static bool IsTextParsable(ITypeSymbol type, out string? error)
    {
        var underlying = UnwrapNullable(type);
        if (underlying.TypeKind == TypeKind.Enum
            || underlying.SpecialType is SpecialType.System_Boolean
                or SpecialType.System_Byte
                or SpecialType.System_SByte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Single
                or SpecialType.System_Double
                or SpecialType.System_Decimal
                or SpecialType.System_Char
                or SpecialType.System_String)
        {
            error = null;
            return true;
        }

        if (underlying is INamedTypeSymbol named)
        {
            var metadataName = InvocationAnalyzer.GetMetadataName(named.OriginalDefinition);
            if (metadataName is "System.Guid" or "System.DateTime" or "System.DateTimeOffset")
            {
                error = null;
                return true;
            }
        }

        error = "supported text values are primitives, string, Guid, enum, DateTime, and DateTimeOffset";
        return false;
    }

    private static bool IsFormFile(ITypeSymbol type)
    {
        var underlying = UnwrapNullable(type);
        return underlying is INamedTypeSymbol named
            && InvocationAnalyzer.GetMetadataName(named.OriginalDefinition) == "Mugi.FormFile";
    }

    internal static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && InvocationAnalyzer.GetMetadataName(named.OriginalDefinition) == "System.Nullable`1")
        {
            return named.TypeArguments[0];
        }

        return type;
    }

    internal static bool IsNullable(ITypeSymbol type) =>
        type.IsReferenceType
        || (type is INamedTypeSymbol named
            && InvocationAnalyzer.GetMetadataName(named.OriginalDefinition) == "System.Nullable`1");

    internal static bool IsNumeric(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16
        or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Single or SpecialType.System_Double
        or SpecialType.System_Decimal;
}

internal sealed class SchemaSourceEmitter
{
    private readonly SchemaBindingModel _model;
    private readonly GeneratorSettings _settings;
    private readonly ImmutableArray<string> _patterns;

    internal SchemaSourceEmitter(SchemaBindingModel model, GeneratorSettings settings)
    {
        _model = model;
        _settings = settings;
        _patterns = model.Fields
            .SelectMany(static field => field.Rules)
            .Where(static rule => rule.Kind == SchemaRuleKind.Pattern)
            .Select(static rule => (string)rule.Values[0]!)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
    }

    internal string Emit()
    {
        var writer = new CodeWriter();
        writer.Line("// <auto-generated/>");
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace Mugi.Generated;");
        writer.Line();
        var binderName = BinderName(_model.InputType);
        writer.Open(
            "internal sealed class " + binderName +
            " : global::Mugi.Schema.IInputBinder<" + TypeNames.NonNullableDisplay(_model.InputType) + ">");
        writer.Line("internal static readonly " + binderName + " Instance = new " + binderName + "();");
        for (var index = 0; index < _patterns.Length; index++)
        {
            writer.Line(
                "private static readonly global::System.Text.RegularExpressions.Regex Pattern" + index +
                " = global::Mugi.Schema.SchemaRegex.Create(" + GeneratedNaming.Literal(_patterns[index]) + ");");
        }

        writer.Line();
        EmitBind(writer);
        writer.Line();
        EmitErrorResponse(writer);

        if (_model.Fields.Any(static field => field.Source == SchemaFieldSource.Body))
        {
            writer.Line();
            EmitBodyValues(writer);
            writer.Line();
            EmitBodyCodec(writer);
        }

        writer.Close();
        writer.Line();
        EmitRegistration(writer, binderName);
        return writer.ToString();
    }

    internal static string BinderName(ITypeSymbol type) =>
        GeneratedNaming.StableIdentifier("Binder_", TypeNames.Key(type));

    private void EmitBind(CodeWriter writer)
    {
        var inputName = TypeNames.NonNullableDisplay(_model.InputType);
        writer.Open(
            "public async global::System.Threading.Tasks.ValueTask<global::Mugi.Schema.BindResult<" +
            inputName + ">> Bind(global::Mugi.Context context)");
        writer.Line("var errors = new global::System.Collections.Generic.List<global::Mugi.Schema.ValidationError>();");
        for (var index = 0; index < _model.Fields.Length; index++)
        {
            writer.Line(TypeNames.Display(_model.Fields[index].Property.Type) + " value" + index + " = default!;");
            writer.Line("var hasValue" + index + " = false;");
            writer.Line("var valid" + index + " = true;");
        }

        writer.Line();
        if (_model.Fields.Any(static field => field.Source == SchemaFieldSource.Body))
        {
            writer.Line("BodyValues? body;");
            writer.Open("try");
            writer.Line("body = await context.Req.Json<BodyValues>().ConfigureAwait(false);");
            writer.Close();
            writer.Open("catch (global::Mugi.Json.JsonException exception) when (exception.IsInputError)");
            writer.Line("errors.Add(new global::Mugi.Schema.ValidationError(\"body\", \"invalid JSON body\"));");
            writer.Line("await WriteErrors(context, errors).ConfigureAwait(false);");
            writer.Line(
                "return global::Mugi.Schema.BindResult<" + inputName + ">.Invalid(errors);");
            writer.Close();
            writer.Open("if (body is not null)");
            for (var index = 0; index < _model.Fields.Length; index++)
            {
                if (_model.Fields[index].Source != SchemaFieldSource.Body)
                {
                    continue;
                }

                writer.Line("value" + index + " = body.Value" + index + ";");
                writer.Line("hasValue" + index + " = body.HasValue" + index + ";");
            }

            writer.Close();
        }
        else if (_model.Fields.Any(static field => field.Source == SchemaFieldSource.Form))
        {
            EmitFormRead(writer, inputName);
        }
        else
        {
            writer.Line("await global::System.Threading.Tasks.ValueTask.CompletedTask.ConfigureAwait(false);");
        }

        for (var index = 0; index < _model.Fields.Length; index++)
        {
            var field = _model.Fields[index];
            if (field.Source == SchemaFieldSource.Body)
            {
                continue;
            }

            writer.Line();
            EmitTextRead(writer, field, index);
        }

        for (var index = 0; index < _model.Fields.Length; index++)
        {
            writer.Line();
            EmitMissingAndRules(writer, _model.Fields[index], index);
        }

        writer.Line();
        for (var index = 0; index < _model.Fields.Length; index++)
        {
            writer.Line("_ = valid" + index + ";");
        }

        writer.Open("if (errors.Count != 0)");
        writer.Line("await WriteErrors(context, errors).ConfigureAwait(false);");
        writer.Line(
            "return global::Mugi.Schema.BindResult<" + inputName + ">.Invalid(errors);");
        writer.Close();
        writer.Line();
        writer.Line("var value = " + Construction() + ";");
        writer.Line("return global::Mugi.Schema.BindResult<" + inputName + ">.Valid(value);");
        writer.Close();
    }

    private static void EmitErrorResponse(CodeWriter writer)
    {
        writer.Open(
            "private static global::System.Threading.Tasks.ValueTask WriteErrors(" +
            "global::Mugi.Context context, " +
            "global::System.Collections.Generic.IReadOnlyList<global::Mugi.Schema.ValidationError> errors)");
        writer.Line("context.Status(400);");
        writer.Line("var buffer = new global::System.Buffers.ArrayBufferWriter<byte>();");
        writer.Line(
            "global::Mugi.Json.Json.Serialize(buffer, new ErrorResponse(errors), ErrorResponseCodec.Instance);");
        writer.Line("return context.Bytes(buffer.WrittenMemory, \"application/json\");");
        writer.Close();
        writer.Line();
        writer.Line(
            "private sealed record ErrorResponse(" +
            "global::System.Collections.Generic.IReadOnlyList<global::Mugi.Schema.ValidationError> Errors);");
        writer.Line();
        writer.Open(
            "private sealed class ErrorResponseCodec : " +
            "global::Mugi.Json.IJsonCodec<ErrorResponse>");
        writer.Line("internal static readonly ErrorResponseCodec Instance = new ErrorResponseCodec();");
        writer.Line();
        writer.Open(
            "public void Write(ref global::Mugi.Json.JsonWriter writer, ErrorResponse? value)");
        writer.Line("global::System.ArgumentNullException.ThrowIfNull(value);");
        writer.Line("writer.EnterContainer(1);");
        writer.Line("writer.WriteRaw(\"{\\\"errors\\\":[\"u8);");
        writer.Line("writer.EnterContainer(value.Errors.Count);");
        writer.Open("for (var index = 0; index < value.Errors.Count; index++)");
        writer.Open("if (index != 0)");
        writer.Line("writer.WriteRaw(\",\"u8);");
        writer.Close();
        writer.Line("var error = value.Errors[index];");
        writer.Line("writer.EnterContainer(2);");
        writer.Line("writer.WriteRaw(\"{\\\"field\\\":\"u8);");
        writer.Line("writer.WriteString(error.Field);");
        writer.Line("writer.WriteRaw(\",\\\"message\\\":\"u8);");
        writer.Line("writer.WriteString(error.Message);");
        writer.Line("writer.WriteRaw(\"}\"u8);");
        writer.Line("writer.ExitContainer();");
        writer.Close();
        writer.Line("writer.ExitContainer();");
        writer.Line("writer.WriteRaw(\"]}\"u8);");
        writer.Line("writer.ExitContainer();");
        writer.Close();
        writer.Line();
        writer.Open(
            "public ErrorResponse? Read(ref global::Mugi.Json.JsonReader reader)");
        writer.Line(
            "throw new global::System.NotSupportedException(" +
            "\"Validation error responses are write-only.\");");
        writer.Close();
        writer.Close();
    }

    private static void EmitFormRead(CodeWriter writer, string inputName)
    {
        writer.Line("global::Mugi.FormData form;");
        writer.Open("try");
        writer.Line("form = await context.Req.Form().ConfigureAwait(false);");
        writer.Close();
        writer.Open("catch (global::Mugi.FormException exception) when (exception.IsInputError)");
        writer.Line(
            "errors.Add(new global::Mugi.Schema.ValidationError(\"\", exception.Message));");
        writer.Line("await WriteErrors(context, errors).ConfigureAwait(false);");
        writer.Line(
            "return global::Mugi.Schema.BindResult<" + inputName + ">.Invalid(errors);");
        writer.Close();
    }

    private void EmitTextRead(CodeWriter writer, SchemaBoundField field, int index)
    {
        string read;
        switch (field.Source)
        {
            case SchemaFieldSource.Route:
                read = "context.Param(" + GeneratedNaming.Literal(field.Property.Name) + ")";
                break;
            case SchemaFieldSource.Query:
                read = "context.Query(" + GeneratedNaming.Literal(field.Property.Name) + ")";
                break;
            case SchemaFieldSource.Header:
                read = "context.Req.Header(" + GeneratedNaming.Literal(field.HeaderName ?? field.Property.Name) + ")";
                break;
            case SchemaFieldSource.Form:
                read = "form.Get(" + GeneratedNaming.Literal(field.Property.Name) + ")";
                break;
            default:
                throw new InvalidOperationException("Unknown text field source.");
        }

        writer.Line("var raw" + index + " = " + read + ";");
        writer.Open("if (raw" + index + " is not null)");
        writer.Line("hasValue" + index + " = true;");
        EmitParse(writer, field, index);
        writer.Close();
    }

    private void EmitParse(CodeWriter writer, SchemaBoundField field, int index)
    {
        var type = field.Property.Type;
        var underlying = SchemaBindingBuilder.UnwrapNullable(type);
        var target = TypeNames.Display(underlying);
        if (underlying.SpecialType == SpecialType.System_String)
        {
            writer.Line("value" + index + " = raw" + index + ";");
            return;
        }

        if (underlying.SpecialType == SpecialType.System_Char)
        {
            writer.Open("if (raw" + index + ".Length == 1)");
            writer.Line("value" + index + " = raw" + index + "[0];");
            writer.Close();
            EmitParseFailure(writer, field, index);
            return;
        }

        var temporary = "parsed" + index;
        string parse;
        if (underlying.TypeKind == TypeKind.Enum)
        {
            parse = "global::System.Enum.TryParse<" + target + ">(raw" + index + ", false, out var " + temporary + ")";
        }
        else
        {
            var metadataName = underlying is INamedTypeSymbol named
                ? InvocationAnalyzer.GetMetadataName(named.OriginalDefinition)
                : string.Empty;
            if (metadataName == "System.Guid")
            {
                parse = "global::System.Guid.TryParse(raw" + index + ", out var " + temporary + ")";
            }
            else if (metadataName == "System.DateTime")
            {
                parse = "global::Mugi.Schema.SchemaText.TryParseDateTime(raw" + index + ", out var " + temporary + ")";
            }
            else if (metadataName == "System.DateTimeOffset")
            {
                parse = "global::Mugi.Schema.SchemaText.TryParseDateTimeOffset(raw" + index + ", out var " + temporary + ")";
            }
            else if (underlying.SpecialType == SpecialType.System_Boolean)
            {
                parse = "global::System.Boolean.TryParse(raw" + index + ", out var " + temporary + ")";
            }
            else if (underlying.SpecialType is SpecialType.System_Single or SpecialType.System_Double)
            {
                parse = "global::Mugi.Schema.SchemaText.TryParseFloatingPoint<" + target + ">(raw" + index + ", out var " + temporary + ")";
            }
            else if (underlying.SpecialType == SpecialType.System_Decimal)
            {
                parse = "global::Mugi.Schema.SchemaText.TryParseDecimal(raw" + index + ", out var " + temporary + ")";
            }
            else
            {
                parse = "global::Mugi.Schema.SchemaText.TryParseInteger<" + target + ">(raw" + index + ", out var " + temporary + ")";
            }
        }

        writer.Open("if (" + parse + ")");
        writer.Line("value" + index + " = " + temporary + ";");
        writer.Close();
        EmitParseFailure(writer, field, index);
    }

    private void EmitParseFailure(CodeWriter writer, SchemaBoundField field, int index)
    {
        writer.Open("else");
        writer.Line("valid" + index + " = false;");
        writer.Line(
            "errors.Add(new global::Mugi.Schema.ValidationError(" +
            GeneratedNaming.Literal(GeneratedNaming.JsonPropertyName(field.Property.Name, _settings.Naming)) +
            ", \"has an invalid value\"));");
        writer.Close();
    }

    private void EmitMissingAndRules(CodeWriter writer, SchemaBoundField field, int index)
    {
        var optional = field.Rules.Any(static rule => rule.Kind == SchemaRuleKind.Optional);
        var defaultRule = field.Rules.LastOrDefault(static rule => rule.Kind == SchemaRuleKind.Default);
        writer.Open("if (!hasValue" + index + ")");
        if (defaultRule is not null && defaultRule.Values.Length != 0)
        {
            writer.Line("value" + index + " = " + FormatConstant(field.Property.Type, defaultRule.Values[0]) + ";");
            writer.Line("hasValue" + index + " = true;");
        }
        else if (!optional)
        {
            writer.Line("valid" + index + " = false;");
            AddError(writer, field, "is required");
        }

        writer.Close();

        if (SchemaBindingBuilder.IsNullable(field.Property.Type))
        {
            writer.Open("if (hasValue" + index + " && value" + index + " is null)");
            if (!optional)
            {
                writer.Line("valid" + index + " = false;");
                AddError(writer, field, "is required");
            }

            writer.Close();
        }

        var condition = "valid" + index + " && hasValue" + index;
        if (SchemaBindingBuilder.IsNullable(field.Property.Type))
        {
            condition += " && value" + index + " is not null";
        }

        foreach (var rule in field.Rules)
        {
            if (rule.Kind is SchemaRuleKind.Optional or SchemaRuleKind.Default)
            {
                continue;
            }

            EmitRule(writer, field, rule, index, condition);
        }
    }

    private void EmitRule(
        CodeWriter writer,
        SchemaBoundField field,
        SchemaRuleDeclaration rule,
        int index,
        string condition)
    {
        var value = SchemaBindingBuilder.IsNullable(field.Property.Type)
            && !field.Property.Type.IsReferenceType
                ? "value" + index + ".Value"
                : "value" + index;
        string test;
        string message;
        switch (rule.Kind)
        {
            case SchemaRuleKind.Min:
                test = value + " < " + FormatConstant(field.Property.Type, rule.Values[0]);
                message = "must be at least " + DisplayConstant(rule.Values[0]);
                break;
            case SchemaRuleKind.Max:
                test = value + " > " + FormatConstant(field.Property.Type, rule.Values[0]);
                message = "must be at most " + DisplayConstant(rule.Values[0]);
                break;
            case SchemaRuleKind.Range:
                test = value + " < " + FormatConstant(field.Property.Type, rule.Values[0]) +
                    " || " + value + " > " + FormatConstant(field.Property.Type, rule.Values[1]);
                message = "must be between " + DisplayConstant(rule.Values[0]) + " and " + DisplayConstant(rule.Values[1]);
                break;
            case SchemaRuleKind.Positive:
                test = value + " <= 0";
                message = "must be positive";
                break;
            case SchemaRuleKind.NonNegative:
                test = value + " < 0";
                message = "must be non-negative";
                break;
            case SchemaRuleKind.NotEmpty:
                test = value + ".Length == 0";
                message = "must not be empty";
                break;
            case SchemaRuleKind.Length:
                test = value + ".Length < " + Convert.ToString(rule.Values[0], CultureInfo.InvariantCulture) +
                    " || " + value + ".Length > " + Convert.ToString(rule.Values[1], CultureInfo.InvariantCulture);
                message = "length must be between " + DisplayConstant(rule.Values[0]) + " and " + DisplayConstant(rule.Values[1]);
                break;
            case SchemaRuleKind.MinLength:
                test = value + ".Length < " + Convert.ToString(rule.Values[0], CultureInfo.InvariantCulture);
                message = "length must be at least " + DisplayConstant(rule.Values[0]);
                break;
            case SchemaRuleKind.MaxLength:
                test = value + ".Length > " + Convert.ToString(rule.Values[0], CultureInfo.InvariantCulture);
                message = "length must be at most " + DisplayConstant(rule.Values[0]);
                break;
            case SchemaRuleKind.Pattern:
                test = "!global::Mugi.Schema.SchemaRegex.IsMatch(" +
                    PatternField((string)rule.Values[0]!) + ", " + value + ")";
                message = "has an invalid format";
                break;
            case SchemaRuleKind.Must:
                test = "!((global::System.Func<" + TypeNames.Display(field.Property.Type) + ", bool>)(" +
                    rule.Predicate + "))(value" + index + ")";
                message = rule.Message!;
                break;
            default:
                return;
        }

        writer.Open("if (" + condition + " && (" + test + "))");
        AddError(writer, field, message);
        writer.Close();
    }

    private void AddError(CodeWriter writer, SchemaBoundField field, string message)
    {
        writer.Line(
            "errors.Add(new global::Mugi.Schema.ValidationError(" +
            GeneratedNaming.Literal(GeneratedNaming.JsonPropertyName(field.Property.Name, _settings.Naming)) + ", " +
            GeneratedNaming.Literal(message) + "));");
    }

    private string PatternField(string pattern)
    {
        for (var index = 0; index < _patterns.Length; index++)
        {
            if (string.Equals(_patterns[index], pattern, StringComparison.Ordinal))
            {
                return "Pattern" + index;
            }
        }

        throw new InvalidOperationException("The schema pattern was not registered.");
    }

    private string Construction()
    {
        var primaryIndexes = new List<int>();
        foreach (var primary in _model.InputModel.PrimaryProperties)
        {
            for (var index = 0; index < _model.Fields.Length; index++)
            {
                if (SymbolEqualityComparer.Default.Equals(primary.Property, _model.Fields[index].Property))
                {
                    primaryIndexes.Add(index);
                    break;
                }
            }
        }

        var result = "new " + TypeNames.NonNullableDisplay(_model.InputType) + "(" +
            string.Join(", ", primaryIndexes.Select(ValueExpression)) + ")";
        var remaining = Enumerable.Range(0, _model.Fields.Length)
            .Where(index => !_model.InputModel.PrimaryProperties.Any(primary =>
                SymbolEqualityComparer.Default.Equals(
                    primary.Property,
                    _model.Fields[index].Property)))
            .ToList();
        if (remaining.Count == 0)
        {
            return result;
        }

        return result + " { " + string.Join(", ", remaining.Select(index =>
            GeneratedNaming.Identifier(_model.Fields[index].Property.Name) + " = " + ValueExpression(index))) + " }";
    }

    private string ValueExpression(int index)
    {
        var property = _model.Fields[index].Property;
        return "value" + index +
            (property.Type.IsReferenceType
             && property.NullableAnnotation == NullableAnnotation.NotAnnotated ? "!" : string.Empty);
    }

    private void EmitBodyValues(CodeWriter writer)
    {
        writer.Open("internal sealed class BodyValues");
        for (var index = 0; index < _model.Fields.Length; index++)
        {
            if (_model.Fields[index].Source != SchemaFieldSource.Body)
            {
                continue;
            }

            writer.Line("internal " + TypeNames.Display(_model.Fields[index].Property.Type) + " Value" + index + " = default!;");
            writer.Line("internal bool HasValue" + index + ";");
        }

        writer.Close();
    }

    private void EmitBodyCodec(CodeWriter writer)
    {
        writer.Open("internal sealed class BodyValuesCodec : global::Mugi.Json.IJsonCodec<BodyValues>");
        writer.Line("internal static readonly BodyValuesCodec Instance = new BodyValuesCodec();");
        writer.Line();
        writer.Open("public void Write(ref global::Mugi.Json.JsonWriter writer, BodyValues? value)");
        writer.Line("throw new global::System.NotSupportedException(\"Schema body values are read-only.\");");
        writer.Close();
        writer.Line();
        writer.Open("public BodyValues? Read(ref global::Mugi.Json.JsonReader reader)");
        writer.Open("if (reader.TryReadNull())");
        writer.Line("return null;");
        writer.Close();
        writer.Line("var result = new BodyValues();");
        writer.Line("reader.ReadBeginObject();");
        writer.Open("while (!reader.TryReadEndObject())");
        writer.Line("var propertyName = reader.ReadPropertyName();");

        var bodyFields = _model.Fields
            .Select((field, index) => new
            {
                Field = field,
                Index = index,
                Name = GeneratedNaming.JsonPropertyName(field.Property.Name, _settings.Naming),
            })
            .Where(static item => item.Field.Source == SchemaFieldSource.Body)
            .ToList();
        var first = true;
        foreach (var item in bodyFields)
        {
            writer.Open((first ? "if" : "else if") +
                " (global::System.MemoryExtensions.SequenceEqual(propertyName, " +
                GeneratedNaming.Utf8Literal(item.Name) + "))");
            writer.Line(
                "result.Value" + item.Index + " = global::Mugi.Json.Json.GetCodec<" +
                TypeNames.NonNullableDisplay(item.Field.Property.Type) + ">().Read(ref reader)!;");
            writer.Line("result.HasValue" + item.Index + " = true;");
            writer.Close();
            first = false;
        }

        writer.Open("else");
        writer.Line("reader.SkipValue();");
        writer.Close();
        writer.Close();
        writer.Line("return result;");
        writer.Close();
        writer.Close();
    }

    private void EmitRegistration(CodeWriter writer, string binderName)
    {
        writer.Open("internal static class SchemaGeneratedRegistration_" + binderName);
        writer.Line("[global::System.Runtime.CompilerServices.ModuleInitializer]");
        writer.Open("internal static void Initialize()");
        writer.Line(
            "global::Mugi.Schema.BinderRegistry<" + TypeNames.NonNullableDisplay(_model.InputType) +
            ">.Register(" + binderName + ".Instance);");
        if (_model.Fields.Any(static field => field.Source == SchemaFieldSource.Body))
        {
            writer.Line(
                "global::Mugi.Json.Json.Register<" + binderName + ".BodyValues>(" +
                binderName + ".BodyValuesCodec.Instance);");
        }

        writer.Close();
        writer.Close();
    }

    private static string FormatConstant(ITypeSymbol type, object? value)
    {
        var target = SchemaBindingBuilder.UnwrapNullable(type);
        if (value is null)
        {
            return "default";
        }

        if (target.TypeKind == TypeKind.Enum)
        {
            return "(" + TypeNames.Display(target) + ")" + Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        switch (value)
        {
            case string text:
                return GeneratedNaming.Literal(text);
            case char character:
                return SymbolDisplay.FormatLiteral(character, quote: true);
            case bool boolean:
                return boolean ? "true" : "false";
            case float single:
                return single.ToString("R", CultureInfo.InvariantCulture) + "F";
            case double number:
                return number.ToString("R", CultureInfo.InvariantCulture) + "D";
            case decimal decimalValue:
                return decimalValue.ToString(CultureInfo.InvariantCulture) + "M";
            case long longValue:
                return longValue.ToString(CultureInfo.InvariantCulture) + "L";
            case ulong ulongValue:
                return ulongValue.ToString(CultureInfo.InvariantCulture) + "UL";
            case uint uintValue:
                return uintValue.ToString(CultureInfo.InvariantCulture) + "U";
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default";
        }
    }

    private static string DisplayConstant(object? value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
}
