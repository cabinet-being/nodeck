using Microsoft.AspNetCore.Http.Features;

namespace MyApp.Api.Storage;

/// <summary>
/// Raises the request body limits for media uploads. Kestrel's defaults (about 30 MB per request
/// and 128 MB per multipart body) reject ordinary video files, so both are set from
/// NODECK_MAX_UPLOAD_MB (default 8192 MB).
/// </summary>
public static class UploadLimits
{
    public const string ConfigurationKey = "NODECK_MAX_UPLOAD_MB";
    public const long DefaultMaxUploadMegabytes = 8192;

    public static WebApplicationBuilder AddUploadLimits(this WebApplicationBuilder builder)
    {
        var configured = builder.Configuration[ConfigurationKey];
        var megabytes = DefaultMaxUploadMegabytes;
        if (!string.IsNullOrWhiteSpace(configured) &&
            (!long.TryParse(configured, out megabytes) || megabytes is < 1 or > 1024 * 1024))
            throw new InvalidOperationException($"{ConfigurationKey} must be a whole number of megabytes from 1 to 1048576.");

        var bytes = megabytes * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = bytes);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = bytes);
        return builder;
    }
}
