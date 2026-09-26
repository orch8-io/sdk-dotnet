using System.Net;
using System.Text.Json;

namespace Orch8.Sdk;

/// <summary>Base type for every exception raised by the Orch8 SDK transport and client.</summary>
public class Orch8Exception : Exception
{
    /// <summary>Creates a new exception.</summary>
    public Orch8Exception(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// The request never produced an HTTP response (DNS/connection failure, reset, or the
/// SDK's request timeout elapsed). Transport errors are retryable.
/// </summary>
public class Orch8TransportException : Orch8Exception
{
    /// <summary>Creates a new transport exception.</summary>
    public Orch8TransportException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>A request path was rejected locally before sending (e.g. a protocol-relative path).</summary>
public class Orch8InvalidPathException : Orch8Exception
{
    /// <summary>Creates a new invalid-path exception.</summary>
    public Orch8InvalidPathException(string message) : base(message) { }
}

/// <summary>
/// The engine answered with a 4xx/5xx status. <see cref="Code"/> and <see cref="Exception.Message"/> are
/// taken from the error envelope <c>{"error": {"code", "message", "request_id", "details"}}</c>.
/// Status-specific subclasses exist for 400, 401, 403, 404, 409, 413, 422, 429 and 5xx.
/// </summary>
public class Orch8ApiException : Orch8Exception
{
    /// <summary>Creates a new API exception.</summary>
    public Orch8ApiException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(message)
    {
        Status = status;
        Code = code;
        RequestId = requestId;
        Details = details;
        RawBody = rawBody;
    }

    /// <summary>HTTP status code.</summary>
    public int Status { get; }

    /// <summary>HTTP status code as <see cref="HttpStatusCode"/>.</summary>
    public HttpStatusCode StatusCode => (HttpStatusCode)Status;

    /// <summary>Machine-readable <c>error.code</c> (e.g. <c>not_found</c>), when the body carried an envelope.</summary>
    public string? Code { get; }

    /// <summary><c>error.request_id</c> or the response <c>x-request-id</c> header.</summary>
    public string? RequestId { get; }

    /// <summary>Optional <c>error.details</c>.</summary>
    public JsonElement? Details { get; }

    /// <summary>The raw response body (may be empty).</summary>
    public string? RawBody { get; }

    /// <summary>True when the status is one the transport policy considers transient (408, 425, 429, 5xx).</summary>
    public bool IsRetryable => Orch8Transport.IsRetryableStatus(Status);
}

/// <summary>400 <c>invalid_argument</c>.</summary>
public class Orch8BadRequestException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8BadRequestException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>401 <c>unauthorized</c>.</summary>
public class Orch8UnauthorizedException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8UnauthorizedException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>403 <c>forbidden</c>.</summary>
public class Orch8ForbiddenException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8ForbiddenException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>404 <c>not_found</c>.</summary>
public class Orch8NotFoundException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8NotFoundException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>409 <c>conflict</c> / <c>already_exists</c>.</summary>
public class Orch8ConflictException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8ConflictException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>413 <c>payload_too_large</c>.</summary>
public class Orch8PayloadTooLargeException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8PayloadTooLargeException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>422 <c>unprocessable_entity</c>.</summary>
public class Orch8UnprocessableException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8UnprocessableException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}

/// <summary>429 <c>rate_limited</c>.</summary>
public class Orch8RateLimitedException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8RateLimitedException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null, TimeSpan? retryAfter = null)
        : base(status, code, message, requestId, details, rawBody)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>The <c>Retry-After</c> hint, when the server sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Any 5xx status (<c>internal</c>, <c>bad_gateway</c>, <c>unavailable</c>, ...).</summary>
public class Orch8ServerException : Orch8ApiException
{
    /// <summary>Creates a new exception.</summary>
    public Orch8ServerException(int status, string? code, string message, string? requestId = null, JsonElement? details = null, string? rawBody = null)
        : base(status, code, message, requestId, details, rawBody) { }
}
