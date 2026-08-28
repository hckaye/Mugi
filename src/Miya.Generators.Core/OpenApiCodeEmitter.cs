namespace Miya.Generators.Core;

internal static class OpenApiCodeEmitter
{
    internal static void EmitDeclaration(CodeWriter writer, OpenApiImportDeclaration declaration) =>
        EmitDeclaration(writer, declaration, avoidDuplicateNullable: false);

    internal static void EmitDeclaration(
        CodeWriter writer,
        OpenApiImportDeclaration declaration,
        bool avoidDuplicateNullable)
    {
        if (declaration is OpenApiImportRecord record)
        {
            EmitRecord(writer, record.Name, record.Properties, avoidDuplicateNullable);
            return;
        }

        var enumDeclaration = (OpenApiImportEnum)declaration;
        writer.Open("public enum " + enumDeclaration.Name);
        for (var index = 0; index < enumDeclaration.Members.Count; index++)
        {
            var suffix = index + 1 == enumDeclaration.Members.Count ? string.Empty : ",";
            writer.Line(enumDeclaration.Members[index].Value + suffix);
        }

        writer.Close();
    }

    internal static void EmitRecord(
        CodeWriter writer,
        string name,
        System.Collections.Generic.IReadOnlyList<OpenApiImportProperty> properties) =>
        EmitRecord(writer, name, properties, avoidDuplicateNullable: false);

    internal static void EmitRecord(
        CodeWriter writer,
        string name,
        System.Collections.Generic.IReadOnlyList<OpenApiImportProperty> properties,
        bool avoidDuplicateNullable)
    {
        if (properties.Count == 0)
        {
            writer.Line("public sealed record " + name + "();");
            return;
        }

        writer.Line("public sealed record " + name + "(");
        for (var index = 0; index < properties.Count; index++)
        {
            var property = properties[index];
            var suffix = index + 1 == properties.Count ? ");" : ",";
            writer.Line(
                "    " + RenderType(property.Type, property.Required, avoidDuplicateNullable)
                + " " + property.Identifier + suffix);
        }
    }

    private static string RenderType(
        OpenApiImportType type,
        bool required,
        bool avoidDuplicateNullable) =>
        avoidDuplicateNullable && type.Nullable && !required
            ? type.Render(required: true)
            : type.Render(required);
}
