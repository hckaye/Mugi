namespace Miya.Schema;

/// <summary>
/// Defines reusable binding and validation for fields declared by <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">
/// The interface or base type that declares the fields in the schema part. Interfaces are the
/// typical choice, but any base type accepted by <see cref="SchemaPartExtensions.Use{T, TPart}(Schema{T}, SchemaPart{TPart})"/>
/// can be used.
/// </typeparam>
public sealed class SchemaPart<T>
{
    internal SchemaPart()
    {
    }

    /// <summary>
    /// Maps a field from a route parameter.
    /// </summary>
    /// <typeparam name="F">The field type.</typeparam>
    /// <param name="field">A selector for the field.</param>
    /// <param name="rules">Optional validation and default-value rules.</param>
    /// <returns>This schema part.</returns>
    public SchemaPart<T> Route<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }

    /// <summary>
    /// Maps a field from the query string.
    /// </summary>
    /// <typeparam name="F">The field type.</typeparam>
    /// <param name="field">A selector for the field.</param>
    /// <param name="rules">Optional validation and default-value rules.</param>
    /// <returns>This schema part.</returns>
    public SchemaPart<T> Query<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }

    /// <summary>
    /// Maps a field from the JSON request body.
    /// </summary>
    /// <typeparam name="F">The field type.</typeparam>
    /// <param name="field">A selector for the field.</param>
    /// <param name="rules">Optional validation and default-value rules.</param>
    /// <returns>This schema part.</returns>
    public SchemaPart<T> Body<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }

    /// <summary>
    /// Maps a field from a request header.
    /// </summary>
    /// <typeparam name="F">The field type.</typeparam>
    /// <param name="field">A selector for the field.</param>
    /// <param name="name">The request header name.</param>
    /// <param name="rules">Optional validation and default-value rules.</param>
    /// <returns>This schema part.</returns>
    public SchemaPart<T> Header<F>(Func<T, F> field, string name, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return this;
    }

    /// <summary>
    /// Maps a field from a URL-encoded or multipart form field.
    /// </summary>
    /// <typeparam name="F">The field type.</typeparam>
    /// <param name="field">A selector for the field.</param>
    /// <param name="rules">Optional validation and default-value rules.</param>
    /// <returns>This schema part.</returns>
    public SchemaPart<T> Form<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }
}

/// <summary>
/// Adds reusable schema parts to concrete input schemas.
/// </summary>
public static class SchemaPartExtensions
{
    /// <summary>
    /// Applies a schema part to an input type that implements its interface or derives from its
    /// base type.
    /// </summary>
    /// <typeparam name="T">The concrete input type.</typeparam>
    /// <typeparam name="TPart">The interface or base type declared by the schema part.</typeparam>
    /// <param name="schema">The concrete input schema.</param>
    /// <param name="part">The schema part to apply.</param>
    /// <returns><paramref name="schema"/>.</returns>
    public static Schema<T> Use<T, TPart>(this Schema<T> schema, SchemaPart<TPart> part)
        where T : TPart
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(part);
        return schema;
    }
}
