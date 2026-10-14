namespace Qahwa.Core;

/// <summary>Base class for errors that map to a specific HTTP status and a stable machine-readable code.</summary>
public abstract class QahwaException(string message, int statusCode, string code) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record FieldError(string Field, string Message);

public sealed class DocumentValidationException : QahwaException
{
    public DocumentValidationException(IReadOnlyList<FieldError> errors)
        : base("Validation failed.", 400, "validation_error") => Errors = errors;

    public DocumentValidationException(string message)
        : this([new FieldError(string.Empty, message)])
    {
    }

    public IReadOnlyList<FieldError> Errors { get; }
}

public sealed class NotFoundException(string message) : QahwaException(message, 404, "not_found");

public sealed class ConflictException(string message) : QahwaException(message, 409, "conflict");

public sealed class PermissionDeniedException(string message) : QahwaException(message, 403, "permission_denied");

public sealed class UnauthenticatedException() : QahwaException("Authentication is required.", 401, "unauthenticated");
