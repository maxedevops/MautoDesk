namespace MautoDesk.SharedKernel;

/// <summary>The kind of failure, which maps to an HTTP status at the edge.</summary>
public enum ErrorKind
{
    /// <summary>Well-formed but semantically invalid. 422.</summary>
    Validation,

    /// <summary>Not found, or belongs to another tenant. 404 — see remarks on <see cref="Error.NotFound"/>.</summary>
    NotFound,

    /// <summary>Violates a business rule or an optimistic-concurrency check. 409.</summary>
    Conflict,

    /// <summary>The principal lacks the required permission. 403.</summary>
    Forbidden,

    /// <summary>An upstream dependency failed. 503.</summary>
    Unavailable,
}

/// <summary>A failure with a stable machine-readable code.</summary>
/// <param name="Code">Stable identifier, e.g. <c>vehicle.stock_number.duplicate</c>.</param>
/// <param name="Message">Human-readable text safe to show a user.</param>
/// <param name="Kind">How the edge should surface it.</param>
/// <param name="Field">The offending field, when the error is field-scoped.</param>
public sealed record Error(string Code, string Message, ErrorKind Kind, string? Field = null)
{
    public static Error Validation(string code, string message, string? field = null) =>
        new(code, message, ErrorKind.Validation, field);

    /// <summary>
    /// A missing record.
    /// </summary>
    /// <remarks>
    /// This is also what a cross-tenant identifier produces. Returning 403 for a
    /// resource belonging to another tenant would confirm that the record
    /// exists, which is an information leak; 404 is deliberately
    /// indistinguishable. See docs/04-api-contracts.md §4.
    /// </remarks>
    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorKind.NotFound);

    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorKind.Conflict);

    public static Error Forbidden(string code, string message) =>
        new(code, message, ErrorKind.Forbidden);

    public static Error Unavailable(string code, string message) =>
        new(code, message, ErrorKind.Unavailable);
}

/// <summary>
/// The outcome of an operation that is expected to fail sometimes.
/// </summary>
/// <remarks>
/// Expected failures — a duplicate stock number, an invalid VIN, an invalid
/// status transition — are values, not exceptions. Exceptions remain for the
/// genuinely exceptional: a dropped connection, a bug. This keeps the failure
/// paths visible in the type signature instead of hidden in a catch block, and
/// it means a handler cannot silently forget that an operation can fail.
/// </remarks>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
    }

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    /// <summary>The value. Throws when the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot read Value of a failed result ({Error!.Code}). Check IsSuccess first.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error) => new(error);

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);

    /// <summary>Branches on the outcome without unwrapping by hand.</summary>
    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(Error!);
}

/// <summary>A result carrying no value.</summary>
public readonly struct Result
{
    private Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => new(error);
}
