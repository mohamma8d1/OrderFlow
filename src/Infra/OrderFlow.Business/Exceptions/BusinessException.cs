using OrderFlow.Common.Enums;

namespace OrderFlow.Business.Exceptions;



/// <summary>
/// A business rule failed. The Api layer's BusinessExceptionFilter turns it into the matching
/// HTTP status code (400/401/404/409), with <see cref="Exception.Message"/> as the body when there is one.
/// </summary>
public sealed class BusinessException(BusinessErrorType type, string? message = null)
    : Exception(message ?? type.ToString())
{
    public BusinessErrorType Type { get; } = type;

    /// <summary>Text for the response body; null means "status code only".</summary>
    public string? ResponseMessage { get; } = message;

    public static BusinessException BadRequest(string? message = null) => new(BusinessErrorType.BadRequest, message);
    public static BusinessException Unauthorized(string? message = null) => new(BusinessErrorType.Unauthorized, message);
    public static BusinessException NotFound(string? message = null) => new(BusinessErrorType.NotFound, message);
    public static BusinessException Conflict(string? message = null) => new(BusinessErrorType.Conflict, message);
}
