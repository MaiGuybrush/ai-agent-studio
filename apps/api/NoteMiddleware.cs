using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AiAgentStudio.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using AiAgentStudio.Api.Error;

namespace AiAgentStudio.Api;

public class NoteMiddlewareOptions {
    public string[] AvailableShops;
}

public class NoteMiddleware
{
    private readonly RequestDelegate _next;

    [ThreadStatic]
    public static Shop RequestShop;

    public static string RequestAction;
    public static ConcurrentDictionary<string, Shop> AvailableShops = new ConcurrentDictionary<string, Shop>();
    public NoteMiddleware(RequestDelegate next, IOptions<NoteMiddlewareOptions> options)
    {
        _next = next;
        foreach (var shop in options.Value.AvailableShops) {
            if (!AvailableShops.ContainsKey(shop.ToUpper())) {
                AvailableShops[shop.ToUpper()] = Shop.GetShop(shop);
            }
        }
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Method == "GET" ||
            httpContext.Request.Method == "POST" ||
            httpContext.Request.Method == "PUT" ||
            httpContext.Request.Method == "DELETE") {
            var shop = (string)httpContext.Request.RouteValues["shop"];
            Console.WriteLine($"NoteMiddleware shop = {shop}");
            var action = (string)httpContext.Request.RouteValues["action"];
            Console.WriteLine($"NoteMiddleware action = {action}");
            RequestAction = action;
            if (!string.IsNullOrEmpty(shop))
            {
                if (AvailableShops.Count > 0 && !AvailableShops.ContainsKey(shop.ToUpper()))
                {
                    throw new ApiException(ApiErr.BadRequest, $"request url contain not support shop[{shop}].");
                }
                RequestShop = Shop.GetShop((string)shop);
            }
        }
        // httpContext.Items["ShopObj"] = RequestShop;
        await _next(httpContext);
    }
}

public static class NoteMiddlewareExtensions
{
    public static IApplicationBuilder UseNoteMiddleware(
        this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<NoteMiddleware>();
    }
}