namespace Mugi.Generators.Tests;

public sealed class RouteTemplateTests
{
    [Fact]
    public void Precompiled_template_uses_existing_route_registration_path()
    {
        var template = Mugi.RouteTemplate.Precompiled(
            "/items/:id",
            ["items", "id"],
            [3, 2],
            [-1, 0],
            ["id"]);
        var app = new Mugi.App();

        var built = app.Get(template, context => context.Text("ok")).Build();

        Assert.NotNull(built);
    }
}
