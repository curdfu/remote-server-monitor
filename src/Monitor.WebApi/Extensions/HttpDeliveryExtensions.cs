using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;

namespace Monitor.WebApi.Extensions;

public static partial class HttpDeliveryExtensions
{
    public static IServiceCollection AddMonitorHttpDelivery(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services;
    }

    public static IApplicationBuilder UseMonitorHttpDelivery(this IApplicationBuilder app)
    {
        app.UseResponseCompression();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/hubs") ||
                context.Request.Path.StartsWithSegments("/health"))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
        return app;
    }

    public static StaticFileOptions CreateMonitorStaticFileOptions()
    {
        return new StaticFileOptions
        {
            OnPrepareResponse = SetStaticFileCacheControl
        };
    }

    private static void SetStaticFileCacheControl(StaticFileResponseContext context)
    {
        // Only content-addressed Vite assets are immutable. HTML and other files must revalidate.
        var isHashedAsset = context.Context.Request.Path.StartsWithSegments("/assets") &&
                            HashedAssetName().IsMatch(context.File.Name);
        context.Context.Response.Headers.CacheControl = isHashedAsset
            ? "public,max-age=31536000,immutable"
            : "no-cache";
    }

    [GeneratedRegex(@"-[A-Za-z0-9_-]{8,}\.(?:js|css|woff2?|ttf|png|jpe?g|webp|avif|svg|ico)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HashedAssetName();
}
