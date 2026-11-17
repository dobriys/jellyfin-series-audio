using System.Text;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.SeriesAudio;

internal sealed class WebPanelStartupFilter(IServerConfigurationManager config) : IStartupFilter
{
    private const string ScriptId = "id=\"series-audio-script\"";
    private const string Script = "<script id=\"series-audio-script\" defer src=\"../SeriesAudio/web.js\"></script>";
    private static readonly string[] FullDocumentHeaders =
        [HeaderNames.AcceptEncoding, HeaderNames.IfNoneMatch, HeaderNames.IfModifiedSince, HeaderNames.Range];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var baseUrl = config.GetNetworkConfiguration().BaseUrl;
        var index = new PathString(baseUrl + "/web/index.html");
        var directory = new PathString(baseUrl + "/web/");
        app.Use((context, forward) => HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path.Equals(index) || context.Request.Path.Equals(directory))
                ? InjectPanel(context, forward)
                : forward(context));
        next(app);
    };

    private static async Task InjectPanel(HttpContext context, RequestDelegate forward)
    {
        var requestHeaders = context.Request.Headers;
        var originalHeaders = new Dictionary<string, StringValues>();
        // Ask the native static-file handler for the full, uncompressed document.
        foreach (var name in FullDocumentHeaders)
        {
            if (!requestHeaders.TryGetValue(name, out var value)) continue;
            originalHeaders.Add(name, value);
            requestHeaders.Remove(name);
        }

        var response = context.Response;
        var originalBody = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await forward(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = originalBody;
            foreach (var (name, value) in originalHeaders) requestHeaders[name] = value;
        }

        if (response.StatusCode == StatusCodes.Status200OK && !response.HasStarted
            && response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
            && StringValues.IsNullOrEmpty(response.Headers.ContentEncoding))
        {
            var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (end >= 0 && !html.Contains(ScriptId, StringComparison.Ordinal))
            {
                var updated = Encoding.UTF8.GetBytes(html.Insert(end, Script));
                response.ContentLength = updated.Length;
                response.Headers.Remove(HeaderNames.ETag);
                response.Headers.Remove(HeaderNames.LastModified);
                response.Headers.Remove(HeaderNames.AcceptRanges);
                response.Headers.CacheControl = "no-store";
                await originalBody.WriteAsync(updated, context.RequestAborted).ConfigureAwait(false);
                return;
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }
}
