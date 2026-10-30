namespace MyAndroidPhone.Core.Results;

public enum ErrorKind
{
    InvalidInput,
    NotFound,
    Unsupported,
    AccessDenied,
    Cancelled,
    Timeout,
    ProcessFailure,
    Io,
    Unknown
}

public sealed record OperationError(ErrorKind Kind, string Message, string? Detail = null);

/// <summary>
/// Outcome of an operation that can fail for expected reasons. Callers must look at
/// <see cref="Succeeded"/>; a result is never "success" just because nothing threw.
/// </summary>
public sealed class OperationResult<T>
{
    private readonly T? _value;

    private OperationResult(T? value, OperationError? error)
    {
        _value = value;
        Error = error;
    }

    public OperationError? Error { get; }

    public bool Succeeded => Error is null;

    public string ErrorMessage => Error?.Message ?? string.Empty;

    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException("Cannot read the value of a failed operation: " + ErrorMessage);

    public static OperationResult<T> Success(T value) => new(value, null);

    public static OperationResult<T> Failure(OperationError error) => new(default, error);

    public static OperationResult<T> Failure(ErrorKind kind, string message, string? detail = null) =>
        new(default, new OperationError(kind, message, detail));
}
