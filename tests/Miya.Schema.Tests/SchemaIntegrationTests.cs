using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Miya.Schema;
using Miya.Schema.Tests.ExtensionRules;
using Miya.Testing;

namespace Miya.Schema.Tests;

public sealed class SchemaIntegrationTests
{
    [Fact]
    public async Task Must_lambda_can_call_extension_method_from_an_imported_namespace()
    {
        var app = new App();
        var schema = Schemas.For<ExtensionRuleInput>()
            .Query(input => input.Value, rules =>
                rules.Must(value => value.IsAllowed(), "is not allowed"));
        app.Get("/extension", schema, static (context, input) => context.Text(input.Value));

        var invalid = await app.Request("GET", "/extension?Value=blocked");
        var valid = await app.Request("GET", "/extension?Value=allowed");

        Assert.Equal(400, invalid.Status);
        AssertValidationMessage(invalid, "value", "is not allowed");
        Assert.Equal(200, valid.Status);
        Assert.Equal("allowed", valid.Text());
    }

    [Fact]
    public async Task Route_query_and_default_values_are_bound()
    {
        var app = new App();
        var schema = Schemas.For<SearchInput>()
            .Route(input => input.Id, rules => rules.Positive())
            .Query(input => input.Filter, rules => rules.Optional())
            .Query(input => input.Limit, rules => rules.Default(25).Range(1, 100));
        app.Get(
            "/items/:Id",
            schema,
            static (context, input) => context.Json(input));

        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.GetAsync("/items/42?Filter=active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(42, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("active", body.RootElement.GetProperty("filter").GetString());
        Assert.Equal(25, body.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Urlencoded_form_fields_are_bound_with_optional_defaults_and_rules()
    {
        var app = new App();
        var schema = Schemas.For<UrlEncodedFormInput>()
            .Form(input => input.Name, rules => rules.NotEmpty().MaxLength(40).Must(value => value != "blocked", "is reserved"))
            .Form(input => input.Age, rules => rules.Range(0, 120))
            .Form(input => input.Nickname, rules => rules.Optional())
            .Form(input => input.Country, rules => rules.Default("JP"));
        app.Post("/form", schema, static (context, input) => context.Json(input));

        var response = await app.Request(
            "POST",
            "/form",
            FormRequest(FormBody(("Name", "Ada Lovelace"), ("Age", "37"))));

        Assert.Equal(200, response.Status);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        Assert.Equal("Ada Lovelace", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(37, body.RootElement.GetProperty("age").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("nickname").ValueKind);
        Assert.Equal("JP", body.RootElement.GetProperty("country").GetString());
    }

    [Fact]
    public async Task Multipart_form_fields_are_bound_without_binding_file_parts()
    {
        var app = new App();
        var schema = Schemas.For<MultipartFormInput>()
            .Form(input => input.Name)
            .Form(input => input.Age);
        app.Post("/form", schema, static (context, input) => context.Text(input.Name + ":" + input.Age));

        using var content = new MultipartFormDataContent("schema-boundary");
        content.Add(new StringContent("Ada"), "Name");
        content.Add(new StringContent("42"), "Age");
        using var file = new ByteArrayContent("ignored"u8.ToArray());
        content.Add(file, "upload", "ignored.txt");
        var response = await app.Request(
            "POST",
            "/form",
            new TestRequestOptions
            {
                Body = await content.ReadAsByteArrayAsync(),
                Headers =
                [
                    new KeyValuePair<string, string>(
                        "Content-Type",
                        content.Headers.ContentType!.ToString()),
                ],
            });

        Assert.Equal(200, response.Status);
        Assert.Equal("Ada:42", response.Text());
    }

    [Fact]
    public async Task Missing_and_invalid_form_fields_return_structured_400_without_calling_handler()
    {
        var called = false;
        var app = new App();
        var schema = Schemas.For<FormValidationInput>()
            .Form(input => input.Name, rules => rules.NotEmpty())
            .Form(input => input.Age, rules => rules.Range(0, 120));
        app.Post(
            "/form",
            schema,
            (context, input) =>
            {
                called = true;
                return context.Text(input.Name);
            });

        var response = await app.Request(
            "POST",
            "/form",
            FormRequest(FormBody(("Age", "not-a-number"))));

        Assert.Equal(400, response.Status);
        Assert.Equal("application/json", response.Header("Content-Type"));
        Assert.False(called);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        var errors = body.RootElement.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Contains(
            errors,
            static error => error.GetProperty("field").GetString() == "age"
                && error.GetProperty("message").GetString() == "has an invalid value");
        Assert.Contains(
            errors,
            static error => error.GetProperty("field").GetString() == "name"
                && error.GetProperty("message").GetString() == "is required");
    }

    [Fact]
    public async Task Form_parser_errors_return_one_structured_error_without_calling_handler()
    {
        var called = false;
        var app = new App();
        var schema = Schemas.For<FormErrorInput>().Form(input => input.Name);
        app.Post(
            "/form",
            schema,
            (context, input) =>
            {
                called = true;
                return context.Text(input.Name);
            });

        var response = await app.Request(
            "POST",
            "/form",
            FormRequest("Name=%GG", "application/x-www-form-urlencoded"));

        Assert.Equal(400, response.Status);
        Assert.False(called);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(string.Empty, error.GetProperty("field").GetString());
        Assert.Equal(
            "The URL-encoded form contains an invalid percent escape.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Wrong_form_content_type_returns_structured_400_without_calling_handler()
    {
        var called = false;
        var app = new App();
        var schema = Schemas.For<FormContentTypeInput>().Form(input => input.Name);
        app.Post(
            "/form",
            schema,
            (context, input) =>
            {
                called = true;
                return context.Text(input.Name);
            });

        var response = await app.Request(
            "POST",
            "/form",
            FormRequest("Name=Ada", "application/json"));

        Assert.Equal(400, response.Status);
        Assert.False(called);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(string.Empty, error.GetProperty("field").GetString());
        Assert.Equal(
            "The request Content-Type is not a supported form media type.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Form_limits_return_a_structured_error_without_calling_handler()
    {
        var called = false;
        var app = new App();
        var schema = Schemas.For<FormLimitInput>().Form(input => input.Name);
        app.Post(
            "/form",
            schema,
            (context, input) =>
            {
                called = true;
                return context.Text(input.Name);
            });

        await using var server = await app.StartAsync(new AppOptions
        {
            Port = 0,
            MaxFormBodyBytes = 4,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        using var client = Client(server);
        using var content = new StringContent(
            "Name=Ada",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");
        using var response = await client.PostAsync("/form", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(called);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal(string.Empty, error.GetProperty("field").GetString());
        Assert.Equal(
            "The form body exceeds the configured limit of 4 bytes.",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Form_binding_supports_all_scalar_text_types_and_nullable_values()
    {
        var id = "01234567-89ab-cdef-0123-456789abcdef";
        var app = new App();
        var schema = Schemas.For<AllFormTypesInput>()
            .Form(input => input.StringValue)
            .Form(input => input.CharValue)
            .Form(input => input.EnumValue)
            .Form(input => input.GuidValue)
            .Form(input => input.DateValue)
            .Form(input => input.OffsetValue)
            .Form(input => input.BooleanValue)
            .Form(input => input.ByteValue)
            .Form(input => input.SByteValue)
            .Form(input => input.Int16Value)
            .Form(input => input.UInt16Value)
            .Form(input => input.Int32Value)
            .Form(input => input.UInt32Value)
            .Form(input => input.Int64Value)
            .Form(input => input.UInt64Value)
            .Form(input => input.SingleValue)
            .Form(input => input.DoubleValue)
            .Form(input => input.DecimalValue)
            .Form(input => input.NullableValue);
        app.Post("/all", schema, static (context, input) => context.Json(input));

        var response = await app.Request(
            "POST",
            "/all",
            FormRequest(FormBody(
                ("StringValue", "hello world"),
                ("CharValue", "X"),
                ("EnumValue", "Ready"),
                ("GuidValue", id),
                ("DateValue", "2026-08-28T05:21:42.1234567Z"),
                ("OffsetValue", "2026-08-28T05:21:42.1234567+09:00"),
                ("BooleanValue", "true"),
                ("ByteValue", "1"),
                ("SByteValue", "-2"),
                ("Int16Value", "-3"),
                ("UInt16Value", "4"),
                ("Int32Value", "-5"),
                ("UInt32Value", "6"),
                ("Int64Value", "-7"),
                ("UInt64Value", "8"),
                ("SingleValue", "1.5"),
                ("DoubleValue", "-2.5e2"),
                ("DecimalValue", "3.25"),
                ("NullableValue", "9"))));

        Assert.Equal(200, response.Status);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        Assert.Equal("hello world", body.RootElement.GetProperty("stringValue").GetString());
        Assert.Equal("X", body.RootElement.GetProperty("charValue").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("enumValue").GetInt32());
        Assert.Equal(id, body.RootElement.GetProperty("guidValue").GetString());
        Assert.True(body.RootElement.GetProperty("booleanValue").GetBoolean());
        Assert.Equal(1, body.RootElement.GetProperty("byteValue").GetByte());
        Assert.Equal(-2, body.RootElement.GetProperty("sByteValue").GetSByte());
        Assert.Equal(-3, body.RootElement.GetProperty("int16Value").GetInt16());
        Assert.Equal(4, body.RootElement.GetProperty("uInt16Value").GetUInt16());
        Assert.Equal(-5, body.RootElement.GetProperty("int32Value").GetInt32());
        Assert.Equal((uint)6, body.RootElement.GetProperty("uInt32Value").GetUInt32());
        Assert.Equal((long)-7, body.RootElement.GetProperty("int64Value").GetInt64());
        Assert.Equal((ulong)8, body.RootElement.GetProperty("uInt64Value").GetUInt64());
        Assert.Equal(1.5f, body.RootElement.GetProperty("singleValue").GetSingle());
        Assert.Equal(-250d, body.RootElement.GetProperty("doubleValue").GetDouble());
        Assert.Equal(3.25m, body.RootElement.GetProperty("decimalValue").GetDecimal());
        Assert.Equal(9, body.RootElement.GetProperty("nullableValue").GetInt32());
    }

    [Fact]
    public async Task Body_fields_are_bound_and_validated()
    {
        var app = CreatePersonApp(out _);
        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.PostAsJsonAsync("/people", new { name = "Ada", age = 37 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("Ada", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(37, body.RootElement.GetProperty("age").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task Shared_body_rule_method_validates_a_nested_record_with_both_messages()
    {
        var app = new App();
        var schema = Schemas.For<ScratchBazInput>()
            .Body(input => input.Foo, FooCommonRules);
        app.Post("/scratch", schema, static (context, input) => context.Json(input));

        var response = await app.Request(
            "POST",
            "/scratch",
            JsonRequest("""{"foo":{"name":"","count":-1}}"""));

        Assert.Equal(400, response.Status);
        using var body = JsonDocument.Parse(response.Body.ToArray());
        var errors = body.RootElement.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Contains(
            errors,
            static error => error.GetProperty("field").GetString() == "foo"
                && error.GetProperty("message").GetString() == "name must not be empty");
        Assert.Contains(
            errors,
            static error => error.GetProperty("field").GetString() == "foo"
                && error.GetProperty("message").GetString() == "count must be non-negative");
    }

    [Fact]
    public async Task Shared_rule_method_can_be_reused_by_two_schemas()
    {
        var app = new App();
        var firstSchema = Schemas.For<FirstSharedRuleInput>()
            .Query(input => input.Name, SharedNameRules);
        var secondSchema = Schemas.For<SecondSharedRuleInput>()
            .Query(input => input.Name, SharedNameRules);
        app.Get("/first-shared", firstSchema, static (context, input) => context.Text(input.Name));
        app.Get("/second-shared", secondSchema, static (context, input) => context.Text(input.Name));

        var first = await app.Request("GET", "/first-shared?Name=");
        var second = await app.Request("GET", "/second-shared?Name=");

        Assert.Equal(400, first.Status);
        Assert.Equal(400, second.Status);
        AssertValidationMessage(first, "name", "must not be empty");
        AssertValidationMessage(second, "name", "must not be empty");
    }

    [Fact]
    public async Task Query_and_form_accept_expression_and_block_bodied_rule_methods()
    {
        var app = new App();
        var querySchema = Schemas.For<MethodRuleQueryInput>()
            .Query(input => input.Count, QueryCountRules);
        var formSchema = Schemas.For<MethodRuleFormInput>()
            .Form(input => input.Name, FormNameRules);
        app.Get("/method-query", querySchema, static (context, input) => context.Text(input.Count.ToString()));
        app.Post("/method-form", formSchema, static (context, input) => context.Text(input.Name));

        var query = await app.Request("GET", "/method-query?Count=11");
        var form = await app.Request(
            "POST",
            "/method-form",
            FormRequest(FormBody(("Name", "too-long"))));

        Assert.Equal(400, query.Status);
        Assert.Equal(400, form.Status);
        AssertValidationMessage(query, "count", "must be between 0 and 10");
        AssertValidationMessage(form, "name", "length must be at most 5");
    }

    [Fact]
    public async Task Range_failure_returns_structured_400_without_calling_handler()
    {
        var app = CreatePersonApp(out var handlerState);
        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.PostAsJsonAsync("/people", new { name = "Ada", age = 121 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(handlerState.Called);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("age", error.GetProperty("field").GetString());
        Assert.Equal("must be between 0 and 120", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("{\"name\":\"Ada\",\"age\":\"old\"}")]
    [InlineData("{\"age\":37}")]
    [InlineData("{\"name\":\"Ada\",\"age\":")]
    public async Task Invalid_type_missing_required_field_and_invalid_json_return_400(string json)
    {
        var app = CreatePersonApp(out var handlerState);
        await using var server = await Start(app);
        using var client = Client(server);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/people", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(handlerState.Called);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.NotEmpty(body.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task Typed_context_receives_validated_input()
    {
        var app = new App<TestContext>();
        var schema = Schemas.For<HeaderInput>()
            .Header(input => input.RequestId, "X-Request-Id");
        app.Get(
            "/header",
            schema,
            static (context, input) =>
            {
                context.Seen = input.RequestId;
                return context.Text(input.RequestId.ToString("D"));
            });

        await using var server = await Start(app);
        using var client = Client(server);
        var id = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/header");
        request.Headers.Add("X-Request-Id", id.ToString("D"));
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(id.ToString("D"), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Pattern_rules_match_and_reject_values_without_unbounded_backtracking()
    {
        var app = new App();
        var schema = Schemas.For<PatternInput>()
            .Query(input => input.Simple, rules => rules.Pattern("^[a-z]+$"))
            .Query(input => input.Lookahead, rules => rules.Pattern("^(?=a)a+$"))
            .Query(input => input.Catastrophic, rules => rules.Pattern("^(?=a)(a+)+$"));
        app.Get("/patterns", schema, static (context, input) => context.Text(input.Simple));

        await using var server = await Start(app);
        using var client = Client(server);
        using (var valid = await client.GetAsync(
                   "/patterns?Simple=alpha&Lookahead=aaa&Catastrophic=aaa"))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        }

        using (var invalid = await client.GetAsync(
                   "/patterns?Simple=123&Lookahead=aaa&Catastrophic=aaa"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var body = JsonDocument.Parse(await invalid.Content.ReadAsByteArrayAsync());
            Assert.Contains(
                body.RootElement.GetProperty("errors").EnumerateArray(),
                static error => error.GetProperty("field").GetString() == "simple");
        }

        var hostile = new string('a', 4_000) + "!";
        var stopwatch = Stopwatch.StartNew();
        using var timedOut = await client.GetAsync(
            "/patterns?Simple=alpha&Lookahead=aaa&Catastrophic=" + hostile);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.BadRequest, timedOut.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), stopwatch.Elapsed.ToString());
        using var timeoutBody = JsonDocument.Parse(await timedOut.Content.ReadAsByteArrayAsync());
        Assert.Contains(
            timeoutBody.RootElement.GetProperty("errors").EnumerateArray(),
            static error => error.GetProperty("field").GetString() == "catastrophic");
    }

    [Fact]
    public async Task Strict_text_values_accept_sign_exponent_and_iso_date_formats()
    {
        var app = StrictTextApp();
        await using var server = await Start(app);
        using var client = Client(server);
        var datePairs = new[]
        {
            ("2026-08-28", "2026-08-28"),
            ("2026-08-28Z", "2026-08-28Z"),
            ("2026-08-28T05:21:42", "2026-08-28T05:21:42.1234567"),
            ("2026-08-28T05:21:42Z", "2026-08-28T05:21:42+09:00"),
            ("2026-08-28T05:21:42.1234567+09:00", "2026-08-28T05:21:42.1234567Z"),
        };

        foreach (var pair in datePairs)
        {
            using var response = await client.GetAsync(StrictQuery(
                ("Date", pair.Item1),
                ("Offset", pair.Item2)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Strict_text_values_reject_lenient_and_malformed_forms()
    {
        var app = StrictTextApp();
        await using var server = await Start(app);
        using var client = Client(server);
        var invalidValues = new[]
        {
            ("Integer", "1,000"),
            ("Integer", "(5)"),
            ("Integer", " 5 "),
            ("Integer", "1e3"),
            ("Integer", "0x10"),
            ("Integer", ""),
            ("Integer", "garbage"),
            ("Floating", "1,000"),
            ("Floating", "(5)"),
            ("Floating", " 5 "),
            ("Floating", "0x10"),
            ("Floating", ""),
            ("Floating", "garbage"),
            ("Decimal", "1,000"),
            ("Decimal", "(5)"),
            ("Decimal", " 5 "),
            ("Decimal", "1e3"),
            ("Decimal", "0x10"),
            ("Decimal", ""),
            ("Decimal", "garbage"),
            ("Date", "8/28/2026"),
            ("Date", " 2026-08-28 "),
            ("Date", ""),
            ("Date", "garbage"),
            ("Offset", "8/28/2026"),
            ("Offset", " 2026-08-28 "),
            ("Offset", ""),
            ("Offset", "garbage"),
        };

        foreach (var invalid in invalidValues)
        {
            using var response = await client.GetAsync(StrictQuery(invalid));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Contains(
                body.RootElement.GetProperty("errors").EnumerateArray(),
                error => error.GetProperty("field").GetString() ==
                    char.ToLowerInvariant(invalid.Item1[0]) + invalid.Item1[1..]);
        }
    }

    [Fact]
    public async Task Non_finite_floating_point_values_are_rejected_from_all_text_sources()
    {
        var app = new App();
        var schema = Schemas.For<FiniteValuesInput>()
            .Route(input => input.RouteValue, rules => rules.Range(-100D, 100D))
            .Query(input => input.QueryValue, rules => rules.Range(-100D, 100D))
            .Header(input => input.HeaderValue, "X-Value", rules => rules.Range(-100D, 100D))
            .Form(input => input.FormValue, rules => rules.Range(-100D, 100D));
        app.Post(
            "/finite/:RouteValue",
            schema,
            static (context, input) => context.Text("ok"));

        foreach (var nonFinite in new[] { "NaN", "Infinity" })
        {
            foreach (var source in new[] { "route", "query", "header", "form" })
            {
                var route = source == "route" ? nonFinite : "1";
                var query = source == "query" ? nonFinite : "2";
                var header = source == "header" ? nonFinite : "3";
                var form = source == "form" ? nonFinite : "4";
                var response = await app.Request(
                    "POST",
                    "/finite/" + route + "?QueryValue=" + query,
                    new TestRequestOptions
                    {
                        TextBody = FormBody(("FormValue", form)),
                        Headers =
                        [
                            new KeyValuePair<string, string>("Content-Type", "application/x-www-form-urlencoded"),
                            new KeyValuePair<string, string>("X-Value", header),
                        ],
                    });

                Assert.Equal(400, response.Status);
            }
        }

        var finite = await app.Request(
            "POST",
            "/finite/1?QueryValue=2",
            new TestRequestOptions
            {
                TextBody = FormBody(("FormValue", "4")),
                Headers =
                [
                    new KeyValuePair<string, string>("Content-Type", "application/x-www-form-urlencoded"),
                    new KeyValuePair<string, string>("X-Value", "3"),
                ],
            });
        Assert.Equal(200, finite.Status);
    }

    [Fact]
    public async Task Schema_fields_remain_required_when_record_constructor_has_a_default()
    {
        var app = new App();
        var schema = Schemas.For<SchemaDefaultInput>();
        app.Get("/default", schema, static (context, input) => context.Text(input.Value.ToString()));

        await using var server = await Start(app);
        using var client = Client(server);
        using var response = await client.GetAsync("/default");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var error = Assert.Single(body.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Equal("value", error.GetProperty("field").GetString());
        Assert.Equal("is required", error.GetProperty("message").GetString());
    }

    private static App CreatePersonApp(out HandlerState state)
    {
        var app = new App();
        state = new HandlerState();
        var captured = state;
        var schema = Schemas.For<PersonInput>()
            .Body(input => input.Name, rules => rules.NotEmpty())
            .Body(input => input.Age, rules => rules.Range(0, 120))
            .Body(input => input.Note, rules => rules.Optional());
        app.Post(
            "/people",
            schema,
            async (context, input) =>
            {
                captured.Called = true;
                await context.Json(input);
            });
        return app;
    }

    private static App StrictTextApp()
    {
        var app = new App();
        var schema = Schemas.For<StrictTextInput>();
        app.Get("/strict", schema, static (context, input) => context.Text(input.Integer.ToString()));
        return app;
    }

    private static string StrictQuery(params (string Name, string Value)[] replacements)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Integer"] = "+5",
            ["Floating"] = "1e3",
            ["Decimal"] = "+5.25",
            ["Date"] = "2026-08-28T05:21:42.1234567Z",
            ["Offset"] = "2026-08-28T05:21:42.1234567+09:00",
        };
        foreach (var replacement in replacements)
        {
            values[replacement.Name] = replacement.Value;
        }

        var query = new StringBuilder("/strict?");
        foreach (var value in values)
        {
            if (query[^1] != '?')
            {
                query.Append('&');
            }

            query.Append(value.Key);
            query.Append('=');
            query.Append(Uri.EscapeDataString(value.Value));
        }

        return query.ToString();
    }

    private static async Task<Server> Start<C>(App<C> app)
        where C : Context, new() => await app.StartAsync(new AppOptions
        {
            Port = 0,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

    private static HttpClient Client(Server server) => new()
    {
        BaseAddress = new Uri(server.Addresses[0]),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static TestRequestOptions FormRequest(string body, string? contentType = "application/x-www-form-urlencoded") =>
        new()
        {
            TextBody = body,
            Headers = contentType is null
                ? null
                : [new KeyValuePair<string, string>("Content-Type", contentType)],
        };

    private static TestRequestOptions JsonRequest(string body) => new()
    {
        TextBody = body,
        Headers = [new KeyValuePair<string, string>("Content-Type", "application/json")],
    };

    private static void AssertValidationMessage(TestResponse response, string field, string message)
    {
        using var body = JsonDocument.Parse(response.Body.ToArray());
        Assert.Contains(
            body.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == field
                && error.GetProperty("message").GetString() == message);
    }

    private static string FormBody(params (string Name, string Value)[] fields)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < fields.Length; index++)
        {
            if (index != 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(fields[index].Name));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(fields[index].Value));
        }

        return builder.ToString();
    }

    private sealed class HandlerState
    {
        internal bool Called { get; set; }
    }

    internal static void FooCommonRules(Rule<ScratchFoo> rule) =>
        rule.Must(ScratchFooChecks.NameOk, "name must not be empty")
            .Must(ScratchFooChecks.CountOk, "count must be non-negative");

    internal static void SharedNameRules(Rule<string> rule) => rule.NotEmpty();

    internal static void QueryCountRules(Rule<int> rule) => rule.Range(0, 10);

    internal static void FormNameRules(Rule<string> rule)
    {
        rule.NotEmpty().MaxLength(5);
    }

    internal static class ScratchFooChecks
    {
        internal static bool NameOk(ScratchFoo value) => value.Name.Length != 0;

        internal static bool CountOk(ScratchFoo value) => value.Count >= 0;
    }

    internal sealed record ScratchFoo(string Name, int Count);

    internal sealed record ScratchBazInput(ScratchFoo Foo);

    internal sealed record FirstSharedRuleInput(string Name);

    internal sealed record SecondSharedRuleInput(string Name);

    internal sealed record MethodRuleQueryInput(int Count);

    internal sealed record MethodRuleFormInput(string Name);

    internal sealed record SearchInput(int Id, string? Filter, int Limit);

    internal sealed record PersonInput(string Name, int Age, string? Note);

    internal sealed record HeaderInput(Guid RequestId);

    internal sealed record PatternInput(string Simple, string Lookahead, string Catastrophic);

    internal sealed record StrictTextInput(
        int Integer,
        double Floating,
        decimal Decimal,
        DateTime Date,
        DateTimeOffset Offset);

    internal sealed record ExtensionRuleInput(string Value);

    internal sealed record FiniteValuesInput(
        double RouteValue,
        double QueryValue,
        double HeaderValue,
        double FormValue);

    internal sealed record SchemaDefaultInput(int Value = 42);

    internal sealed record UrlEncodedFormInput(string Name, int Age, string? Nickname, string Country);

    internal sealed record MultipartFormInput(string Name, int Age);

    internal sealed record FormValidationInput(string Name, int Age);

    internal sealed record FormErrorInput(string Name);

    internal sealed record FormContentTypeInput(string Name);

    internal sealed record FormLimitInput(string Name);

    internal enum FormState
    {
        Ready,
    }

    internal sealed record AllFormTypesInput(
        string StringValue,
        char CharValue,
        FormState EnumValue,
        Guid GuidValue,
        DateTime DateValue,
        DateTimeOffset OffsetValue,
        bool BooleanValue,
        byte ByteValue,
        sbyte SByteValue,
        short Int16Value,
        ushort UInt16Value,
        int Int32Value,
        uint UInt32Value,
        long Int64Value,
        ulong UInt64Value,
        float SingleValue,
        double DoubleValue,
        decimal DecimalValue,
        int? NullableValue);

    public sealed class TestContext : Context
    {
        internal Guid Seen { get; set; }
    }
}
