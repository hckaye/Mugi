using System.Diagnostics;
using System.Globalization;
using Miya;

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

app.Run();
