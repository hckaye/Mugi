using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Miya.Generators.Core;

internal static class OpenApiImportGenerator
{
    internal static GenerationResult Generate(
        OpenApiImportInput input,
        CancellationToken cancellationToken)
    {
        var result = BuildDocument(input, cancellationToken, OpenApiGenerationMode.Import);
        if (result.Document is null)
        {
            return new GenerationResult(
                ImmutableArray<GeneratedSource>.Empty,
                result.Diagnostics);
        }

        var hintName = GeneratedNaming.StableIdentifier("Miya.OpenApi.", input.Path) + ".g.cs";
        return new GenerationResult(
            ImmutableArray.Create(new GeneratedSource(hintName, result.Source!)),
            result.Diagnostics);
    }

    internal static OpenApiDocumentBuildResult BuildDocument(
        OpenApiImportInput input,
        CancellationToken cancellationToken,
        OpenApiGenerationMode mode)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!SimpleJsonParser.TryParse(input.Content, out var value, out var parseError))
        {
            diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.InvalidOpenApiDocument,
                CreateLocation(input, parseError!.Position),
                input.Path,
                parseError.Message));
            return new OpenApiDocumentBuildResult(null, null, diagnostics.ToImmutable());
        }

        if (!(value is SimpleJsonObject root))
        {
            diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.InvalidOpenApiDocument,
                CreateLocation(input, value!.Position),
                input.Path,
                "the document root must be an object"));
            return new OpenApiDocumentBuildResult(null, null, diagnostics.ToImmutable());
        }

        var builder = new Builder(input, root, diagnostics, cancellationToken, mode);
        var document = builder.BuildModel();
        return new OpenApiDocumentBuildResult(
            document,
            document is null ? null : builder.Emit(),
            diagnostics.ToImmutable());
    }

    internal static string PublicIdentifier(string value, string fallback) =>
        Builder.PublicIdentifier(value, fallback);

    internal static bool TryExactIdentifier(string value, out string identifier) =>
        Builder.TryExactIdentifier(value, out identifier);

    internal static bool TryRenderNamespace(string value, out string rendered) =>
        Builder.TryRenderNamespace(value, out rendered);

    private static Location CreateLocation(OpenApiImportInput input, int position)
    {
        position = Math.Max(0, Math.Min(position, input.Content.Length));
        var line = 0;
        var character = 0;
        for (var index = 0; index < position; index++)
        {
            if (input.Content[index] == '\n')
            {
                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        var point = new LinePosition(line, character);
        return Location.Create(
            input.Path,
            new TextSpan(position, 0),
            new LinePositionSpan(point, point));
    }

    private sealed class Builder
    {
        private static readonly string[] HttpMethods =
        {
            "delete", "get", "head", "options", "patch", "post", "put", "trace",
        };

        private static readonly string[] UnsupportedConstraintNames =
        {
            "multipleOf", "minItems", "maxItems", "uniqueItems", "contains",
            "minContains", "maxContains", "minProperties", "maxProperties",
        };

        private static readonly string[] GeneratedClientNames =
        {
            "_http", "path", "query", "hasQueryParameter", "request", "response",
            "responseBytes", "responseBody", "bodyBuffer", "cancellationToken",
        };

        private readonly OpenApiImportInput _input;
        private readonly SimpleJsonObject _root;
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics;
        private readonly CancellationToken _cancellationToken;
        private readonly OpenApiGenerationMode _mode;
        private readonly Dictionary<string, SimpleJsonObject> _componentSchemas =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _componentTypeNames =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _invalidComponents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _typeOwners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _operationOwners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>> _enumMembers =
            new(StringComparer.Ordinal);
        private readonly List<OpenApiImportDeclaration> _declarations = new();
        private readonly List<OpenApiImportOperation> _operations = new();
        private readonly List<OpenApiClientOperation> _clientOperations = new();
        private readonly List<KeyValuePair<string, string>> _paths = new();
        private string _namespace = string.Empty;
        private string? _title;
        private string? _clientName;

        internal Builder(
            OpenApiImportInput input,
            SimpleJsonObject root,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken,
            OpenApiGenerationMode mode)
        {
            _input = input;
            _root = root;
            _diagnostics = diagnostics;
            _cancellationToken = cancellationToken;
            _mode = mode;
            if (_mode == OpenApiGenerationMode.Import || _input.ServerImport)
            {
                _typeOwners.Add("Paths", "generated Paths class");
                _typeOwners.Add("ApiSchemas", "generated ApiSchemas class");
            }
        }

        internal OpenApiImportDocument? BuildModel()
        {
            if (!TryValidateDocument())
            {
                return null;
            }

            ReadComponents();
            BuildComponents();
            BuildPaths();
            FilterInvalidDependencies();
            return new OpenApiImportDocument(
                _namespace,
                _title,
                _declarations,
                _operations,
                _paths,
                _clientOperations,
                new HashSet<string>(_componentTypeNames.Values, StringComparer.Ordinal),
                _mode == OpenApiGenerationMode.Client && _input.ServerImport);
        }

        internal string Emit()
        {
            var writer = new CodeWriter();
            writer.Line("// <auto-generated/>");
            writer.Line("#nullable enable");
            writer.Line();
            writer.Open("namespace " + _namespace);

            foreach (var declaration in _declarations
                         .OrderBy(static declaration => declaration.Name, StringComparer.Ordinal))
            {
                EmitDeclaration(writer, declaration);
                writer.Line();
            }

            EmitPaths(writer);
            writer.Line();
            foreach (var operation in _operations.OrderBy(static operation => operation.Name, StringComparer.Ordinal))
            {
                EmitRecord(writer, operation.InputName, operation.Fields);
                writer.Line();
            }

            EmitSchemas(writer);
            writer.Close();
            return writer.ToString();
        }

        private bool TryValidateDocument()
        {
            if (!TryGetString(_root, "openapi", out var version, out var versionValue)
                || !(version!.StartsWith("3.0.", StringComparison.Ordinal)
                     || version.StartsWith("3.1.", StringComparison.Ordinal)))
            {
                ReportInvalid(
                    versionValue?.Position ?? _root.Position,
                    "the 'openapi' value must identify OpenAPI 3.0 or 3.1");
                return false;
            }

            if (!TryRenderNamespace(_input.TargetNamespace, out _namespace))
            {
                ReportUnrepresentable(
                    _root.Position,
                    _input.TargetNamespace,
                    "the target namespace is not a valid C# namespace");
                return false;
            }

            if (TryGetObject(_root, "info", out var info)
                && TryGetString(info!, "title", out var title, out _)
                && !string.IsNullOrWhiteSpace(title))
            {
                _title = title;
            }

            if (_mode == OpenApiGenerationMode.Client)
            {
                _typeOwners.Add("ApiException", "generated ApiException class");
                var clientName = _input.ClientName;
                if (string.IsNullOrWhiteSpace(clientName))
                {
                    clientName = PublicIdentifier(_title ?? "OpenApi", "OpenApi") + "Client";
                }

                if (!TryExactIdentifier(clientName!, out var renderedClientName))
                {
                    ReportUnrepresentable(
                        _root.Position,
                        clientName!,
                        "the client class name is not a valid C# identifier");
                    return false;
                }

                _clientName = UnescapeIdentifier(renderedClientName);
                if (!TryReserveType(UnescapeIdentifier(renderedClientName), "generated client class", _root.Position))
                {
                    return false;
                }
            }

            if (_root.TryGetValue("paths", out var paths) && !(paths is SimpleJsonObject))
            {
                ReportInvalid(paths.Position, "the 'paths' value must be an object");
                return false;
            }

            if (_root.TryGetValue("components", out var components) && !(components is SimpleJsonObject))
            {
                ReportInvalid(components.Position, "the 'components' value must be an object");
                return false;
            }

            return true;
        }

        private void ReadComponents()
        {
            if (!TryGetObject(_root, "components", out var components)
                || !TryGetObject(components!, "schemas", out var schemas))
            {
                return;
            }

            foreach (var property in schemas!.Properties)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!(property.Value is SimpleJsonObject schema))
                {
                    ReportUnsupportedSchema(
                        property.Value.Position,
                        property.Name,
                        "a component schema must be an object");
                    _invalidComponents.Add(property.Name);
                    continue;
                }

                var typeName = PublicIdentifier(property.Name, "Schema");
                _componentSchemas[property.Name] = schema;
                _componentTypeNames[property.Name] = typeName;
                if (!TryReserveType(typeName, "component '" + property.Name + "'", property.Value.Position))
                {
                    _invalidComponents.Add(property.Name);
                }
            }
        }

        private void BuildComponents()
        {
            foreach (var component in _componentSchemas.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (_invalidComponents.Contains(component.Key))
                {
                    continue;
                }

                var declaration = BuildNamedDeclaration(
                    component.Key,
                    _componentTypeNames[component.Key],
                    component.Value,
                    reserveName: false);
                if (declaration is null)
                {
                    _invalidComponents.Add(component.Key);
                    continue;
                }

                _declarations.Add(declaration);
            }
        }

        private OpenApiImportDeclaration? BuildNamedDeclaration(
            string context,
            string typeName,
            SimpleJsonObject schema,
            bool reserveName)
        {
            if (HasUnsupportedSchemaShape(schema, context))
            {
                return null;
            }

            if (reserveName && !TryReserveType(typeName, "inline schema '" + context + "'", schema.Position))
            {
                return null;
            }

            if (schema.TryGetValue("$ref", out var reference))
            {
                ReportUnsupportedSchema(
                    reference.Position,
                    context,
                    "named component aliases are not supported; reference the target component directly");
                return null;
            }

            if (TryGetArray(schema, "enum", out _))
            {
                return BuildEnumDeclaration(context, typeName, schema);
            }

            var hasType = schema.TryGetValue("type", out var typeValue);
            if (!hasType
                || (TryGetSchemaType(schema, out var schemaType, out _)
                    && string.Equals(schemaType, "object", StringComparison.Ordinal)))
            {
                return BuildRecordDeclaration(context, typeName, schema);
            }

            if (hasType && !TryGetSchemaType(schema, out schemaType, out _))
            {
                ReportUnsupportedSchema(
                    typeValue.Position,
                    context,
                    "type must contain one supported type and optional null");
                return null;
            }

            ReportUnsupportedSchema(
                schema.Position,
                context,
                "named component schemas must be objects or string enums");
            return null;
        }

        private OpenApiImportEnum? BuildEnumDeclaration(
            string context,
            string typeName,
            SimpleJsonObject schema)
        {
            if (TryGetSchemaType(schema, out var schemaType, out _)
                && !string.Equals(schemaType, "string", StringComparison.Ordinal))
            {
                ReportUnsupportedSchema(
                    schema.Position,
                    context,
                    "only string enums can be generated as C# enums");
                return null;
            }

            if (!TryGetArray(schema, "enum", out var values) || values!.Items.Count == 0)
            {
                ReportUnsupportedSchema(schema.Position, context, "a string enum must declare at least one value");
                return null;
            }

            var members = new List<KeyValuePair<string, string>>(values.Items.Count);
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values.Items)
            {
                if (!(value is SimpleJsonString text))
                {
                    ReportUnsupportedSchema(value.Position, context, "string enum values must be strings");
                    return null;
                }

                var identifier = PublicIdentifier(text.Value, "Value");
                if (string.Equals(identifier, typeName, StringComparison.Ordinal)
                    || !identifiers.Add(identifier))
                {
                    ReportNameCollision(value.Position, text.Value, identifier);
                    return null;
                }

                members.Add(new KeyValuePair<string, string>(text.Value, identifier));
            }

            _enumMembers[typeName] = members;
            return new OpenApiImportEnum(typeName, members);
        }

        private OpenApiImportRecord? BuildRecordDeclaration(
            string context,
            string typeName,
            SimpleJsonObject schema)
        {
            if (schema.TryGetValue("additionalProperties", out var additionalProperties))
            {
                ReportUnsupportedSchema(
                    additionalProperties.Position,
                    context,
                    "additionalProperties is not supported");
                return null;
            }

            if (!TryReadRequiredNames(schema, context, out var requiredNames))
            {
                return null;
            }

            var properties = new List<OpenApiImportProperty>();
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            if (!TryGetObject(schema, "properties", out var propertyObject))
            {
                return new OpenApiImportRecord(typeName, properties, dependencies);
            }

            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in propertyObject!.Properties)
            {
                if (!(property.Value is SimpleJsonObject propertySchema))
                {
                    ReportUnsupportedSchema(
                        property.Value.Position,
                        context + "." + property.Name,
                        "a property schema must be an object");
                    return null;
                }

                if (!TryJsonPropertyIdentifier(property.Name, out var identifier))
                {
                    ReportUnrepresentable(
                        property.Position,
                        context + "." + property.Name,
                        "the JSON property name cannot be preserved by Miya's JSON naming policy");
                    return null;
                }

                if (string.Equals(identifier, typeName, StringComparison.Ordinal)
                    || !identifiers.Add(identifier))
                {
                    ReportNameCollision(property.Position, property.Name, identifier);
                    return null;
                }

                var suggestedName = typeName + PublicIdentifier(property.Name, "Value");
                if (!TryResolveType(
                        propertySchema,
                        context + "." + property.Name,
                        suggestedName,
                        allowInlineObject: true,
                        out var type))
                {
                    return null;
                }

                AddDependencies(type!, dependencies);
                properties.Add(new OpenApiImportProperty(
                    property.Name,
                    EscapeIdentifier(identifier),
                    type!,
                    requiredNames.Contains(property.Name),
                    source: string.Empty,
                    headerName: null,
                    rules: Array.Empty<string>()));
            }

            foreach (var requiredName in requiredNames)
            {
                if (!propertyObject.Properties.Any(property =>
                        string.Equals(property.Name, requiredName, StringComparison.Ordinal)))
                {
                    ReportUnsupportedSchema(
                        schema.Position,
                        context,
                        "required property '" + requiredName + "' is not declared in properties");
                    return null;
                }
            }

            return new OpenApiImportRecord(typeName, properties, dependencies);
        }

        private void BuildPaths()
        {
            if (!TryGetObject(_root, "paths", out var paths))
            {
                return;
            }

            foreach (var pathProperty in paths!.Properties)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!(pathProperty.Value is SimpleJsonObject pathItem))
                {
                    ReportInvalid(pathProperty.Value.Position, "path item '" + pathProperty.Name + "' must be an object");
                    continue;
                }

                if (!TryConvertPath(pathProperty.Name, pathProperty.Position, out var miyaPath, out var routeNames))
                {
                    continue;
                }

                TryReadParameterArray(pathItem, "parameters", pathProperty.Name, out var pathParameters);
                foreach (var method in HttpMethods)
                {
                    if (!pathItem.TryGetValue(method, out var operationValue))
                    {
                        continue;
                    }

                    if (!(operationValue is SimpleJsonObject operation))
                    {
                        ReportInvalid(operationValue.Position, method.ToUpperInvariant() + " " + pathProperty.Name + " must be an object");
                        continue;
                    }

                    BuildOperation(
                        method,
                        pathProperty.Name,
                        miyaPath!,
                        routeNames!,
                        pathParameters,
                        operation);
                }
            }
        }

        private void BuildOperation(
            string method,
            string openApiPath,
            string miyaPath,
            HashSet<string> routeNames,
            IReadOnlyList<SimpleJsonObject> pathParameters,
            SimpleJsonObject operation)
        {
            var operationLabel = method.ToUpperInvariant() + " " + openApiPath;
            string rawName;
            if (operation.TryGetValue("operationId", out var operationIdValue))
            {
                if (!(operationIdValue is SimpleJsonString operationId) || operationId.Value.Length == 0)
                {
                    ReportInvalid(operationIdValue.Position, operationLabel + " has an invalid operationId");
                    return;
                }

                rawName = operationId.Value;
            }
            else
            {
                rawName = FallbackOperationName(method, openApiPath);
            }

            var name = PublicIdentifier(rawName, "Operation");
            if (((_mode == OpenApiGenerationMode.Import || _input.ServerImport)
                    && (string.Equals(name, "Paths", StringComparison.Ordinal)
                        || string.Equals(name, "ApiSchemas", StringComparison.Ordinal)))
                || string.Equals(name, _clientName, StringComparison.Ordinal)
                || _operationOwners.TryGetValue(name, out _))
            {
                ReportNameCollision(operation.Position, rawName, name);
                return;
            }

            _operationOwners.Add(name, operationLabel);
            _paths.Add(new KeyValuePair<string, string>(name, miyaPath));

            var inputName = name + "Input";
            if ((_mode == OpenApiGenerationMode.Import || _input.ServerImport)
                && !TryReserveType(inputName, "operation '" + operationLabel + "'", operation.Position))
            {
                return;
            }

            if (!TryReadParameterArray(operation, "parameters", operationLabel, out var operationParameters))
            {
                return;
            }

            var parameters = MergeParameters(pathParameters, operationParameters);
            var fields = new List<OpenApiImportProperty>();
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            if (_mode == OpenApiGenerationMode.Client)
            {
                foreach (var generatedName in GeneratedClientNames)
                {
                    propertyNames.Add(generatedName);
                }
            }
            var boundRouteNames = new HashSet<string>(StringComparer.Ordinal);
            var valid = true;
            foreach (var parameter in parameters)
            {
                if (!TryBuildParameter(
                        parameter,
                        operationLabel,
                        inputName,
                        propertyNames,
                        out var field))
                {
                    valid = false;
                    continue;
                }

                if (field is null)
                {
                    continue;
                }

                fields.Add(field);
                AddDependencies(field.Type, dependencies);
                if (field.Source == "Route")
                {
                    boundRouteNames.Add(field.OpenApiName);
                }
            }

            foreach (var routeName in routeNames)
            {
                if (!boundRouteNames.Contains(routeName))
                {
                    ReportInvalid(
                        operation.Position,
                        operationLabel + " has no path parameter declaration for '{" + routeName + "}'");
                    valid = false;
                }
            }

            if (!TryBuildRequestBody(
                    operation,
                    operationLabel,
                    inputName,
                    propertyNames,
                    fields,
                    dependencies,
                    out var bodyType,
                    out var bodyRequired))
            {
                valid = false;
            }

            OpenApiImportType? responseType = null;
            IReadOnlyList<string> jsonResponseStatuses = Array.Empty<string>();
            IReadOnlyList<string> noBodyResponseStatuses = Array.Empty<string>();
            if (_mode == OpenApiGenerationMode.Client
                && !TryBuildResponse(
                    operation,
                    operationLabel,
                    inputName,
                    dependencies,
                    out responseType,
                    out jsonResponseStatuses,
                    out noBodyResponseStatuses))
            {
                valid = false;
            }

            if (valid)
            {
                if (_mode == OpenApiGenerationMode.Client)
                {
                    _clientOperations.Add(new OpenApiClientOperation(
                        name,
                        method,
                        miyaPath,
                        fields,
                        bodyType,
                        bodyRequired,
                        responseType,
                        jsonResponseStatuses,
                        noBodyResponseStatuses,
                        dependencies));
                }
                else
                {
                    _operations.Add(new OpenApiImportOperation(name, inputName, fields, dependencies));
                }
            }
        }

        private bool TryBuildParameter(
            SimpleJsonObject parameter,
            string operationLabel,
            string inputName,
            HashSet<string> propertyNames,
            out OpenApiImportProperty? field)
        {
            field = null;
            if (parameter.TryGetValue("$ref", out var reference))
            {
                ReportUnrepresentable(
                    reference.Position,
                    operationLabel,
                    "referenced parameter objects are not supported; place the parameter on the operation");
                return false;
            }

            var hasName = TryGetString(parameter, "name", out var name, out var nameValue);
            var hasSource = TryGetString(parameter, "in", out var source, out var sourceValue);
            if (!hasName || string.IsNullOrEmpty(name) || !hasSource)
            {
                ReportInvalid(
                    nameValue?.Position ?? sourceValue?.Position ?? parameter.Position,
                    operationLabel + " has a parameter without valid 'name' and 'in' values");
                return false;
            }

            string binding;
            string identifier;
            string? headerName = null;
            switch (source)
            {
                case "path":
                    binding = "Route";
                    if (!TryExactIdentifier(name!, out identifier))
                    {
                        ReportUnrepresentable(
                            nameValue!.Position,
                            operationLabel + " parameter '" + name + "'",
                            "path parameter names must also be valid C# identifiers");
                        return false;
                    }

                    break;
                case "query":
                    binding = "Query";
                    if (!TryExactIdentifier(name!, out identifier))
                    {
                        ReportUnrepresentable(
                            nameValue!.Position,
                            operationLabel + " parameter '" + name + "'",
                            "query parameter names must also be valid C# identifiers");
                        return false;
                    }

                    break;
                case "header":
                    binding = "Header";
                    identifier = PublicIdentifier(name!, "Header");
                    headerName = name;
                    break;
                case "cookie":
                    ReportUnrepresentable(
                        sourceValue!.Position,
                        operationLabel + " parameter '" + name + "'",
                        "cookie parameters have no Miya.Schema source");
                    return _mode != OpenApiGenerationMode.Client;
                default:
                    ReportInvalid(
                        sourceValue!.Position,
                        operationLabel + " parameter '" + name + "' has an invalid 'in' value");
                    return false;
            }

            var symbolName = UnescapeIdentifier(identifier);
            if (string.Equals(symbolName, inputName, StringComparison.Ordinal)
                || !propertyNames.Add(symbolName))
            {
                ReportNameCollision(nameValue!.Position, name!, symbolName);
                return false;
            }

            if (!TryGetObject(parameter, "schema", out var schema))
            {
                ReportUnrepresentable(
                    parameter.Position,
                    operationLabel + " parameter '" + name + "'",
                    "parameters must use a schema object");
                return false;
            }

            var required = string.Equals(source, "path", StringComparison.Ordinal)
                || GetBoolean(parameter, "required", defaultValue: false);
            var suggestedName = inputName + PublicIdentifier(name!, "Value");
            if (!TryResolveType(
                    schema!,
                    operationLabel + " parameter '" + name + "'",
                    suggestedName,
                    allowInlineObject: false,
                    out var type))
            {
                return false;
            }

            if (!IsTextType(type!))
            {
                ReportUnrepresentable(
                    schema!.Position,
                    operationLabel + " parameter '" + name + "'",
                    "path, query, and header parameters must use scalar text values");
                return false;
            }

            if (_mode == OpenApiGenerationMode.Client && required && type!.Nullable)
            {
                ReportUnrepresentable(
                    schema!.Position,
                    operationLabel + " parameter '" + name + "'",
                    "required scalar parameters cannot use a nullable schema");
                return false;
            }

            var rules = BuildRules(
                EffectiveSchema(schema!),
                type!,
                required,
                operationLabel + " parameter '" + name + "'");
            field = new OpenApiImportProperty(
                name!,
                EscapeIdentifier(symbolName),
                type!,
                required,
                binding,
                headerName,
                rules);
            return true;
        }

        private bool TryBuildRequestBody(
            SimpleJsonObject operation,
            string operationLabel,
            string inputName,
            HashSet<string> propertyNames,
            List<OpenApiImportProperty> fields,
            HashSet<string> dependencies,
            out OpenApiImportType? bodyType,
            out bool bodyRequired)
        {
            bodyType = null;
            bodyRequired = false;
            if (!operation.TryGetValue("requestBody", out var requestBodyValue))
            {
                return true;
            }

            if (!(requestBodyValue is SimpleJsonObject requestBody))
            {
                ReportInvalid(requestBodyValue.Position, operationLabel + " requestBody must be an object");
                return false;
            }

            if (requestBody.TryGetValue("$ref", out var requestBodyReference))
            {
                ReportUnrepresentable(
                    requestBodyReference.Position,
                    operationLabel + " requestBody",
                    "referenced requestBody objects are not supported");
                return false;
            }

            if (!TryGetObject(requestBody, "content", out var content))
            {
                ReportUnrepresentable(
                    requestBody.Position,
                    operationLabel + " requestBody",
                    "a JSON request body must declare content");
                return false;
            }

            SimpleJsonObject? mediaType = null;
            foreach (var contentProperty in content!.Properties)
            {
                if (string.Equals(contentProperty.Name, "application/json", StringComparison.OrdinalIgnoreCase)
                    || contentProperty.Name.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                {
                    mediaType = contentProperty.Value as SimpleJsonObject;
                    if (mediaType is null)
                    {
                        ReportInvalid(contentProperty.Value.Position, operationLabel + " JSON media type must be an object");
                        return false;
                    }

                    break;
                }
            }

            if (mediaType is null)
            {
                ReportUnrepresentable(
                    content.Position,
                    operationLabel + " requestBody",
                    "only application/json request bodies can be mapped to Miya.Schema");
                return _mode != OpenApiGenerationMode.Client;
            }

            if (!TryGetObject(mediaType, "schema", out var bodySchema))
            {
                ReportUnrepresentable(
                    mediaType.Position,
                    operationLabel + " requestBody",
                    "the JSON media type must declare a schema object");
                return false;
            }

            var effectiveBody = EffectiveSchema(bodySchema!);
            if (effectiveBody is null || HasUnsupportedSchemaShape(effectiveBody, operationLabel + " requestBody"))
            {
                return false;
            }

            bodyRequired = GetBoolean(requestBody, "required", defaultValue: false);
            if (_mode == OpenApiGenerationMode.Client)
            {
                if (!TryResolveType(
                        bodySchema!,
                        operationLabel + " requestBody",
                        ClientOperationTypePrefix(inputName) + "Body",
                        allowInlineObject: true,
                        out bodyType))
                {
                    return false;
                }

                if (bodyType!.Kind != OpenApiImportTypeKind.Object)
                {
                    ReportUnrepresentable(
                        effectiveBody.Position,
                        operationLabel + " requestBody",
                        "request bodies must be JSON objects");
                    return false;
                }

                AddDependencies(bodyType, dependencies);
                return true;
            }

            if (effectiveBody.TryGetValue("additionalProperties", out var additionalProperties))
            {
                ReportUnsupportedSchema(
                    additionalProperties.Position,
                    operationLabel + " requestBody",
                    "additionalProperties is not supported");
                return false;
            }

            if (TryGetSchemaType(effectiveBody, out var bodySchemaType, out _)
                && !string.Equals(bodySchemaType, "object", StringComparison.Ordinal))
            {
                ReportUnrepresentable(
                    effectiveBody.Position,
                    operationLabel + " requestBody",
                    "Miya.Schema request bodies must be JSON objects");
                return false;
            }

            if (!TryReadRequiredNames(effectiveBody, operationLabel + " requestBody", out var requiredNames))
            {
                return false;
            }

            if (!TryGetObject(effectiveBody, "properties", out var bodyProperties))
            {
                return true;
            }

            foreach (var property in bodyProperties!.Properties)
            {
                if (!(property.Value is SimpleJsonObject propertySchema))
                {
                    ReportUnsupportedSchema(
                        property.Value.Position,
                        operationLabel + " requestBody." + property.Name,
                        "a property schema must be an object");
                    return false;
                }

                if (!TryJsonPropertyIdentifier(property.Name, out var identifier))
                {
                    ReportUnrepresentable(
                        property.Position,
                        operationLabel + " requestBody." + property.Name,
                        "the JSON property name cannot be preserved by Miya's JSON naming policy");
                    return false;
                }

                if (string.Equals(identifier, inputName, StringComparison.Ordinal)
                    || !propertyNames.Add(identifier))
                {
                    ReportNameCollision(property.Position, property.Name, identifier);
                    return false;
                }

                var required = bodyRequired && requiredNames.Contains(property.Name);
                var suggestedName = inputName + PublicIdentifier(property.Name, "Value");
                if (!TryResolveType(
                        propertySchema,
                        operationLabel + " requestBody." + property.Name,
                        suggestedName,
                        allowInlineObject: true,
                        out var type))
                {
                    return false;
                }

                var rules = BuildRules(
                    EffectiveSchema(propertySchema),
                    type!,
                    required,
                    operationLabel + " requestBody." + property.Name);
                AddDependencies(type!, dependencies);
                fields.Add(new OpenApiImportProperty(
                    property.Name,
                    EscapeIdentifier(identifier),
                    type!,
                    required,
                    "Body",
                    headerName: null,
                    rules));
            }

            return true;
        }

        private bool TryBuildResponse(
            SimpleJsonObject operation,
            string operationLabel,
            string inputName,
            HashSet<string> dependencies,
            out OpenApiImportType? responseType,
            out IReadOnlyList<string> jsonResponseStatuses,
            out IReadOnlyList<string> noBodyResponseStatuses)
        {
            responseType = null;
            jsonResponseStatuses = Array.Empty<string>();
            noBodyResponseStatuses = Array.Empty<string>();
            if (!TryGetObject(operation, "responses", out var responses))
            {
                if (operation.TryGetValue("responses", out var invalidResponses))
                {
                    ReportInvalid(invalidResponses.Position, operationLabel + " responses must be an object");
                    return false;
                }

                return true;
            }

            var jsonResponses = new List<KeyValuePair<string, SimpleJsonObject>>();
            var noBodyStatuses = new List<string>();
            var hasNonJsonSuccessBody = false;
            foreach (var responseProperty in responses!.Properties)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (!IsSuccessStatusCode(responseProperty.Name))
                {
                    continue;
                }

                if (!(responseProperty.Value is SimpleJsonObject response))
                {
                    ReportInvalid(
                        responseProperty.Value.Position,
                        operationLabel + " response '" + responseProperty.Name + "' must be an object");
                    return false;
                }

                if (response.TryGetValue("$ref", out var responseReference))
                {
                    ReportUnrepresentable(
                        responseReference.Position,
                        operationLabel + " response '" + responseProperty.Name + "'",
                        "referenced response objects are not supported");
                    return false;
                }

                if (!TryGetObject(response, "content", out var content))
                {
                    if (response.TryGetValue("content", out var invalidContent))
                    {
                        ReportInvalid(
                            invalidContent.Position,
                            operationLabel + " response '" + responseProperty.Name + "'.content must be an object");
                        return false;
                    }

                    noBodyStatuses.Add(responseProperty.Name);
                    continue;
                }

                SimpleJsonObject? jsonMediaType = null;
                foreach (var contentProperty in content!.Properties)
                {
                    if (!IsJsonMediaType(contentProperty.Name))
                    {
                        continue;
                    }

                    jsonMediaType = contentProperty.Value as SimpleJsonObject;
                    if (jsonMediaType is null)
                    {
                        ReportInvalid(
                            contentProperty.Value.Position,
                            operationLabel + " response JSON media type must be an object");
                        return false;
                    }

                    break;
                }

                if (jsonMediaType is null)
                {
                    if (content.Properties.Count != 0)
                    {
                        hasNonJsonSuccessBody = true;
                    }
                    else
                    {
                        noBodyStatuses.Add(responseProperty.Name);
                    }

                    continue;
                }

                if (!TryGetObject(jsonMediaType, "schema", out var responseSchema))
                {
                    ReportUnrepresentable(
                        jsonMediaType.Position,
                        operationLabel + " response '" + responseProperty.Name + "'",
                        "the JSON media type must declare a schema object");
                    return false;
                }

                jsonResponses.Add(new KeyValuePair<string, SimpleJsonObject>(
                    responseProperty.Name,
                    responseSchema!));
            }

            if (hasNonJsonSuccessBody)
            {
                ReportUnrepresentable(
                    responses.Position,
                    operationLabel + " responses",
                    "only JSON success response bodies are supported by the generated client");
                return false;
            }

            noBodyResponseStatuses = noBodyStatuses;
            if (jsonResponses.Count == 0)
            {
                return true;
            }

            var selectedResponse = jsonResponses
                .OrderBy(static response => ResponseStatusPriority(response.Key))
                .First();
            foreach (var candidate in jsonResponses)
            {
                if (!ResponseSchemasMatch(selectedResponse.Value, candidate.Value))
                {
                    ReportUnrepresentable(
                        candidate.Value.Position,
                        operationLabel + " responses",
                        "success statuses declare incompatible JSON schemas");
                    return false;
                }
            }

            if (!TryResolveType(
                    selectedResponse.Value,
                    operationLabel + " response",
                    ClientOperationTypePrefix(inputName) + "Response",
                    allowInlineObject: true,
                    out responseType))
            {
                return false;
            }

            AddDependencies(responseType!, dependencies);
            jsonResponseStatuses = jsonResponses.Select(static response => response.Key).ToArray();
            return true;
        }

        private static int ResponseStatusPriority(string status) => status == "200"
            ? 0
            : status == "201"
                ? 1
                : string.Equals(status, "2XX", StringComparison.OrdinalIgnoreCase)
                    ? 3
                    : 2;

        private static bool ResponseSchemasMatch(SimpleJsonObject left, SimpleJsonObject right)
        {
            var leftShape = new StringBuilder();
            var rightShape = new StringBuilder();
            AppendResponseSchemaShape(leftShape, left);
            AppendResponseSchemaShape(rightShape, right);
            return string.Equals(leftShape.ToString(), rightShape.ToString(), StringComparison.Ordinal);
        }

        private static void AppendResponseSchemaShape(StringBuilder builder, SimpleJsonObject schema)
        {
            foreach (var property in schema.Properties
                         .Where(static property => IsResponseShapeProperty(property.Name))
                         .OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                builder.Append(property.Name);
                builder.Append(':');
                if (property.Name == "properties" && property.Value is SimpleJsonObject properties)
                {
                    foreach (var item in properties.Properties.OrderBy(
                                 static item => item.Name,
                                 StringComparer.Ordinal))
                    {
                        builder.Append(item.Name);
                        builder.Append('=');
                        if (item.Value is SimpleJsonObject propertySchema)
                        {
                            AppendResponseSchemaShape(builder, propertySchema);
                        }
                        else
                        {
                            AppendResponseShapeValue(builder, item.Value, sortArray: false);
                        }

                        builder.Append(';');
                    }
                }
                else if ((property.Name == "required" || property.Name == "type")
                         && property.Value is SimpleJsonArray)
                {
                    AppendResponseShapeValue(builder, property.Value, sortArray: true);
                }
                else if ((property.Name == "items" || property.Name == "additionalProperties")
                         && property.Value is SimpleJsonObject nestedSchema)
                {
                    AppendResponseSchemaShape(builder, nestedSchema);
                }
                else
                {
                    AppendResponseShapeValue(builder, property.Value, sortArray: false);
                }

                builder.Append('|');
            }
        }

        private static void AppendResponseShapeValue(
            StringBuilder builder,
            SimpleJsonValue value,
            bool sortArray)
        {
            switch (value)
            {
                case SimpleJsonString text:
                    builder.Append('"');
                    builder.Append(text.Value);
                    builder.Append('"');
                    break;
                case SimpleJsonNumber number:
                    builder.Append(number.Text);
                    break;
                case SimpleJsonBoolean boolean:
                    builder.Append(boolean.Value ? "true" : "false");
                    break;
                case SimpleJsonNull:
                    builder.Append("null");
                    break;
                case SimpleJsonObject nested:
                    AppendResponseSchemaShape(builder, nested);
                    break;
                case SimpleJsonArray array:
                    var values = new List<string>(array.Items.Count);
                    foreach (var item in array.Items)
                    {
                        var itemBuilder = new StringBuilder();
                        AppendResponseShapeValue(itemBuilder, item, sortArray: false);
                        values.Add(itemBuilder.ToString());
                    }

                    if (sortArray)
                    {
                        values.Sort(StringComparer.Ordinal);
                    }

                    builder.Append('[');
                    builder.Append(string.Join(",", values));
                    builder.Append(']');
                    break;
            }
        }

        private static bool IsResponseShapeProperty(string name) => name is
            "$ref" or "type" or "format" or "nullable" or "enum" or "properties"
            or "required" or "items" or "additionalProperties";

        private static string ClientOperationTypePrefix(string inputName) =>
            inputName.EndsWith("Input", StringComparison.Ordinal)
                ? inputName.Substring(0, inputName.Length - "Input".Length)
                : inputName;

        private bool TryResolveType(
            SimpleJsonObject schema,
            string context,
            string suggestedName,
            bool allowInlineObject,
            out OpenApiImportType? type)
        {
            type = null;
            if (HasUnsupportedSchemaShape(schema, context))
            {
                return false;
            }

            var nullable = IsNullableSchema(schema);
            if (schema.TryGetValue("$ref", out var referenceValue))
            {
                if (!(referenceValue is SimpleJsonString reference)
                    || !TryResolveComponentReference(reference.Value, out var componentName)
                    || !_componentTypeNames.TryGetValue(componentName!, out var referencedType))
                {
                    ReportUnsupportedSchema(
                        referenceValue.Position,
                        context,
                        "only local references under #/components/schemas are supported");
                    return false;
                }

                if (_invalidComponents.Contains(componentName!))
                {
                    ReportUnrepresentable(
                        referenceValue.Position,
                        context,
                        "the referenced component '" + componentName + "' was skipped");
                    return false;
                }

                var referencedSchema = _componentSchemas[componentName!];
                var kind = IsStringEnum(referencedSchema)
                    ? OpenApiImportTypeKind.Enum
                    : OpenApiImportTypeKind.Object;
                type = new OpenApiImportType(
                    kind,
                    referencedType,
                    nullable: nullable || IsNullableSchema(referencedSchema));
                return true;
            }

            if (TryGetArray(schema, "enum", out _))
            {
                var enumName = PublicIdentifier(suggestedName, "Value");
                var declaration = BuildNamedDeclaration(context, enumName, schema, reserveName: true);
                if (!(declaration is OpenApiImportEnum enumDeclaration))
                {
                    return false;
                }

                _declarations.Add(enumDeclaration);
                type = new OpenApiImportType(OpenApiImportTypeKind.Enum, enumName, nullable: nullable);
                return true;
            }

            if (!TryGetSchemaType(schema, out var schemaType, out var typeValue))
            {
                if (schema.TryGetValue("properties", out _))
                {
                    schemaType = "object";
                }
                else if (schema.TryGetValue("type", out var invalidType))
                {
                    ReportUnsupportedSchema(
                        invalidType.Position,
                        context,
                        "type must contain one supported type and optional null");
                    return false;
                }
                else
                {
                    ReportUnsupportedSchema(
                        typeValue?.Position ?? schema.Position,
                        context,
                        "the schema must declare a supported type, properties, or $ref");
                    return false;
                }
            }

            switch (schemaType)
            {
                case "string":
                    type = new OpenApiImportType(OpenApiImportTypeKind.String, nullable: nullable);
                    return true;
                case "integer":
                    var integerFormat = GetOptionalString(schema, "format");
                    if (integerFormat is null || integerFormat == "int32")
                    {
                        type = new OpenApiImportType(OpenApiImportTypeKind.Int32, nullable: nullable);
                        return true;
                    }

                    if (integerFormat == "int64")
                    {
                        type = new OpenApiImportType(OpenApiImportTypeKind.Int64, nullable: nullable);
                        return true;
                    }

                    ReportUnrepresentable(
                        schema.Position,
                        context,
                        "integer format '" + integerFormat + "' has no configured C# mapping");
                    return false;
                case "number":
                    var numberFormat = GetOptionalString(schema, "format");
                    switch (numberFormat)
                    {
                        case "float":
                            type = new OpenApiImportType(OpenApiImportTypeKind.Single, nullable: nullable);
                            return true;
                        case null:
                        case "double":
                            type = new OpenApiImportType(OpenApiImportTypeKind.Double, nullable: nullable);
                            return true;
                        case "decimal":
                            type = new OpenApiImportType(OpenApiImportTypeKind.Decimal, nullable: nullable);
                            return true;
                        default:
                            ReportUnrepresentable(
                                schema.Position,
                                context,
                                "number format '" + numberFormat + "' has no configured C# mapping");
                            return false;
                    }
                case "boolean":
                    type = new OpenApiImportType(OpenApiImportTypeKind.Boolean, nullable: nullable);
                    return true;
                case "array":
                    if (!TryGetObject(schema, "items", out var items))
                    {
                        ReportUnsupportedSchema(schema.Position, context, "an array schema must declare one items schema");
                        return false;
                    }

                    if (!TryResolveType(
                            items!,
                            context + " items",
                            suggestedName + "Item",
                            allowInlineObject: true,
                            out var elementType))
                    {
                        return false;
                    }

                    type = new OpenApiImportType(
                        OpenApiImportTypeKind.Array,
                        elementType: elementType,
                        nullable: nullable);
                    return true;
                case "object":
                    if (!allowInlineObject)
                    {
                        ReportUnrepresentable(
                            schema.Position,
                            context,
                            "object values cannot be read from path, query, or header parameters");
                        return false;
                    }

                    var objectName = PublicIdentifier(suggestedName, "Value");
                    var objectDeclaration = BuildNamedDeclaration(
                        context,
                        objectName,
                        schema,
                        reserveName: true);
                    if (!(objectDeclaration is OpenApiImportRecord recordDeclaration))
                    {
                        return false;
                    }

                    _declarations.Add(recordDeclaration);
                    type = new OpenApiImportType(OpenApiImportTypeKind.Object, objectName, nullable: nullable);
                    return true;
                default:
                    ReportUnrepresentable(
                        typeValue?.Position ?? schema.Position,
                        context,
                        "schema type '" + schemaType + "' has no configured C# mapping");
                    return false;
            }
        }

        private IReadOnlyList<string> BuildRules(
            SimpleJsonObject? schema,
            OpenApiImportType type,
            bool required,
            string context)
        {
            var rules = new List<string>();
            if (schema is null)
            {
                if (!required)
                {
                    rules.Add("Optional()");
                }

                return rules;
            }

            foreach (var constraintName in UnsupportedConstraintNames)
            {
                if (schema.TryGetValue(constraintName, out var unsupported))
                {
                    ReportUnrepresentable(
                        unsupported.Position,
                        context + " constraint '" + constraintName + "'",
                        "Miya.Schema has no equivalent validation rule");
                }
            }

            if (schema.TryGetValue("default", out var defaultValue))
            {
                if (TryFormatDefault(type, defaultValue, out var literal))
                {
                    rules.Add("Default(" + literal + ")");
                }
                else
                {
                    ReportUnrepresentable(
                        defaultValue.Position,
                        context + " default",
                        "the default value cannot be expressed as the generated C# type");
                }
            }

            BuildNumericRules(schema, type, context, rules);
            BuildStringRules(schema, type, context, rules);
            if (!required)
            {
                rules.Add("Optional()");
            }

            return rules;
        }

        private void BuildNumericRules(
            SimpleJsonObject schema,
            OpenApiImportType type,
            string context,
            List<string> rules)
        {
            var hasMinimum = schema.TryGetValue("minimum", out var minimumValue);
            var hasMaximum = schema.TryGetValue("maximum", out var maximumValue);
            var minimumExclusive = false;
            var maximumExclusive = false;

            if (schema.TryGetValue("exclusiveMinimum", out var exclusiveMinimum))
            {
                if (exclusiveMinimum is SimpleJsonBoolean minimumBoolean)
                {
                    minimumExclusive = minimumBoolean.Value;
                    if (minimumExclusive && !hasMinimum)
                    {
                        ReportUnrepresentable(
                            exclusiveMinimum.Position,
                            context + " exclusiveMinimum",
                            "the Boolean OpenAPI 3.0 form also requires minimum");
                    }
                }
                else if (exclusiveMinimum is SimpleJsonNumber)
                {
                    minimumValue = exclusiveMinimum;
                    hasMinimum = true;
                    minimumExclusive = true;
                }
                else
                {
                    ReportUnrepresentable(
                        exclusiveMinimum.Position,
                        context + " exclusiveMinimum",
                        "exclusiveMinimum must be a number or Boolean");
                }
            }

            if (schema.TryGetValue("exclusiveMaximum", out var exclusiveMaximum))
            {
                if (exclusiveMaximum is SimpleJsonBoolean maximumBoolean)
                {
                    maximumExclusive = maximumBoolean.Value;
                    if (maximumExclusive && !hasMaximum)
                    {
                        ReportUnrepresentable(
                            exclusiveMaximum.Position,
                            context + " exclusiveMaximum",
                            "the Boolean OpenAPI 3.0 form also requires maximum");
                    }
                }
                else if (exclusiveMaximum is SimpleJsonNumber)
                {
                    maximumValue = exclusiveMaximum;
                    hasMaximum = true;
                    maximumExclusive = true;
                }
                else
                {
                    ReportUnrepresentable(
                        exclusiveMaximum.Position,
                        context + " exclusiveMaximum",
                        "exclusiveMaximum must be a number or Boolean");
                }
            }

            if (!hasMinimum && !hasMaximum)
            {
                return;
            }

            if (!IsNumericType(type))
            {
                ReportUnrepresentable(
                    (minimumValue ?? maximumValue)!.Position,
                    context + " numeric constraints",
                    "Min, Max, and Range require a numeric generated type");
                return;
            }

            string? minimum = null;
            string? maximum = null;
            if (hasMinimum && !TryFormatBoundary(type, minimumValue!, minimumExclusive, increment: true, out minimum))
            {
                ReportUnrepresentable(
                    minimumValue!.Position,
                    context + " minimum",
                    "the boundary cannot be expressed as the generated C# type");
            }

            if (hasMaximum && !TryFormatBoundary(type, maximumValue!, maximumExclusive, increment: false, out maximum))
            {
                ReportUnrepresentable(
                    maximumValue!.Position,
                    context + " maximum",
                    "the boundary cannot be expressed as the generated C# type");
            }

            if (minimum is not null && maximum is not null)
            {
                rules.Add("Range(" + minimum + ", " + maximum + ")");
            }
            else if (minimum is not null)
            {
                rules.Add("Min(" + minimum + ")");
            }
            else if (maximum is not null)
            {
                rules.Add("Max(" + maximum + ")");
            }
        }

        private void BuildStringRules(
            SimpleJsonObject schema,
            OpenApiImportType type,
            string context,
            List<string> rules)
        {
            var hasMinimum = schema.TryGetValue("minLength", out var minimumValue);
            var hasMaximum = schema.TryGetValue("maxLength", out var maximumValue);
            var hasPattern = schema.TryGetValue("pattern", out var patternValue);
            if (!hasMinimum && !hasMaximum && !hasPattern)
            {
                return;
            }

            if (type.Kind != OpenApiImportTypeKind.String)
            {
                ReportUnrepresentable(
                    (minimumValue ?? maximumValue ?? patternValue)!.Position,
                    context + " string constraints",
                    "length and pattern rules require a string generated type");
                return;
            }

            int? minimum = null;
            int? maximum = null;
            if (hasMinimum)
            {
                if (minimumValue is SimpleJsonNumber minimumNumber
                    && minimumNumber.TryGetInt32(out var minimumLength)
                    && minimumLength >= 0)
                {
                    minimum = minimumLength;
                }
                else
                {
                    ReportUnrepresentable(
                        minimumValue!.Position,
                        context + " minLength",
                        "minLength must be a non-negative 32-bit integer");
                }
            }

            if (hasMaximum)
            {
                if (maximumValue is SimpleJsonNumber maximumNumber
                    && maximumNumber.TryGetInt32(out var maximumLength)
                    && maximumLength >= 0)
                {
                    maximum = maximumLength;
                }
                else
                {
                    ReportUnrepresentable(
                        maximumValue!.Position,
                        context + " maxLength",
                        "maxLength must be a non-negative 32-bit integer");
                }
            }

            if (minimum is not null && maximum is not null)
            {
                if (minimum > maximum)
                {
                    ReportUnrepresentable(
                        schema.Position,
                        context + " length constraints",
                        "minLength cannot be greater than maxLength");
                }
                else
                {
                    rules.Add("Length(" + minimum.Value.ToString(CultureInfo.InvariantCulture) + ", " +
                        maximum.Value.ToString(CultureInfo.InvariantCulture) + ")");
                }
            }
            else if (minimum is not null)
            {
                rules.Add("MinLength(" + minimum.Value.ToString(CultureInfo.InvariantCulture) + ")");
            }
            else if (maximum is not null)
            {
                rules.Add("MaxLength(" + maximum.Value.ToString(CultureInfo.InvariantCulture) + ")");
            }

            if (hasPattern)
            {
                if (patternValue is SimpleJsonString pattern)
                {
                    try
                    {
                        _ = new Regex(pattern.Value);
                        rules.Add("Pattern(" + GeneratedNaming.Literal(pattern.Value) + ")");
                    }
                    catch (ArgumentException)
                    {
                        ReportUnrepresentable(
                            pattern.Position,
                            context + " pattern",
                            "the regular expression is not valid for System.Text.RegularExpressions");
                    }
                }
                else
                {
                    ReportUnrepresentable(
                        patternValue!.Position,
                        context + " pattern",
                        "pattern must be a string");
                }
            }
        }

        private bool TryFormatDefault(
            OpenApiImportType type,
            SimpleJsonValue value,
            out string? literal)
        {
            literal = null;
            if (value is SimpleJsonNull)
            {
                if (type.Nullable || type.Kind is OpenApiImportTypeKind.String
                    or OpenApiImportTypeKind.Object or OpenApiImportTypeKind.Array)
                {
                    literal = "null";
                    return true;
                }

                return false;
            }

            switch (type.Kind)
            {
                case OpenApiImportTypeKind.String when value is SimpleJsonString text:
                    literal = GeneratedNaming.Literal(text.Value);
                    return true;
                case OpenApiImportTypeKind.Boolean when value is SimpleJsonBoolean boolean:
                    literal = boolean.Value ? "true" : "false";
                    return true;
                case OpenApiImportTypeKind.Int32:
                case OpenApiImportTypeKind.Int64:
                case OpenApiImportTypeKind.Single:
                case OpenApiImportTypeKind.Double:
                case OpenApiImportTypeKind.Decimal:
                    return TryFormatNumber(type, value, out literal);
                case OpenApiImportTypeKind.Enum when value is SimpleJsonString enumValue:
                    if (_enumMembers.TryGetValue(type.Name!, out var members))
                    {
                        var member = members.FirstOrDefault(item =>
                            string.Equals(item.Key, enumValue.Value, StringComparison.Ordinal));
                        if (!string.IsNullOrEmpty(member.Value))
                        {
                            literal = type.Name + "." + member.Value;
                            return true;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }

        private bool TryFormatBoundary(
            OpenApiImportType type,
            SimpleJsonValue value,
            bool exclusive,
            bool increment,
            out string? literal)
        {
            literal = null;
            if (!(value is SimpleJsonNumber number))
            {
                return false;
            }

            if (type.Kind == OpenApiImportTypeKind.Int32)
            {
                if (!number.TryGetInt32(out var integer))
                {
                    return false;
                }

                if (exclusive)
                {
                    if (increment && integer == int.MaxValue || !increment && integer == int.MinValue)
                    {
                        return false;
                    }

                    integer += increment ? 1 : -1;
                }

                literal = integer.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (type.Kind == OpenApiImportTypeKind.Int64)
            {
                if (!number.TryGetInt64(out var integer))
                {
                    return false;
                }

                if (exclusive)
                {
                    if (increment && integer == long.MaxValue || !increment && integer == long.MinValue)
                    {
                        return false;
                    }

                    integer += increment ? 1L : -1L;
                }

                literal = integer.ToString(CultureInfo.InvariantCulture) + "L";
                return true;
            }

            // Min and Max are the closest Miya rules for exclusive floating-point bounds.
            return TryFormatNumber(type, value, out literal);
        }

        private static bool TryFormatNumber(
            OpenApiImportType type,
            SimpleJsonValue value,
            out string? literal)
        {
            literal = null;
            if (!(value is SimpleJsonNumber number))
            {
                return false;
            }

            switch (type.Kind)
            {
                case OpenApiImportTypeKind.Int32 when number.TryGetInt32(out var int32):
                    literal = int32.ToString(CultureInfo.InvariantCulture);
                    return true;
                case OpenApiImportTypeKind.Int64 when number.TryGetInt64(out var int64):
                    literal = int64.ToString(CultureInfo.InvariantCulture) + "L";
                    return true;
                case OpenApiImportTypeKind.Single when number.TryGetSingle(out var single):
                    literal = single.ToString("R", CultureInfo.InvariantCulture) + "F";
                    return true;
                case OpenApiImportTypeKind.Double when number.TryGetDouble(out var doubleValue):
                    literal = doubleValue.ToString("R", CultureInfo.InvariantCulture) + "D";
                    return true;
                case OpenApiImportTypeKind.Decimal when number.TryGetDecimal(out var decimalValue):
                    literal = decimalValue.ToString(CultureInfo.InvariantCulture) + "M";
                    return true;
                default:
                    return false;
            }
        }

        private void FilterInvalidDependencies()
        {
            var validNames = new HashSet<string>(
                _declarations.Select(static declaration => declaration.Name),
                StringComparer.Ordinal);
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var declaration in _declarations.ToArray())
                {
                    if (!validNames.Contains(declaration.Name))
                    {
                        continue;
                    }

                    var missing = declaration.Dependencies.FirstOrDefault(dependency =>
                        !string.Equals(dependency, declaration.Name, StringComparison.Ordinal)
                        && !validNames.Contains(dependency));
                    if (missing is null)
                    {
                        continue;
                    }

                    validNames.Remove(declaration.Name);
                    ReportUnrepresentable(
                        _root.Position,
                        declaration.Name,
                        "referenced generated type '" + missing + "' was skipped");
                    changed = true;
                }
            }

            _declarations.RemoveAll(declaration => !validNames.Contains(declaration.Name));
            for (var index = _operations.Count - 1; index >= 0; index--)
            {
                var missing = _operations[index].Dependencies.FirstOrDefault(dependency =>
                    !validNames.Contains(dependency));
                if (missing is null)
                {
                    continue;
                }

                ReportUnrepresentable(
                    _root.Position,
                    _operations[index].Name,
                    "referenced generated type '" + missing + "' was skipped");
                _operations.RemoveAt(index);
            }

            for (var index = _clientOperations.Count - 1; index >= 0; index--)
            {
                var missing = _clientOperations[index].Dependencies.FirstOrDefault(dependency =>
                    !validNames.Contains(dependency));
                if (missing is null)
                {
                    continue;
                }

                ReportUnrepresentable(
                    _root.Position,
                    _clientOperations[index].Name,
                    "referenced generated type '" + missing + "' was skipped");
                _clientOperations.RemoveAt(index);
            }
        }

        private static void EmitDeclaration(CodeWriter writer, OpenApiImportDeclaration declaration)
        {
            OpenApiCodeEmitter.EmitDeclaration(writer, declaration);
        }

        private static void EmitRecord(
            CodeWriter writer,
            string name,
            IReadOnlyList<OpenApiImportProperty> properties)
        {
            OpenApiCodeEmitter.EmitRecord(writer, name, properties);
        }

        private void EmitPaths(CodeWriter writer)
        {
            writer.Open("public static partial class Paths");
            foreach (var path in _paths.OrderBy(static path => path.Key, StringComparer.Ordinal))
            {
                writer.Line(
                    "public const string " + path.Key + " = " + GeneratedNaming.Literal(path.Value) + ";");
            }

            writer.Close();
        }

        private void EmitSchemas(CodeWriter writer)
        {
            writer.Open("public static partial class ApiSchemas");
            foreach (var operation in _operations.OrderBy(static operation => operation.Name, StringComparer.Ordinal))
            {
                writer.Line(
                    "public static readonly global::Miya.Schema.Schema<" + operation.InputName + "> " +
                    operation.Name + " =");
                var schemaFactory =
                    "    global::Miya.Schema.Schemas.For<" + operation.InputName + ">()";
                if (operation.Fields.Count == 0)
                {
                    writer.Line(schemaFactory + ";");
                    continue;
                }

                writer.Line(schemaFactory);

                for (var index = 0; index < operation.Fields.Count; index++)
                {
                    var field = operation.Fields[index];
                    var builder = new StringBuilder();
                    builder.Append("        .");
                    builder.Append(field.Source);
                    builder.Append("(input => input.");
                    builder.Append(field.Identifier);
                    if (field.Source == "Header")
                    {
                        builder.Append(", ");
                        builder.Append(GeneratedNaming.Literal(field.HeaderName!));
                    }

                    if (field.Rules.Count != 0)
                    {
                        builder.Append(", rules => rules.");
                        builder.Append(string.Join(".", field.Rules));
                    }

                    builder.Append(')');
                    builder.Append(index + 1 == operation.Fields.Count ? ";" : string.Empty);
                    writer.Line(builder.ToString());
                }
            }

            writer.Close();
        }

        private bool HasUnsupportedSchemaShape(SimpleJsonObject schema, string context)
        {
            foreach (var name in new[] { "oneOf", "anyOf", "allOf", "not", "if", "then", "else" })
            {
                if (schema.TryGetValue(name, out var value))
                {
                    ReportUnsupportedSchema(
                        value.Position,
                        context,
                        name + " schemas are not supported");
                    return true;
                }
            }

            return false;
        }

        private SimpleJsonObject? EffectiveSchema(SimpleJsonObject schema)
        {
            if (!schema.TryGetValue("$ref", out var referenceValue)
                || !(referenceValue is SimpleJsonString reference)
                || !TryResolveComponentReference(reference.Value, out var componentName)
                || !_componentSchemas.TryGetValue(componentName!, out var referenced))
            {
                return schema;
            }

            return referenced;
        }

        private static IReadOnlyList<SimpleJsonObject> MergeParameters(
            IReadOnlyList<SimpleJsonObject> pathParameters,
            IReadOnlyList<SimpleJsonObject> operationParameters)
        {
            var result = new List<SimpleJsonObject>();
            var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
            Add(pathParameters);
            Add(operationParameters);
            return result;

            void Add(IReadOnlyList<SimpleJsonObject> parameters)
            {
                foreach (var parameter in parameters)
                {
                    var name = GetOptionalString(parameter, "name") ?? string.Empty;
                    var source = GetOptionalString(parameter, "in") ?? string.Empty;
                    var key = source + "\0" + name;
                    if (indexes.TryGetValue(key, out var index))
                    {
                        result[index] = parameter;
                    }
                    else
                    {
                        indexes.Add(key, result.Count);
                        result.Add(parameter);
                    }
                }
            }
        }

        private bool TryReadParameterArray(
            SimpleJsonObject owner,
            string propertyName,
            string context,
            out IReadOnlyList<SimpleJsonObject> parameters)
        {
            var result = new List<SimpleJsonObject>();
            parameters = result;
            if (!owner.TryGetValue(propertyName, out var value))
            {
                return true;
            }

            if (!(value is SimpleJsonArray array))
            {
                ReportInvalid(value.Position, context + " parameters must be an array");
                return false;
            }

            foreach (var item in array.Items)
            {
                if (!(item is SimpleJsonObject parameter))
                {
                    ReportInvalid(item.Position, context + " contains a parameter that is not an object");
                    return false;
                }

                result.Add(parameter);
            }

            return true;
        }

        private bool TryReadRequiredNames(
            SimpleJsonObject schema,
            string context,
            out HashSet<string> required)
        {
            required = new HashSet<string>(StringComparer.Ordinal);
            if (!schema.TryGetValue("required", out var value))
            {
                return true;
            }

            if (!(value is SimpleJsonArray array))
            {
                ReportUnsupportedSchema(value.Position, context, "required must be an array of property names");
                return false;
            }

            foreach (var item in array.Items)
            {
                if (!(item is SimpleJsonString name) || !required.Add(name.Value))
                {
                    ReportUnsupportedSchema(item.Position, context, "required must contain unique string property names");
                    return false;
                }
            }

            return true;
        }

        private bool TryConvertPath(
            string path,
            int position,
            out string? result,
            out HashSet<string>? routeNames)
        {
            result = null;
            routeNames = new HashSet<string>(StringComparer.Ordinal);
            if (path.Length == 0 || path[0] != '/')
            {
                ReportInvalid(position, "path '" + path + "' must start with '/'");
                return false;
            }

            if (path == "/")
            {
                result = path;
                return true;
            }

            var segments = path.Substring(1).Split(new[] { '/' }, StringSplitOptions.None);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (segment.StartsWith("{", StringComparison.Ordinal)
                    && segment.EndsWith("}", StringComparison.Ordinal)
                    && segment.Length > 2
                    && segment.IndexOf('{', 1) < 0
                    && segment.IndexOf('}') == segment.Length - 1)
                {
                    var name = segment.Substring(1, segment.Length - 2);
                    if (!routeNames.Add(name))
                    {
                        ReportInvalid(position, "path '" + path + "' repeats parameter '{" + name + "}'");
                        return false;
                    }

                    segments[index] = ":" + name;
                }
                else if (segment.IndexOf('{') >= 0 || segment.IndexOf('}') >= 0)
                {
                    ReportUnrepresentable(
                        position,
                        path,
                        "Miya route parameters must occupy a complete path segment");
                    return false;
                }
            }

            result = "/" + string.Join("/", segments);
            return true;
        }

        private bool TryReserveType(string name, string owner, int position)
        {
            if (_typeOwners.TryGetValue(name, out _))
            {
                ReportNameCollision(position, owner, name);
                return false;
            }

            _typeOwners.Add(name, owner);
            return true;
        }

        private bool TryJsonPropertyIdentifier(string jsonName, out string identifier)
        {
            identifier = jsonName;
            if (_input.Naming == JsonNaming.CamelCase && jsonName.Length != 0)
            {
                identifier = char.ToUpperInvariant(jsonName[0]) + jsonName.Substring(1);
            }

            if (!TryExactIdentifier(identifier, out var escaped))
            {
                return false;
            }

            identifier = UnescapeIdentifier(escaped);
            return string.Equals(
                GeneratedNaming.JsonPropertyName(identifier, _input.Naming),
                jsonName,
                StringComparison.Ordinal);
        }

        internal static bool TryExactIdentifier(string value, out string identifier)
        {
            identifier = value;
            if (value.Length == 0 || !IsIdentifierStart(value[0]))
            {
                return false;
            }

            for (var index = 1; index < value.Length; index++)
            {
                if (!IsIdentifierPart(value[index]))
                {
                    return false;
                }
            }

            identifier = EscapeIdentifier(value);
            return true;
        }

        internal static string PublicIdentifier(string value, string fallback)
        {
            var builder = new StringBuilder();
            var uppercase = true;
            foreach (var character in value)
            {
                if (!char.IsLetterOrDigit(character) && character != '_')
                {
                    uppercase = true;
                    continue;
                }

                if (builder.Length == 0 && char.IsDigit(character))
                {
                    builder.Append('_');
                }

                builder.Append(uppercase ? char.ToUpperInvariant(character) : character);
                uppercase = character == '_';
            }

            if (builder.Length == 0)
            {
                return fallback;
            }

            return builder.ToString();
        }

        private static string FallbackOperationName(string method, string path)
        {
            var builder = new StringBuilder(method);
            if (path == "/")
            {
                builder.Append(" root");
            }
            else
            {
                foreach (var segment in path.Substring(1).Split(new[] { '/' }, StringSplitOptions.None))
                {
                    builder.Append(' ');
                    if (segment.StartsWith("{", StringComparison.Ordinal)
                        && segment.EndsWith("}", StringComparison.Ordinal))
                    {
                        builder.Append("by ");
                        builder.Append(segment.Substring(1, segment.Length - 2));
                    }
                    else
                    {
                        builder.Append(segment);
                    }
                }
            }

            return builder.ToString();
        }

        internal static bool TryRenderNamespace(string value, out string rendered)
        {
            rendered = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var segments = value.Split(new[] { '.' }, StringSplitOptions.None);
            for (var index = 0; index < segments.Length; index++)
            {
                if (!TryExactIdentifier(segments[index], out segments[index]))
                {
                    return false;
                }
            }

            rendered = string.Join(".", segments);
            return true;
        }

        private static bool TryResolveComponentReference(string reference, out string? componentName)
        {
            const string prefix = "#/components/schemas/";
            componentName = null;
            if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var encoded = reference.Substring(prefix.Length);
            if (encoded.Length == 0 || encoded.IndexOf('/') >= 0)
            {
                return false;
            }

            componentName = encoded.Replace("~1", "/").Replace("~0", "~");
            return true;
        }

        private static bool IsNullableSchema(SimpleJsonObject schema)
        {
            if (schema.TryGetValue("nullable", out var nullable)
                && nullable is SimpleJsonBoolean boolean
                && boolean.Value)
            {
                return true;
            }

            if (schema.TryGetValue("type", out var type) && type is SimpleJsonArray types)
            {
                return types.Items.Any(item => item is SimpleJsonString text && text.Value == "null");
            }

            return false;
        }

        private static bool TryGetSchemaType(
            SimpleJsonObject schema,
            out string? type,
            out SimpleJsonValue? typeValue)
        {
            type = null;
            typeValue = null;
            if (!schema.TryGetValue("type", out typeValue))
            {
                return false;
            }

            if (typeValue is SimpleJsonString text)
            {
                type = text.Value;
                return true;
            }

            if (typeValue is SimpleJsonArray types)
            {
                foreach (var item in types.Items)
                {
                    if (!(item is SimpleJsonString itemText))
                    {
                        return false;
                    }

                    if (itemText.Value == "null")
                    {
                        continue;
                    }

                    if (type is not null)
                    {
                        return false;
                    }

                    type = itemText.Value;
                }

                return type is not null;
            }

            return false;
        }

        private static bool IsStringEnum(SimpleJsonObject schema) =>
            TryGetArray(schema, "enum", out _)
            && (!TryGetSchemaType(schema, out var type, out _) || type == "string");

        private static bool IsJsonMediaType(string name) =>
            string.Equals(name, "application/json", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

        private static bool IsSuccessStatusCode(string name) =>
            string.Equals(name, "2XX", StringComparison.OrdinalIgnoreCase)
            || (name.Length == 3
                && name[0] == '2'
                && name[1] >= '0' && name[1] <= '9'
                && name[2] >= '0' && name[2] <= '9');

        private static bool IsTextType(OpenApiImportType type) => type.Kind is
            OpenApiImportTypeKind.String
            or OpenApiImportTypeKind.Int32
            or OpenApiImportTypeKind.Int64
            or OpenApiImportTypeKind.Single
            or OpenApiImportTypeKind.Double
            or OpenApiImportTypeKind.Decimal
            or OpenApiImportTypeKind.Boolean
            or OpenApiImportTypeKind.Enum;

        private static bool IsNumericType(OpenApiImportType type) => type.Kind is
            OpenApiImportTypeKind.Int32
            or OpenApiImportTypeKind.Int64
            or OpenApiImportTypeKind.Single
            or OpenApiImportTypeKind.Double
            or OpenApiImportTypeKind.Decimal;

        private static void AddDependencies(OpenApiImportType type, HashSet<string> dependencies)
        {
            foreach (var dependency in type.NamedDependencies())
            {
                dependencies.Add(dependency);
            }
        }

        private static bool TryGetObject(
            SimpleJsonObject owner,
            string name,
            out SimpleJsonObject? value)
        {
            value = null;
            return owner.TryGetValue(name, out var candidate)
                && (value = candidate as SimpleJsonObject) is not null;
        }

        private static bool TryGetArray(
            SimpleJsonObject owner,
            string name,
            out SimpleJsonArray? value)
        {
            value = null;
            return owner.TryGetValue(name, out var candidate)
                && (value = candidate as SimpleJsonArray) is not null;
        }

        private static bool TryGetString(
            SimpleJsonObject owner,
            string name,
            out string? value,
            out SimpleJsonValue? source)
        {
            value = null;
            source = null;
            if (!owner.TryGetValue(name, out source) || !(source is SimpleJsonString text))
            {
                return false;
            }

            value = text.Value;
            return true;
        }

        private static string? GetOptionalString(SimpleJsonObject owner, string name) =>
            owner.TryGetValue(name, out var value) && value is SimpleJsonString text
                ? text.Value
                : null;

        private static bool GetBoolean(SimpleJsonObject owner, string name, bool defaultValue) =>
            owner.TryGetValue(name, out var value) && value is SimpleJsonBoolean boolean
                ? boolean.Value
                : defaultValue;

        private static string EscapeIdentifier(string value) =>
            SyntaxFacts.GetKeywordKind(value) == SyntaxKind.None ? value : "@" + value;

        private static string UnescapeIdentifier(string value) =>
            value.StartsWith("@", StringComparison.Ordinal) ? value.Substring(1) : value;

        private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';

        private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

        private void ReportInvalid(int position, string reason)
        {
            _diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.InvalidOpenApiDocument,
                CreateLocation(_input, position),
                _input.Path,
                reason));
        }

        private void ReportUnsupportedSchema(int position, string context, string reason)
        {
            _diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.UnsupportedOpenApiSchema,
                CreateLocation(_input, position),
                context,
                reason));
        }

        private void ReportUnrepresentable(int position, string context, string reason)
        {
            _diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.UnrepresentableOpenApiItem,
                CreateLocation(_input, position),
                context,
                reason));
        }

        private void ReportNameCollision(int position, string openApiName, string generatedName)
        {
            _diagnostics.Add(Diagnostic.Create(
                DiagnosticCatalog.OpenApiNameCollision,
                CreateLocation(_input, position),
                openApiName,
                generatedName));
        }
    }
}
