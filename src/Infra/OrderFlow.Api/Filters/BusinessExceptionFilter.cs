using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using OrderFlow.Business.Exceptions;

namespace OrderFlow.Api.Filters;

/// <summary>
/// Turns a <see cref="BusinessException"/> thrown by a Business class into its HTTP status code,
/// so controllers stay free of if/return BadRequest(...) code.
/// </summary>
public sealed class BusinessExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not BusinessException exception)
            return;

        var statusCode = exception.Type switch
        {
            BusinessErrorType.BadRequest => StatusCodes.Status400BadRequest,
            BusinessErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            BusinessErrorType.NotFound => StatusCodes.Status404NotFound,
            BusinessErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        context.Result = exception.ResponseMessage is null
            ? new StatusCodeResult(statusCode)
            : new ObjectResult(exception.ResponseMessage) { StatusCode = statusCode };

        context.ExceptionHandled = true;
    }
}
