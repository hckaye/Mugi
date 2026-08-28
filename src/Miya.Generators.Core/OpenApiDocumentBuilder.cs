using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Miya.Generators.Core;

public sealed class OpenApiSettings
{
    public OpenApiSettings(
        string? title = null,
        string? version = null,
        JsonNaming naming = JsonNaming.CamelCase)
    {
        Title = title;
        Version = version;
        Naming = naming;
    }

    public string? Title { get; }

    public string? Version { get; }

    public JsonNaming Naming { get; }
}

public static class OpenApiDocumentBuilder
{
    public static string Build(
        Compilation compilation,
        OpenApiSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        settings ??= new OpenApiSettings();
        var analyses = GeneratorCore.AnalyzeCompilation(
            compilation,
            includeInterceptLocations: false,
            cancellationToken);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var bindings = SchemaBindingBuilder.Build(analyses, diagnostics);
        return new Builder(compilation, analyses, bindings, settings, cancellationToken).Build();
    }

    private sealed class Builder
    {
        private static readonly string[] AllMethods =
        {
            "delete", "get", "head", "options", "patch", "post", "put", "trace",
        };

        private readonly Compilation _compilation;
        private readonly ImmutableArray<InvocationAnalysis> _analyses;
        private readonly Dictionary<ITypeSymbol, SchemaBindingModel> _bindings;
        private readonly OpenApiSettings _settings;
        private readonly CancellationToken _cancellationToken;
        private readonly SchemaRegistry _schemas;
        private bool _usesValidationErrors;

        internal Builder(
            Compilation compilation,
            ImmutableArray<InvocationAnalysis> analyses,
            ImmutableArray<SchemaBindingModel> bindings,
            OpenApiSettings settings,
            CancellationToken cancellationToken)
        {
            _compilation = compilation;
            _analyses = analyses;
            _bindings = new Dictionary<ITypeSymbol, SchemaBindingModel>(SymbolEqualityComparer.Default);
            foreach (var binding in bindings)
            {
                _bindings[binding.InputType] = binding;
            }

            _settings = settings;
            _cancellationToken = cancellationToken;
            _schemas = new SchemaRegistry(settings.Naming);
        }

        internal string Build()
        {
            var paths = BuildPaths();
            var document = new ObjectNode();
            document.Add("openapi", "3.1.0");
            document.Add("info", BuildInfo());
            document.Add("paths", paths);
            document.Add("components", BuildComponents());
            return JsonWriter.Write(document);
        }

        private ObjectNode BuildInfo()
        {
            var info = new ObjectNode();
            info.Add("title", ResolveTitle());
            info.Add("version", ResolveVersion());
            return info;
        }

        private string ResolveTitle()
        {
            if (!string.IsNullOrWhiteSpace(_settings.Title))
            {
                return _settings.Title!;
            }

            return string.IsNullOrWhiteSpace(_compilation.AssemblyName)
                ? "Application"
                : _compilation.AssemblyName!;
        }

        private string ResolveVersion()
        {
            if (!string.IsNullOrWhiteSpace(_settings.Version))
            {
                return _settings.Version!;
            }

            foreach (var attribute in _compilation.Assembly.GetAttributes())
            {
                var attributeName = attribute.AttributeClass is null
                    ? string.Empty
                    : InvocationAnalyzer.GetMetadataName(attribute.AttributeClass);
                if (attributeName is not ("System.Reflection.AssemblyInformationalVersionAttribute"
                    or "System.Reflection.AssemblyFileVersionAttribute")
                    || attribute.ConstructorArguments.Length != 1
                    || !(attribute.ConstructorArguments[0].Value is string version)
                    || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                return version;
            }

            var identityVersion = _compilation.Assembly.Identity.Version;
            if (identityVersion is not null && identityVersion != new Version(0, 0, 0, 0))
            {
                if (identityVersion.Revision != 0)
                {
                    return identityVersion.ToString(4);
                }

                return identityVersion.ToString(3);
            }

            return "0.1.0";
        }

        private ObjectNode BuildPaths()
        {
            var pathsByName = new SortedDictionary<string, ObjectNode>(StringComparer.Ordinal);
            foreach (var analysis in _analyses
                         .Where(static item => item.Route is not null || item.SchemaEndpoint is not null)
                         .OrderBy(static item => item.Syntax.SyntaxTree.FilePath, StringComparer.Ordinal)
                         .ThenBy(static item => item.Syntax.SpanStart))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (analysis.SchemaEndpoint is not null)
                {
                    _bindings.TryGetValue(analysis.SchemaEndpoint.InputType, out var binding);
                    AddEndpoint(
                        pathsByName,
                        analysis.SchemaEndpoint.Pattern,
                        analysis.SchemaEndpoint.Method,
                        analysis.SchemaEndpoint.Template,
                        binding,
                        analysis.Syntax,
                        hasValidation: true);
                }
                else
                {
                    var route = analysis.Route!;
                    AddEndpoint(
                        pathsByName,
                        route.Pattern,
                        route.Method,
                        route.Template,
                        binding: null,
                        analysis.Syntax,
                        hasValidation: false);
                }
            }

            var paths = new ObjectNode();
            foreach (var path in pathsByName)
            {
                paths.Add(path.Key, path.Value);
            }

            return paths;
        }

        private void AddEndpoint(
            SortedDictionary<string, ObjectNode> paths,
            string pattern,
            string method,
            RoutePatternSpec template,
            SchemaBindingModel? binding,
            InvocationExpressionSyntax registration,
            bool hasValidation)
        {
            var openApiPath = ConvertPath(template);
            if (!paths.TryGetValue(openApiPath, out var pathItem))
            {
                pathItem = new ObjectNode();
                paths.Add(openApiPath, pathItem);
            }

            var methods = method == "*"
                ? AllMethods
                : new[] { method.ToLowerInvariant() };
            foreach (var candidate in methods)
            {
                if (!IsOpenApiMethod(candidate))
                {
                    continue;
                }

                pathItem.Set(candidate, BuildOperation(template, binding, registration, candidate, hasValidation));
            }
        }

        private ObjectNode BuildOperation(
            RoutePatternSpec template,
            SchemaBindingModel? binding,
            InvocationExpressionSyntax registration,
            string method,
            bool hasValidation)
        {
            var operation = new ObjectNode();
            var parameters = BuildParameters(template, binding);
            if (parameters.Count != 0)
            {
                operation.Add("parameters", parameters);
            }

            if (binding is not null)
            {
                var requestBody = BuildRequestBody(binding);
                if (requestBody is not null)
                {
                    operation.Add("requestBody", requestBody);
                }
            }

            if (hasValidation)
            {
                _usesValidationErrors = true;
            }

            operation.Add("responses", BuildResponses(registration, method, hasValidation));
            return operation;
        }

        private ArrayNode BuildParameters(RoutePatternSpec template, SchemaBindingModel? binding)
        {
            var parameters = new ArrayNode();
            foreach (var parameterName in template.ParameterNames)
            {
                var field = binding?.Fields.FirstOrDefault(candidate =>
                    candidate.Source == SchemaFieldSource.Route
                    && string.Equals(candidate.Property.Name, parameterName, StringComparison.Ordinal));
                parameters.Add(BuildParameter(
                    parameterName,
                    "path",
                    required: true,
                    field));
            }

            if (binding is null)
            {
                return parameters;
            }

            foreach (var field in binding.Fields)
            {
                string? location = null;
                string? name = null;
                if (field.Source == SchemaFieldSource.Query)
                {
                    location = "query";
                    name = field.Property.Name;
                }
                else if (field.Source == SchemaFieldSource.Header)
                {
                    location = "header";
                    name = field.HeaderName ?? field.Property.Name;
                }

                if (location is null)
                {
                    continue;
                }

                parameters.Add(BuildParameter(
                    name!,
                    location,
                    IsRequired(field),
                    field));
            }

            return parameters;
        }

        private ObjectNode BuildParameter(
            string name,
            string location,
            bool required,
            SchemaBoundField? field)
        {
            var parameter = new ObjectNode();
            parameter.Add("name", name);
            parameter.Add("in", location);
            parameter.Add("required", required);
            var schema = field is null
                ? PrimitiveSchema("string")
                : _schemas.Create(field.Property.Type);
            if (field is not null)
            {
                ApplyRules(schema, field.Rules);
            }

            parameter.Add("schema", schema);
            return parameter;
        }

        private ObjectNode? BuildRequestBody(SchemaBindingModel binding)
        {
            var bodyFields = binding.Fields
                .Where(static field => field.Source == SchemaFieldSource.Body)
                .ToList();
            var formFields = binding.Fields
                .Where(static field => field.Source == SchemaFieldSource.Form)
                .ToList();
            if (bodyFields.Count == 0 && formFields.Count == 0)
            {
                return null;
            }

            var fields = bodyFields.Count != 0 ? bodyFields : formFields;
            var isForm = formFields.Count != 0;

            var properties = new ObjectNode();
            var required = new ArrayNode();
            foreach (var field in fields)
            {
                var name = isForm
                    ? field.Property.Name
                    : GeneratedNaming.JsonPropertyName(field.Property.Name, _settings.Naming);
                var schema = _schemas.Create(field.Property.Type);
                ApplyRules(schema, field.Rules);
                properties.Add(name, schema);
                if (IsRequired(field))
                {
                    required.Add(name);
                }
            }

            var bodySchema = new ObjectNode();
            bodySchema.Add("type", "object");
            bodySchema.Add("properties", properties);
            if (required.Count != 0)
            {
                bodySchema.Add("required", required);
            }

            var mediaType = new ObjectNode();
            mediaType.Add("schema", bodySchema);
            var content = new ObjectNode();
            content.Add(isForm ? "application/x-www-form-urlencoded" : "application/json", mediaType);
            var requestBody = new ObjectNode();
            requestBody.Add("required", true);
            requestBody.Add("content", content);
            return requestBody;
        }

        private ObjectNode BuildResponses(
            InvocationExpressionSyntax registration,
            string method,
            bool hasValidation)
        {
            var responseShape = AnalyzeResponse(registration);
            var ok = new ObjectNode();
            ok.Add("description", "OK");
            if (method != "head")
            {
                var content = new ObjectNode();
                if (responseShape.JsonType is not null)
                {
                    var mediaType = new ObjectNode();
                    mediaType.Add("schema", _schemas.Create(responseShape.JsonType));
                    content.Add("application/json", mediaType);
                }

                if (responseShape.HasText)
                {
                    var mediaType = new ObjectNode();
                    mediaType.Add("schema", PrimitiveSchema("string"));
                    content.Add("text/plain", mediaType);
                }

                if (content.Count != 0)
                {
                    ok.Add("content", content);
                }
            }

            var responses = new ObjectNode();
            responses.Add("200", ok);
            if (hasValidation)
            {
                var reference = new ObjectNode();
                reference.Add("$ref", "#/components/schemas/ValidationErrorResponse");
                var mediaType = new ObjectNode();
                mediaType.Add("schema", reference);
                var content = new ObjectNode();
                content.Add("application/json", mediaType);
                var badRequest = new ObjectNode();
                badRequest.Add("description", "Validation failed");
                badRequest.Add("content", content);
                responses.Add("400", badRequest);
            }

            return responses;
        }

        private ResponseShape AnalyzeResponse(InvocationExpressionSyntax registration)
        {
            var semanticModel = _compilation.GetSemanticModel(registration.SyntaxTree);
            if (!(semanticModel.GetOperation(registration, _cancellationToken) is IInvocationOperation operation))
            {
                return new ResponseShape(jsonType: null, hasText: false);
            }

            var handlerArgument = operation.Arguments.FirstOrDefault(static argument =>
                argument.Parameter?.Name == "handler");
            if (!(handlerArgument?.Syntax is ArgumentSyntax argumentSyntax)
                || !(argumentSyntax.Expression is AnonymousFunctionExpressionSyntax handler))
            {
                return new ResponseShape(jsonType: null, hasText: false);
            }

            ITypeSymbol? jsonType = null;
            var hasText = false;
            foreach (var invocation in handler.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                var method = semanticModel.GetSymbolInfo(invocation, _cancellationToken).Symbol as IMethodSymbol;
                if (method is null
                    || InvocationAnalyzer.GetMetadataName(method.OriginalDefinition.ContainingType) != "Miya.Context")
                {
                    continue;
                }

                if (jsonType is null && method.Name == "Json" && method.TypeArguments.Length == 1)
                {
                    jsonType = NormalizeTopLevelNullability(method.TypeArguments[0]);
                }
                else if (method.Name == "Text")
                {
                    hasText = true;
                }
            }

            return new ResponseShape(jsonType, hasText);
        }

        private ObjectNode BuildComponents()
        {
            var schemas = _schemas.BuildComponents();
            if (_usesValidationErrors)
            {
                schemas.Add("ValidationError", BuildValidationErrorSchema());
                schemas.Add("ValidationErrorResponse", BuildValidationErrorResponseSchema());
            }

            var components = new ObjectNode();
            components.Add("schemas", schemas);
            return components;
        }

        private static ObjectNode BuildValidationErrorSchema()
        {
            var properties = new ObjectNode();
            properties.Add("field", PrimitiveSchema("string"));
            properties.Add("message", PrimitiveSchema("string"));
            var required = new ArrayNode();
            required.Add("field");
            required.Add("message");
            var schema = new ObjectNode();
            schema.Add("type", "object");
            schema.Add("properties", properties);
            schema.Add("required", required);
            return schema;
        }

        private static ObjectNode BuildValidationErrorResponseSchema()
        {
            var itemReference = new ObjectNode();
            itemReference.Add("$ref", "#/components/schemas/ValidationError");
            var errors = new ObjectNode();
            errors.Add("type", "array");
            errors.Add("items", itemReference);
            var properties = new ObjectNode();
            properties.Add("errors", errors);
            var required = new ArrayNode();
            required.Add("errors");
            var schema = new ObjectNode();
            schema.Add("type", "object");
            schema.Add("properties", properties);
            schema.Add("required", required);
            return schema;
        }

        private static bool IsRequired(SchemaBoundField field) => !field.Rules.Any(static rule =>
            rule.Kind is SchemaRuleKind.Optional or SchemaRuleKind.Default);

        private static void ApplyRules(ObjectNode schema, ImmutableArray<SchemaRuleDeclaration> rules)
        {
            foreach (var rule in rules)
            {
                switch (rule.Kind)
                {
                    case SchemaRuleKind.Default when rule.Values.Length != 0:
                        schema.Set("default", rule.Values[0]);
                        break;
                    case SchemaRuleKind.Min when rule.Values.Length != 0:
                        schema.Set("minimum", rule.Values[0]);
                        break;
                    case SchemaRuleKind.Max when rule.Values.Length != 0:
                        schema.Set("maximum", rule.Values[0]);
                        break;
                    case SchemaRuleKind.Range when rule.Values.Length == 2:
                        schema.Set("minimum", rule.Values[0]);
                        schema.Set("maximum", rule.Values[1]);
                        break;
                    case SchemaRuleKind.Positive:
                        schema.Set("exclusiveMinimum", 0);
                        break;
                    case SchemaRuleKind.NonNegative:
                        schema.Set("minimum", 0);
                        break;
                    case SchemaRuleKind.NotEmpty:
                        SetMinimumLength(schema, 1);
                        break;
                    case SchemaRuleKind.Length when rule.Values.Length == 2:
                        SetMinimumLength(schema, (int)rule.Values[0]!);
                        SetMaximumLength(schema, (int)rule.Values[1]!);
                        break;
                    case SchemaRuleKind.MinLength when rule.Values.Length != 0:
                        SetMinimumLength(schema, (int)rule.Values[0]!);
                        break;
                    case SchemaRuleKind.MaxLength when rule.Values.Length != 0:
                        SetMaximumLength(schema, (int)rule.Values[0]!);
                        break;
                    case SchemaRuleKind.Pattern when rule.Values.Length != 0:
                        schema.Set("pattern", rule.Values[0]);
                        break;
                    default:
                        break;
                }
            }
        }

        private static void SetMinimumLength(ObjectNode schema, int value)
        {
            if (!schema.TryGetValue("minLength", out var current)
                || !(current is int currentValue)
                || value > currentValue)
            {
                schema.Set("minLength", value);
            }
        }

        private static void SetMaximumLength(ObjectNode schema, int value)
        {
            if (!schema.TryGetValue("maxLength", out var current)
                || !(current is int currentValue)
                || value < currentValue)
            {
                schema.Set("maxLength", value);
            }
        }

        private static string ConvertPath(RoutePatternSpec template)
        {
            if (template.Segments.Length == 0)
            {
                return "/";
            }

            return "/" + string.Join(
                "/",
                template.Segments.Select(static segment =>
                    segment.Kind == 3 ? segment.Value : "{" + segment.Value + "}"));
        }

        private static bool IsOpenApiMethod(string method) => method is
            "delete" or "get" or "head" or "options" or "patch" or "post" or "put" or "trace";

        private static ITypeSymbol NormalizeTopLevelNullability(ITypeSymbol type) =>
            type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated
                ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                : type;
    }

    private sealed class SchemaRegistry
    {
        private readonly JsonNaming _naming;
        private readonly Dictionary<ITypeSymbol, JsonTypeModel> _models =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ITypeSymbol, string> _componentNames =
            new(SymbolEqualityComparer.Default);
        private readonly HashSet<string> _usedComponentNames = new(StringComparer.Ordinal);

        internal SchemaRegistry(JsonNaming naming)
        {
            _naming = naming;
            _usedComponentNames.Add("ValidationError");
            _usedComponentNames.Add("ValidationErrorResponse");
        }

        internal ObjectNode Create(ITypeSymbol type)
        {
            var nullable = IsNullable(type);
            var normalized = Normalize(type);
            var schema = CreateCore(normalized);
            if (!nullable)
            {
                return schema;
            }

            var alternatives = new ArrayNode();
            alternatives.Add(schema);
            alternatives.Add(PrimitiveSchema("null"));
            var nullableSchema = new ObjectNode();
            nullableSchema.Add("oneOf", alternatives);
            return nullableSchema;
        }

        internal ObjectNode BuildComponents()
        {
            var schemas = new ObjectNode();
            foreach (var item in _componentNames
                         .OrderBy(static item => item.Value, StringComparer.Ordinal)
                         .ToList())
            {
                if (_models.TryGetValue(item.Key, out var model) && model.Kind == JsonTypeKind.Object)
                {
                    schemas.Add(item.Value, BuildObject(model));
                }
            }

            return schemas;
        }

        private ObjectNode CreateCore(ITypeSymbol type)
        {
            if (!EnsureModels(type) || !_models.TryGetValue(type, out var model))
            {
                return new ObjectNode();
            }

            switch (model.Kind)
            {
                case JsonTypeKind.Boolean:
                    return PrimitiveSchema("boolean");
                case JsonTypeKind.Byte:
                    return IntegerSchema("int32", 0, byte.MaxValue);
                case JsonTypeKind.SByte:
                    return IntegerSchema("int32", sbyte.MinValue, sbyte.MaxValue);
                case JsonTypeKind.Int16:
                    return IntegerSchema("int32", short.MinValue, short.MaxValue);
                case JsonTypeKind.UInt16:
                    return IntegerSchema("int32", 0, ushort.MaxValue);
                case JsonTypeKind.Int32:
                    return IntegerSchema("int32");
                case JsonTypeKind.UInt32:
                    return IntegerSchema("int64", 0);
                case JsonTypeKind.Int64:
                    return IntegerSchema("int64");
                case JsonTypeKind.UInt64:
                    return IntegerSchema(format: null, minimum: 0);
                case JsonTypeKind.Single:
                    return NumberSchema("float");
                case JsonTypeKind.Double:
                    return NumberSchema("double");
                case JsonTypeKind.Decimal:
                    return NumberSchema(format: null);
                case JsonTypeKind.Char:
                    var character = PrimitiveSchema("string");
                    character.Add("minLength", 1);
                    character.Add("maxLength", 1);
                    return character;
                case JsonTypeKind.String:
                    return PrimitiveSchema("string");
                case JsonTypeKind.Guid:
                    return FormattedStringSchema("uuid");
                case JsonTypeKind.DateTime:
                case JsonTypeKind.DateTimeOffset:
                    return FormattedStringSchema("date-time");
                case JsonTypeKind.Enum:
                    return EnumSchema(model);
                case JsonTypeKind.Nullable:
                    return Create(model.ElementType!);
                case JsonTypeKind.Array:
                case JsonTypeKind.List:
                    var array = new ObjectNode();
                    array.Add("type", "array");
                    array.Add("items", Create(model.ElementType!));
                    return array;
                case JsonTypeKind.Dictionary:
                    var dictionary = new ObjectNode();
                    dictionary.Add("type", "object");
                    dictionary.Add("additionalProperties", Create(model.DictionaryValueType!));
                    return dictionary;
                case JsonTypeKind.Object:
                    var reference = new ObjectNode();
                    reference.Add("$ref", "#/components/schemas/" + ComponentName(model.Type));
                    return reference;
                default:
                    return new ObjectNode();
            }
        }

        private ObjectNode BuildObject(JsonTypeModel model)
        {
            var properties = new ObjectNode();
            var required = new ArrayNode();
            foreach (var property in model.Properties)
            {
                properties.Add(
                    GeneratedNaming.JsonPropertyName(property.Property.Name, _naming),
                    Create(property.Property.Type));
                if (property.RequiresPresence)
                {
                    required.Add(GeneratedNaming.JsonPropertyName(property.Property.Name, _naming));
                }
            }

            var schema = new ObjectNode();
            schema.Add("type", "object");
            schema.Add("properties", properties);
            if (required.Count != 0)
            {
                schema.Add("required", required);
            }

            return schema;
        }

        private bool EnsureModels(ITypeSymbol type)
        {
            if (_models.ContainsKey(type))
            {
                return true;
            }

            if (!JsonTypeGraphBuilder.TryBuild(type, out var graph, out _))
            {
                return false;
            }

            foreach (var model in graph!.Models)
            {
                _models[model.Type] = model;
                if (model.Kind == JsonTypeKind.Object)
                {
                    _ = ComponentName(model.Type);
                }
            }

            return true;
        }

        private string ComponentName(ITypeSymbol type)
        {
            if (_componentNames.TryGetValue(type, out var existing))
            {
                return existing;
            }

            var named = (INamedTypeSymbol)Normalize(type);
            var baseName = Sanitize(named.Name);
            if (named.TypeArguments.Length != 0)
            {
                baseName += "Of" + string.Join("And", named.TypeArguments.Select(ComponentTypePart));
            }

            var candidate = baseName;
            if (_usedComponentNames.Contains(candidate))
            {
                var prefix = named.ContainingNamespace.IsGlobalNamespace
                    ? "Global"
                    : Sanitize(named.ContainingNamespace.ToDisplayString());
                candidate = prefix + "_" + baseName;
            }

            var suffix = 2;
            var unique = candidate;
            while (!_usedComponentNames.Add(unique))
            {
                unique = candidate + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            _componentNames.Add(type, unique);
            return unique;
        }

        private static string ComponentTypePart(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named)
            {
                return Sanitize(named.Name);
            }

            if (type is IArrayTypeSymbol array)
            {
                return ComponentTypePart(array.ElementType) + "Array";
            }

            return "Value";
        }

        private static string Sanitize(string value)
        {
            var result = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            return result.Length == 0 ? "Schema" : result.ToString();
        }

        private static ObjectNode EnumSchema(JsonTypeModel model)
        {
            var schema = IntegerSchema(EnumFormat(model.EnumUnderlyingType));
            var values = new ArrayNode();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in model.Type.GetMembers().OfType<IFieldSymbol>())
            {
                if (!field.HasConstantValue)
                {
                    continue;
                }

                var key = Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture) ?? string.Empty;
                if (seen.Add(key))
                {
                    values.Add(field.ConstantValue);
                }
            }

            if (values.Count != 0)
            {
                schema.Add("enum", values);
            }

            return schema;
        }

        private static string? EnumFormat(ITypeSymbol? type) => type?.SpecialType is
            SpecialType.System_Int64 or SpecialType.System_UInt32 ? "int64" : "int32";

        private static bool IsNullable(ITypeSymbol type) =>
            type.NullableAnnotation == NullableAnnotation.Annotated
            || (type is INamedTypeSymbol named
                && InvocationAnalyzer.GetMetadataName(named.OriginalDefinition) == "System.Nullable`1");

        private static ITypeSymbol Normalize(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named
                && InvocationAnalyzer.GetMetadataName(named.OriginalDefinition) == "System.Nullable`1")
            {
                return named.TypeArguments[0];
            }

            return type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated
                ? type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                : type;
        }
    }

    private sealed class ResponseShape
    {
        internal ResponseShape(ITypeSymbol? jsonType, bool hasText)
        {
            JsonType = jsonType;
            HasText = hasText;
        }

        internal ITypeSymbol? JsonType { get; }

        internal bool HasText { get; }
    }

    private sealed class ObjectNode
    {
        private readonly List<KeyValuePair<string, object?>> _properties = new();
        private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);

        internal int Count => _properties.Count;

        internal IEnumerable<KeyValuePair<string, object?>> Properties => _properties;

        internal void Add(string name, object? value)
        {
            _indexes.Add(name, _properties.Count);
            _properties.Add(new KeyValuePair<string, object?>(name, value));
        }

        internal void Set(string name, object? value)
        {
            if (_indexes.TryGetValue(name, out var index))
            {
                _properties[index] = new KeyValuePair<string, object?>(name, value);
                return;
            }

            Add(name, value);
        }

        internal bool TryGetValue(string name, out object? value)
        {
            if (_indexes.TryGetValue(name, out var index))
            {
                value = _properties[index].Value;
                return true;
            }

            value = null;
            return false;
        }
    }

    private sealed class ArrayNode
    {
        private readonly List<object?> _items = new();

        internal int Count => _items.Count;

        internal IEnumerable<object?> Items => _items;

        internal void Add(object? value) => _items.Add(value);
    }

    private static class JsonWriter
    {
        internal static string Write(ObjectNode value)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object? value, int depth)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case ObjectNode objectValue:
                    WriteObject(builder, objectValue, depth);
                    break;
                case ArrayNode arrayValue:
                    WriteArray(builder, arrayValue, depth);
                    break;
                case string text:
                    WriteString(builder, text);
                    break;
                case char character:
                    WriteString(builder, character.ToString());
                    break;
                case bool boolean:
                    builder.Append(boolean ? "true" : "false");
                    break;
                case Enum enumValue:
                    builder.Append(Convert.ToString(enumValue, CultureInfo.InvariantCulture));
                    break;
                case byte or sbyte or short or ushort or int or uint or long or ulong or decimal:
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case float single when !float.IsNaN(single) && !float.IsInfinity(single):
                    builder.Append(single.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case double number when !double.IsNaN(number) && !double.IsInfinity(number):
                    builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                default:
                    WriteString(builder, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    break;
            }
        }

        private static void WriteObject(StringBuilder builder, ObjectNode value, int depth)
        {
            if (value.Count == 0)
            {
                builder.Append("{}");
                return;
            }

            builder.Append("{\n");
            var index = 0;
            foreach (var property in value.Properties)
            {
                Indent(builder, depth + 1);
                WriteString(builder, property.Key);
                builder.Append(": ");
                WriteValue(builder, property.Value, depth + 1);
                if (++index != value.Count)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            Indent(builder, depth);
            builder.Append('}');
        }

        private static void WriteArray(StringBuilder builder, ArrayNode value, int depth)
        {
            if (value.Count == 0)
            {
                builder.Append("[]");
                return;
            }

            builder.Append("[\n");
            var index = 0;
            foreach (var item in value.Items)
            {
                Indent(builder, depth + 1);
                WriteValue(builder, item, depth + 1);
                if (++index != value.Count)
                {
                    builder.Append(',');
                }

                builder.Append('\n');
            }

            Indent(builder, depth);
            builder.Append(']');
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private static void Indent(StringBuilder builder, int depth) => builder.Append(' ', depth * 2);
    }

    private static ObjectNode PrimitiveSchema(string type)
    {
        var schema = new ObjectNode();
        schema.Add("type", type);
        return schema;
    }

    private static ObjectNode IntegerSchema(
        string? format = null,
        object? minimum = null,
        object? maximum = null)
    {
        var schema = PrimitiveSchema("integer");
        if (format is not null)
        {
            schema.Add("format", format);
        }

        if (minimum is not null)
        {
            schema.Add("minimum", minimum);
        }

        if (maximum is not null)
        {
            schema.Add("maximum", maximum);
        }

        return schema;
    }

    private static ObjectNode NumberSchema(string? format)
    {
        var schema = PrimitiveSchema("number");
        if (format is not null)
        {
            schema.Add("format", format);
        }

        return schema;
    }

    private static ObjectNode FormattedStringSchema(string format)
    {
        var schema = PrimitiveSchema("string");
        schema.Add("format", format);
        return schema;
    }
}
