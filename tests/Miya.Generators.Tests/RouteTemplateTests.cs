namespace Miya.Generators.Tests;

public sealed class RouteTemplateTests
{
    [Fact]
    public void Precompiled_template_uses_existing_route_registration_path()
    {
        var template = Miya.RouteTemplate.Precompiled(
            "/items/:id",
            ["items", "id"],
            [3, 2],
            [-1, 0],
            ["id"]);
        var app = new Miya.App();

        var built = app.Get(template, context => context.Text("ok")).Build();

        Assert.NotNull(built);
    }
}
