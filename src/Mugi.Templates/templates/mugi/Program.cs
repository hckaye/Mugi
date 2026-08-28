using Mugi;
using Mugi.Schema;

var app = new App();

app.Get("/", static c => c.Text("Hello from Mugi"));
app.Get("/users/:id", static c => c.Json(new User(c.Param("id"), "Ada")));

var searchSchema = Schemas.For<SearchInput>()
    .Query(input => input.Limit, rules => rules.Default(20).Range(1, 100));

app.Get("/search/:Page", searchSchema,
    static (c, input) => c.Json(input));

app.Run();

public sealed record User(string Id, string Name);
public sealed record SearchInput(int Page, string Query, int Limit);
