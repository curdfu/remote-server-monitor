using System.Text.Json;

namespace Monitor.WebApi.Middleware;

public sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected or cancelled the request — not a server error.
            logger.LogDebug("Request canceled by client. Path: {Path}, TraceId: {TraceId}",
                context.Request.Path, context.TraceIdentifier);
        }
        catch (Exception exception)
        {
            var traceId = context.TraceIdentifier;
            logger.LogError(exception, "Unhandled exception. TraceId: {TraceId}", traceId);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var payload = JsonSerializer.Serialize(new
            {
                error = "internal_server_error",
                message = "An unexpected error occurred.",
                traceId
            });

            await context.Response.WriteAsync(payload);
        }
    }
}
