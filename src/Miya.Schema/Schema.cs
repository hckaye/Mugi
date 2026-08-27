using System.ComponentModel;

namespace Miya.Schema;

public static class Schemas
{
    public static Schema<T> For<T>() => new(BinderRegistry<T>.Get());
}

public sealed class Schema<T>
{
    internal Schema(IInputBinder<T> binder)
    {
        Binder = binder;
    }

    internal IInputBinder<T> Binder { get; }

    public Schema<T> Route<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }

    public Schema<T> Query<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }

    public Schema<T> Body<F>(Func<T, F> field, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        return this;
    }

    public Schema<T> Header<F>(Func<T, F> field, string name, Action<Rule<F>>? rules = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return this;
    }
}

public sealed class Rule<F>
{
    internal Rule()
    {
    }

    public Rule<F> Optional() => this;

    public Rule<F> Default(F value) => this;

    public Rule<F> Must(Func<F, bool> predicate, string message)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentException.ThrowIfNullOrEmpty(message);
        return this;
    }

    public Rule<F> Min(F value) => this;

    public Rule<F> Max(F value) => this;

    public Rule<F> Range(F minimum, F maximum) => this;

    public Rule<F> Positive() => this;

    public Rule<F> NonNegative() => this;

    public Rule<F> NotEmpty() => this;

    public Rule<F> Length(int minimum, int maximum) => this;

    public Rule<F> MinLength(int minimum) => this;

    public Rule<F> MaxLength(int maximum) => this;

    public Rule<F> Pattern(string regex)
    {
        ArgumentNullException.ThrowIfNull(regex);
        return this;
    }
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IInputBinder<T>
{
    ValueTask<BindResult<T>> Bind(Context context);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct BindResult<T>
{
    private BindResult(bool success, T value, IReadOnlyList<ValidationError> errors)
    {
        Success = success;
        Value = value;
        Errors = errors;
    }

    public bool Success { get; }

    public T Value { get; }

    public IReadOnlyList<ValidationError> Errors { get; }

    public static BindResult<T> Valid(T value) => new(true, value, Array.Empty<ValidationError>());

    public static BindResult<T> Invalid(IReadOnlyList<ValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new BindResult<T>(false, default!, errors);
    }
}

public sealed record ValidationError(string Field, string Message);

[EditorBrowsable(EditorBrowsableState.Never)]
public static class BinderRegistry<T>
{
    private static IInputBinder<T>? _binder;

    public static void Register(IInputBinder<T> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);
        _binder = binder;
    }

    internal static IInputBinder<T> Get() => _binder ?? throw new InvalidOperationException(
        $"No generated input binder is registered for '{typeof(T)}'. " +
        "Reference Miya.Generators and create the schema with Schemas.For<T>().");
}
