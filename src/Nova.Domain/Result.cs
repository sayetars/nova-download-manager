namespace Nova.Domain;

/// <summary>
/// Structured, user-presentable error. Raw exceptions are converted by the engine; never leaked to UI.
/// </summary>
public sealed class NovaError
{
    public string Code { get; }
    public ErrorCategory Category { get; }
    public string Message { get; }
    public Dictionary<string, string>? Details { get; }

    public NovaError(string code, ErrorCategory category, string message,
        Dictionary<string, string>? details = null)
    {
        Code = code;
        Category = category;
        Message = message;
        Details = details;
    }

    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>Result envelope used across engine internals (no exceptions across module borders).</summary>
public class Result
{
    public bool Ok { get; }
    public NovaError? Error { get; }
    protected Result(bool ok, NovaError? error) { Ok = ok; Error = error; }
    public static Result Success() => new(true, null);
    public static Result Fail(NovaError error) => new(false, error);
    public static Result Fail(string code, ErrorCategory cat, string msg, Dictionary<string, string>? d = null)
        => new(false, new NovaError(code, cat, msg, d));
}

public sealed class Result<T> : Result
{
    public T? Value { get; }
    private Result(bool ok, T? value, NovaError? error) : base(ok, error) { Value = value; }
    public static Result<T> Success(T value) => new(true, value, null);
    public static new Result<T> Fail(NovaError error) => new(false, default, error);
    public static new Result<T> Fail(string code, ErrorCategory cat, string msg, Dictionary<string, string>? d = null)
        => new(false, default, new NovaError(code, cat, msg, d));
}
