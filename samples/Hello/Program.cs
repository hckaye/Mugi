using System.Diagnostics;
using System.Globalization;
using Miya;
using Miya.Json;

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

app.Run();

internal sealed record User(string Id);
