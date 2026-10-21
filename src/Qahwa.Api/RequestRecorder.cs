using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>One request that took longer than the threshold. No query string, body or header is ever kept.</summary>
public sealed record SlowRequest(DateTime At, string Method, string Path, int Status, long Milliseconds, string? User);

/// <summary>
/// A small recorder of the slowest API requests (ADR-112): the last few that took at least <c>Qahwa:Recorder:SlowMs</c> milliseconds
/// (default 1000; 0 turns it off). It lives in memory on each server (a restart clears it), costs one stopwatch per request, and only
/// System Managers can read it. It is a pointer to where to look, not a profiler: the database's own tools show the queries.
/// </summary>
public sealed class RequestRecorder
{
    public const int Capacity = 200;
    public const int MaxPathLength = 200;
    private readonly Queue<SlowRequest> _items = new();
    private readonly object _lock = new();

    public void Record(string method, string path, int status, long milliseconds, string? user, DateTime at)
    {
        var clean = new string((path.Length > MaxPathLength ? path[..MaxPathLength] : path).Where(c => !char.IsControl(c)).ToArray());
        lock (_lock)
        {
            _items.Enqueue(new SlowRequest(at, method, clean, status, milliseconds, user));
            while (_items.Count > Capacity)
                _items.Dequeue();
        }
    }

    /// <summary>The recorded requests, slowest first.</summary>
    public IReadOnlyList<SlowRequest> Slowest(int take = 50)
    {
        lock (_lock)
            return _items.OrderByDescending(r => r.Milliseconds).ThenByDescending(r => r.At).Take(Math.Clamp(take, 1, Capacity)).ToList();
    }

    public void Clear()
    {
        lock (_lock)
            _items.Clear();
    }
}

public static class RequestRecorderExtensions
{
    public static IServiceCollection AddQahwaRecorder(this IServiceCollection services)
    {
        services.AddSingleton<RequestRecorder>();
        return services;
    }

    public static IApplicationBuilder UseQahwaRecorder(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var recorder = context.RequestServices.GetService<RequestRecorder>();
            var threshold = context.RequestServices.GetService<IConfiguration>()?.GetValue("Qahwa:Recorder:SlowMs", 1000) ?? 1000;
            if (recorder is null || threshold <= 0 || !context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/api/realtime")) // live updates are long requests by design
            {
                await next(context);
                return;
            }

            var watch = Stopwatch.StartNew();
            try
            {
                await next(context);
            }
            finally
            {
                watch.Stop();
                if (watch.ElapsedMilliseconds >= threshold)
                    recorder.Record(context.Request.Method, context.Request.Path.Value ?? string.Empty, context.Response.StatusCode, watch.ElapsedMilliseconds,
                        context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null, DateTime.UtcNow);
            }
        });

    public static IEndpointRouteBuilder MapQahwaRecorder(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/slow-requests", async (HttpContext http, IUserContextResolver users, RequestRecorder recorder, IConfiguration configuration, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();
            if (!UserSecurityEndpoints.CanManage(user))
                throw new PermissionDeniedException("Only a System Manager can see the slow requests.");
            return Results.Ok(new { thresholdMs = configuration.GetValue("Qahwa:Recorder:SlowMs", 1000), data = recorder.Slowest() });
        });
        return app;
    }
}
