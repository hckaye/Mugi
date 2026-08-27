using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Miya.Generators.Tests;

public sealed class InterceptorTests
{
    [Fact]
    public void Json_and_route_calls_are_rewritten_to_generated_interceptors()
    {
        const string source = """
            using System.Threading.Tasks;
            using Miya;

            public sealed record Payload(int Id);
            public static class Calls
            {
                public static ValueTask Write(Context context) => context.Json(new Payload(1));
                public static void Register(App app) => app.Get("/items/:id", c => c.Text("ok"));
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var errors = run.Compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        Assert.Contains(
            "[global::System.Runtime.CompilerServices.InterceptsLocationAttribute(",
            run.Source("Miya.Interceptors.g.cs"),
            StringComparison.Ordinal);
        Assert.Contains("RouteTemplates.Route_0", run.Source("Miya.Interceptors.g.cs"), StringComparison.Ordinal);

        var assembly = GeneratorTestHelper.EmitAndLoad(run.Compilation);
        var calls = assembly.GetType("Calls")!;
        Assert.Contains(GetCalledMethods(calls.GetMethod("Write")!), IsGeneratedInterceptor);
        Assert.Contains(GetCalledMethods(calls.GetMethod("Register")!), IsGeneratedInterceptor);
    }

    [Fact]
    public void Generic_app_derived_context_and_explicit_json_type_use_declaring_receivers()
    {
        const string source = """
            using System.Threading.Tasks;
            using Miya;
            using Miya.Json;

            public sealed class MyContext : Context { }
            public sealed record Payload(int Id);
            public static class Calls
            {
                public static void Register(App<MyContext> app) =>
                    app.Get("/generic", c => c.Text("ok"));
                public static ValueTask Write(MyContext context, Payload value) =>
                    context.Json<Payload>(value);
                public static ValueTask Generic<T>(Context context, T value) => context.Json(value);
                public static void Include() => MiyaJson.Include<Payload>();
            }
            """;

        var run = GeneratorTestHelper.Run(GeneratorTestHelper.CreateCompilation(source));
        var errors = run.Compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        var generated = run.Source("Miya.Interceptors.g.cs");
        Assert.Contains("this global::Miya.Context receiver", generated, StringComparison.Ordinal);
        Assert.Contains("this global::Miya.App<global::MyContext> receiver", generated, StringComparison.Ordinal);
    }

    private static bool IsGeneratedInterceptor(MethodBase method)
    {
        return method.DeclaringType?.FullName == "Miya.Generated.MiyaInterceptors";
    }

    private static IEnumerable<MethodBase> GetCalledMethods(MethodInfo method)
    {
        var bytes = method.GetMethodBody()!.GetILAsByteArray()!;
        for (var index = 0; index <= bytes.Length - 5; index++)
        {
            if (bytes[index] is not (0x28 or 0x6F))
            {
                continue;
            }

            var token = BitConverter.ToInt32(bytes, index + 1);
            MethodBase? called = null;
            try
            {
                called = method.Module.ResolveMethod(token);
            }
            catch (ArgumentException)
            {
            }

            if (called is not null)
            {
                yield return called;
            }

            index += 4;
        }
    }
}
