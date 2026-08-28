using Mugi.Schema;

namespace Mugi.Schema.Tests.Parts;

internal interface IPaging
{
    int Page { get; }

    int PageSize { get; }
}

internal interface IRequestMetadata
{
    string RequestId { get; }
}

internal interface IFormPerson
{
    string Name { get; }

    int Age { get; }
}

internal interface IRouteIdentity
{
    int Id { get; }
}

internal static class SharedSchemaParts
{
    internal static readonly SchemaPart<IPaging> Paging = Schemas.Part<IPaging>()
        .Query(input => input.Page, PagingRules)
        .Query(input => input.PageSize, rules => rules.Default(20).Range(1, 100));

    internal static readonly SchemaPart<IRequestMetadata> RequestMetadata =
        Schemas.Part<IRequestMetadata>()
            .Header(input => input.RequestId, "X-Request-Id", rules => rules.NotEmpty());

    internal static readonly SchemaPart<IFormPerson> FormPerson = Schemas.Part<IFormPerson>()
        .Form(input => input.Name, rules => rules.NotEmpty())
        .Form(input => input.Age, rules => rules.Range(0, 120));

    internal static readonly SchemaPart<IRouteIdentity> RouteIdentity =
        Schemas.Part<IRouteIdentity>()
            .Route(input => input.Id, rules => rules.Positive());

    internal static void PagingRules(Rule<int> rule) =>
        rule.Default(1)
            .Range(1, 50)
            .Must(PagingValidation.IsAllowed, "is not an allowed page");
}

internal static class PagingValidation
{
    internal static bool IsAllowed(int value) => value <= 50;
}
