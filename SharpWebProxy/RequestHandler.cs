using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Extreme.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace SharpWebProxy
{
    public class RequestHandler
    {
        private readonly ILogger _logger;
        private readonly SiteConfig _config;
        private readonly HttpClient _httpClient;
        private readonly DomainNameReplacer _replacer;
        private readonly ContentUrlReplacer _contentReplacer;
        
        // Only replace file with no extension.
        private readonly Regex _urlNoExtension = new Regex(@"\/[a-zA-Z0-9]*$");

        public RequestHandler(IOptions<SiteConfig> pathConfig, ILoggerFactory loggerFactory,
            DomainNameReplacer replacer, HttpClient client, ContentUrlReplacer contentReplacer)
        {
            _logger = loggerFactory.CreateLogger("RequestHandler");
            _config = pathConfig.Value;
            _replacer = replacer;
            _httpClient = client;
            _contentReplacer = contentReplacer;
        }

        public async Task<Dictionary<string, IEnumerable<string>>> ProcessRequestHeader(IHeaderDictionary original)
        {
            Dictionary<string, IEnumerable<string>> result = new Dictionary<string, IEnumerable<string>>();
            string[] headersToCopy = new string[]
            {
                "User-Agent",
                "Accept",
                "Accept-Language",
                // "Connection",
                // "Keep-Alive",
                "Access-Control-Request-Headers",
                "Access-Control-Request-Methods",
                // "Upgrade-Insecure-Requests",
                "Range"
            };
            foreach (var header in headersToCopy)
            {
                if (original[header].Count > 0)
                    result.Add(header, original[header]);
            }

            string[] headersToMatch = {"Referer", "Origin"};
            foreach (var header in headersToMatch)
            {
                var values = (await Task.WhenAll(original[header]
                        .Select(x =>
                        {
                            try
                            {
                                return new Uri(x);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, $"Invalid url in {header}: {x}");
                                return null;
                            }
                        }).Where(x => (object) x != null).Select(x => _replacer.MatchFullUrl(x))))
                    .Select(x => x.AbsoluteUri).ToImmutableArray();
                if (values.Length > 0)
                    result.Add(header, values);
            }

            return result;
        }

        public bool CheckIfReplace(string mime, Uri uri)
        {
            if (_config.MimeWhitelist.Contains(mime))
                return true;
            if (_config.MimeWhitelistWithoutExtension.Contains(mime) && _urlNoExtension.IsMatch(uri.LocalPath))
                return true;
            return false;
        }

        public async Task HandleRequest(HttpContext context)
        {
            var fullUrl = await _replacer.MatchFullUrl(new Uri(context.Request.GetEncodedUrl()));

            if (fullUrl == null)
            {
                string url = context.Request.Path.Value.Substring(1);
                if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                    url = "http://" + url;
                string targetUrl = await _replacer.ReplaceSingleUrl(url);
                context.Response.StatusCode = 302;
                context.Response.Headers.Add("Location",
                    new StringValues(targetUrl));
                return;
            }

            IFormatter binaryFormatter = new BinaryFormatter();

            _logger.LogInformation($"Requesting {fullUrl}");

            string requestMethod = context.Request.Method;
            HttpRequestMessage requestMessage = new HttpRequestMessage(new HttpMethod(requestMethod), fullUrl);

            const string cookiesName = "Cookies";
            if (context.Session.TryGetValue(cookiesName, out var cookiesBinary))
            {
                MyCookieContainer cookieContainer =
                    (MyCookieContainer) binaryFormatter.Deserialize(new MemoryStream(cookiesBinary));
                var requestCookies = cookieContainer.GetCookieHeader(fullUrl);
                if (!String.IsNullOrWhiteSpace(requestCookies))
                {
                    requestMessage.Headers.Add("Cookie", requestCookies);
                }
            }

            foreach (var header in await ProcessRequestHeader(context.Request.Headers))
            {
                bool result = requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (!result)
                {
                    _logger.LogWarning($"Failed to add header ${header}");
                }
            }

            if (!(HttpMethods.IsGet(requestMethod) ||
                  HttpMethods.IsHead(requestMethod) ||
                  HttpMethods.IsDelete(requestMethod) ||
                  HttpMethods.IsTrace(requestMethod)))
            {
                if (context.Request.ContentLength != null && context.Request.ContentLength > 0)
                {
                    requestMessage.Content = new StreamContent(context.Request.Body);
                    requestMessage.Content.Headers.Add("Content-Length",
                        context.Request.ContentLength.Value.ToString());
                    requestMessage.Content.Headers.Add("Content-Type", context.Request.ContentType);
                }
            }

            using (HttpResponseMessage response = await _httpClient.SendAsync(requestMessage))
            {
                context.Response.StatusCode = (int) response.StatusCode;
                string[] responseHeadersToModify = new string[]
                {
                    "Location",
                    "Link",
                    "Refresh"
                };

                foreach (var header in responseHeadersToModify)
                {
                    if (response.Headers.TryGetValues(header, out var valueList))
                    {
                        context.Response.Headers.Add(header,
                            await Task.WhenAll(valueList.Select(x => _replacer.ReplaceSingleUrl(x)).ToArray()));
                    }
                }

                {
                    if (response.Headers.TryGetValues("Access-Control-Allow-Origin", out var valueList))
                    {
                        context.Response.Headers.Add("Access-Control-Allow-Origin",
                            await Task.WhenAll(valueList.Select(async x =>
                            {
                                if (x == "*")
                                    return x;
                                else
                                {
                                    List<string> newList = new List<string>();
                                    foreach (var domain in x.Split(",").Select(m => m.Trim()))
                                    {
                                        var replaced = await _replacer.ReplaceSingleUrl(domain);
                                        newList.Add(replaced.TrimEnd('/'));
                                    }

                                    return String.Join(", ", newList);
                                }
                            }).ToArray()));
                    }
                }

                string[] responseHeadersToCopy = new string[]
                {
                    "Access-Control-Allow-Credentials",
                    "Access-Control-Expose-Headers",
                    "Access-Control-Max-Age",
                    "Access-Control-Allow-Methods",
                    "Access-Control-Allow-Headers",
                    "Accept-Ranges",
                    "Accept-Patch",
                    "Allow",
                    // "Connection",
                    "Date",
                    "Expires",
                    "IM",
                    "Last-Modified",
                    "Pragma",
                    "P3P",
                    "Retry-After",
                    "Server",
                    "Tk",
                    "X-Frame-Options",
                    "Warning",
                    // "Content-Security-Policy",
                    // "X-Content-Security-Policy",
                    // "X-WebKit-CSP",
                    "X-UA-Compatible",
                    "X-XSS-Protection",
                    "X-Content-Type-Options",
                    "X-Content-Duration"
                };

                foreach (var header in responseHeadersToCopy)
                {
                    if (response.Headers.TryGetValues(header, out var valueList))
                    {
                        context.Response.Headers.Add(header, valueList.ToArray());
                    }
                }

                context.Response.Headers.Add("X-Original-Url", fullUrl.AbsoluteUri);

                if (response.Headers.TryGetValues("Set-Cookie", out var cookieValueList))
                {
                    // TODO: Lock cookie session object to further prevent race condition.
                    MyCookieContainer cookieContainer;
                    // We're getting the container again here to prevent race condition.
                    if (context.Session.TryGetValue(cookiesName, out var cookiesBinary2))
                    {
                        cookieContainer =
                            (MyCookieContainer) binaryFormatter.Deserialize(new MemoryStream(cookiesBinary2));
                    }
                    else
                    {
                        cookieContainer = new MyCookieContainer();
                    }

                    foreach (var cookie in cookieValueList)
                    {
                        cookieContainer.SetCookies(fullUrl, cookie, false); // Ignore invalid cookies
                    }

                    using (var ms = new MemoryStream())
                    {
                        binaryFormatter.Serialize(ms, cookieContainer);
                        context.Session.Set(cookiesName, ms.GetBuffer());
                    }
                }

                long? responseContentLength = response.Content.Headers.ContentLength;
                if (responseContentLength != null && responseContentLength > 0)
                {
                    string[] responseContentHeadersToCopy = new string[]
                    {
                        "Content-Language",
                        "Content-Range",
                        "Content-Type"
                    };

                    foreach (var header in responseContentHeadersToCopy)
                    {
                        if (response.Content.Headers.TryGetValues(header, out var valueList))
                        {
                            context.Response.Headers.Add(header, valueList.ToArray());
                        }
                    }

                    if (CheckIfReplace(response.Content.Headers.ContentType?.MediaType, fullUrl))
                    {
                        string responseContent = await response.Content.ReadAsStringAsync();
                        string replacedContent = await _contentReplacer.ReplaceUrlInText(responseContent);
                        byte[] txt = Encoding.UTF8.GetBytes(replacedContent);
                        context.Response.ContentLength = txt.Length;
                        await context.Response.Body.WriteAsync(txt, 0, txt.Length);
                    }
                    else
                    {
                        context.Response.ContentLength = responseContentLength;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            const int chunkSize = 1024;
                            byte[] buf = new byte[chunkSize];
                            int read;
                            while ((read = await stream.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) != 0)
                            {
                                await context.Response.Body.WriteAsync(buf, 0, read).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }
        }
    }
}