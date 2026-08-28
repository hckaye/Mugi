# Miya.Schema

Miya.Schema adds typed request input to the [Miya](https://www.nuget.org/packages/Miya) web framework. A schema combines route parameters, query values, headers, form fields, and JSON body fields into one input record, and the handler runs only after parsing and validation succeed.

## Install

```xml
<ItemGroup>
  <PackageReference Include="Miya" Version="0.1.1" />
  <PackageReference Include="Miya.Schema" Version="0.1.1" />
  <PackageReference Include="Miya.Generators" Version="0.1.1" />
</ItemGroup>
```

The `Miya.Generators` package reads the field selectors and rule declarations at build time and generates the binders, so binding works under NativeAOT without reflection.

## Usage

```csharp
using Miya.Schema;

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Limit, rules => rules.Default(20).Range(1, 100));

app.Get("/search/:Page", searchSchema,
    static (c, input) => c.Json(input));

var personSchema = Schemas.For<CreatePersonInput>()
    .Body(input => input.Name, rules => rules.NotEmpty().MaxLength(80))
    .Body(input => input.Age, rules => rules.Range(0, 120))
    .Body(input => input.Note, rules => rules.Optional());

app.Post("/people", personSchema,
    static (c, input) => c.Json(input));

public sealed record SearchInput(int Page, string Query, int Limit);
public sealed record CreatePersonInput(string Name, int Age, string? Note);
```

An explicit `Route`, `Query`, `Body`, `Header`, or `Form` mapping takes precedence. An unmapped field whose name matches a `:parameter` comes from the route; other unmapped fields come from the JSON body for `POST`, `PUT`, and `PATCH`, and from the query string for other methods.

Rules can be chained: `Min`, `Max`, `Range`, `Positive`, and `NonNegative` for numbers; `NotEmpty`, `Length`, `MinLength`, `MaxLength`, and `Pattern` for strings; `Optional`, `Default`, and `Must` for every field. A missing required value, parse failure, invalid JSON body, or failed rule returns 400 with a JSON `errors` array without calling the handler.

Schemas can share parts across input types with `Schemas.Part<TPart>()` and `.Use(part)`, and share rule chains through static methods.

Full documentation is at [github.com/hckaye/Miya](https://github.com/hckaye/Miya).
