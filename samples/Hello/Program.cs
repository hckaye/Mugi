using System.Diagnostics;
using System.Globalization;
using Mugi;
using Mugi.Json;
using Mugi.Schema;

var app = new App();

app.Use(static async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    await next(context);
    context.Header(
        "Server-Timing",
        $"app;dur={stopwatch.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}");
});

app.Get("/", static context => context.Text("Hello"));
app.Get("/users/:id", static context => context.Json(new User(context.Param("id"))));
app.Post("/users", static async context =>
{
    var user = await context.Req.Json<User>()
        ?? throw new JsonException("A user is required.");
    await context.Json(user);
});

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Limit, rules => rules.Default(20).Range(1, 100));
app.Get(
    "/search/:Page",
    searchSchema,
    static (context, input) => context.Json(input));

var createPersonSchema = Schemas.For<CreatePersonInput>()
    .Body(input => input.Name, rules => rules.NotEmpty().MaxLength(80).Pattern("^[A-Za-z ]+$"))
    .Body(input => input.Age, rules => rules.Range(0, 120))
    .Body(input => input.Note, rules => rules.Optional().MaxLength(200));
app.Post(
    "/people",
    createPersonSchema,
    static (context, input) => context.Json(input));

app.Run();

internal sealed record User(string Id);

internal sealed record SearchInput(int Page, string Query, int Limit);

internal sealed record CreatePersonInput(string Name, int Age, string? Note);
