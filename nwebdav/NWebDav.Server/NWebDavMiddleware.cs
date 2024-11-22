using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NWebDav.Server.Handlers;

namespace NWebDav.Server;

internal class NWebDavMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<NWebDavOptions> _options;
    private readonly ILogger<NWebDavMiddleware> _logger;

    public NWebDavMiddleware(RequestDelegate next, IOptions<NWebDavOptions> options, ILogger<NWebDavMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var opts = _options.Value;
        if (opts.Filter(context))
        {
            if (opts.Handlers.TryGetValue(context.Request.Method, out var handlerType))
            {
                _logger.LogTrace("Using handler type '{HandlerType}'.", handlerType);

                if (opts.RequireAuthentication && context.Request.Method != HttpMethods.Options && !(context.User.Identity?.IsAuthenticated ?? false))
                {
                    await context.ChallengeAsync().ConfigureAwait(false);
                    return;
                }

                var handler = (IRequestHandler)context.RequestServices.GetRequiredService(handlerType);
                var handled = await handler.HandleRequestAsync(context).ConfigureAwait(false);

                if (handled)
                {
                    //Address port exhaustion of the ephemeral ports.
                    //Confirm by running:
                    //Get-NetTCPConnection | Group-Object -Property State, OwningProcess | Select -Property Count, Name, @{Name="ProcessName";Expression={(Get-Process -PID ($_.Name.Split(',')[-1].Trim(' '))).Name}}, Group | Sort Count -Descending | Select-Object -First 10
                    context.Connection.RequestClose();
                    return;
                }
            }
            else
            {
                _logger.LogTrace("Skipped request, because HTTP method {HttpMethod} has no handler.", context.Request.Method);
            }
        }
        else
        {
            _logger.LogTrace("Skipped request, because it didn't match the filter.");
        }

        // Default handling
        await _next(context).ConfigureAwait(false);
    }
}