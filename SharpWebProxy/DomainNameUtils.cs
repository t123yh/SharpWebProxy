using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.Extensions.Options;
using SharpWebProxy.Data;

namespace SharpWebProxy
{
    public class DomainNameReplacer
    {
        private readonly Regex _hostRegex;
        private readonly Regex _selfAddressRegex;
        private readonly IOptions<SiteConfig> _config;
        private readonly ApplicationDbContext _context;

        public SiteConfig Config => _config.Value;

        private static readonly Regex DomainRegex = new Regex(
            @"(?<!:)(?:http\:|https\:)?\/\/(?:[a-z0-9-_]+\.)+(?:" + gTLDs.RegexList +
            @")(?:\:\d+)?(?:\/(?:[\w\/#!:.?+=&%@!\-])*)?(?![\w\/#!:.?+=&%@!\-])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public DomainNameReplacer(IOptions<SiteConfig> options, ApplicationDbContext dbContext)
        {
            _config = options;
            _hostRegex = new Regex(
                @"^([a-zA-Z0-9-]+)-(hs|m|h|p\d+|s\d+)\." + Regex.Escape(Config.UrlSuffix) + "$",
                RegexOptions.IgnoreCase);
            _selfAddressRegex = new Regex(
                @"(?:http\:|https\:)\/\/[a-zA-Z0-9]+-(?:hs|m|h|p\d+|s\d+)\." + Regex.Escape(Config.UrlSuffix) + @"\/",
                RegexOptions.IgnoreCase);
            _context = dbContext;
        }

        public string Transform(string url, bool restricted = false)
        {
            if (Config.BlacklistedDomains.Any(x => url.Contains(x)))
            {
                return Utils.RandomString(10);
            }

            // restricted mode.
            if (restricted)
            {
                // TODO: Handle replacement in restricted mode.
                /*
                var expandedSections = url.Split('.').Select(s =>
                {
                    // Add trailing x
                    var matchedKeywords = gTLDs.ReplaceWords.Where(r => s.Contains(r.Key));
                    return s + (matchedKeywords.Any() ? "x" : "");
                });
                */
                var expandedSections = url.Split('.');
                return string.Join('-', expandedSections);
            }

            // keywords replaced
            var sections = url.Split('.')
                .Select(s => Config.DomainNameReplacement.ContainsKey(s) ? Config.DomainNameReplacement[s] : s)
                .ToList();

            // www is always unwanted
            sections.Remove("www");

            // known domain is unwanted
            var cut = sections
                .AsEnumerable()
                .Reverse()
                .SkipWhile(s => gTLDs.CountryDomains.Contains(s))
                .ToArray();
            int countryCount = sections.Count - cut.Count();

            int domainsCount = cut
                .TakeWhile(s => gTLDs.TopDomains.Contains(s))
                .Count();
            int domainsIndex = domainsCount == 0
                ? -1
                : sections.Count - countryCount - domainsCount;

            if (domainsIndex != -1)
            {
                sections.RemoveRange(domainsIndex, sections.Count - domainsIndex);
            }

            if (domainsIndex != -1)
            {
                sections.Reverse();
            }
            else
            {
                // if no known domain is found, treat the last section as a domain
                sections.Reverse(0, sections.Count - 1);
            }

            if (sections.Count == 0)
                return Transform(url, true);

            return string.Join('-', sections);
        }

        public async Task<string> QueryOrAddDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                throw new BadRequestException("Domain cannot be empty");
            }

            var result = await _context.Domains.Where(x => x.Name == domain).FirstOrDefaultAsync();
            if (result != null)
                return result.Code;

            try
            {
                var code = Transform(domain);
                _context.Domains.Add(new Domain() {Name = domain, Code = code});
                await _context.SaveChangesAsync();
                return code;
            }
            catch (Exception ex)
            {
                result = await _context.Domains.Where(x => x.Name == domain).FirstOrDefaultAsync();
                if (result != null)
                    return result.Code;
                else
                    throw;
            }
        }

        private const string NeutralPrefix = "unk";

        public async Task<string> ReplaceSingleUrl(string url)
        {
            UriBuilder builder = new UriBuilder(url.StartsWith("//") ? NeutralPrefix + ":" + url : url);
            if (builder.Host.Contains(Config.UrlSuffix) || Config.NoReplaceList.Any(x => builder.Host.Contains(x)))
                return url;

            string protoStr;
            bool isNeutral;
            if (new[] {"https", "http"}.Contains(builder.Scheme))
            {
                isNeutral = false;
                bool useHttps = builder.Scheme == "https";
                if (builder.Port > 0)
                {
                    if (builder.Port == 443 && useHttps)
                        protoStr = "hs";
                    else if (builder.Port == 80 && !useHttps)
                        protoStr = "h";
                    else
                        protoStr = (useHttps ? "s" : "p") + builder.Port;
                }
                else
                {
                    protoStr = useHttps ? "hs" : "h";
                }
            }
            else
            {
                protoStr = "m";
                isNeutral = true;
            }

            var code = await QueryOrAddDomain(builder.Host);

            builder.Host = $"{code}-{protoStr}.{Config.UrlSuffix}";
            builder.Port = Config.Port ?? -1;
            string value;
            if (!isNeutral)
            {
                builder.Scheme = Config.Protocol;
                value = builder.Uri.AbsoluteUri;
            }
            else
            {
                builder.Scheme = NeutralPrefix;
                string uri = builder.Uri.AbsoluteUri;
                value = uri.Replace(NeutralPrefix + ":", "");
            }

            return value;
        }

        public async Task<string> ReplaceUrl(string content)
        {
            var replacedList = new List<Tuple<string, string>>();
            string generalReplaced = await DomainRegex.ReplaceAsync(content, async m =>
                {
                    try
                    {
                        var result = await ReplaceSingleUrl(m.Value);
                        string token = Utils.RandomString(20);
                        replacedList.Add(new Tuple<string, string>(token, result));
                        return token;
                    }
                    catch (Exception)
                    {
                        return m.Value;
                    }
                }
            );
            StringBuilder sb = new StringBuilder(generalReplaced);
            foreach (var replace in Config.ReplaceList)
            {
                sb.Replace(replace, $"{await QueryOrAddDomain(replace)}-m.{Config.AccessUrl}");
            }

            foreach (var (token, replacement) in replacedList)
            {
                sb.Replace(token, replacement);
            }

            return sb.ToString();
        }

        public async Task<Uri> MatchFullUrl(Uri originalUri)
        {
            var h = originalUri.Host.ToLower();
            var match = _hostRegex.Match(h);
            if (!match.Success)
            {
                return null;
            }

            bool usingHttps;
            string hostName;
            int port = -1;
            try
            {
                var hostCode = match.Groups[1].Value;
                hostName = (await _context.Domains.Where(x => x.Code == hostCode).FirstOrDefaultAsync())?.Name;
                if (hostName == null)
                {
                    throw new Exception();
                }

                var protocol = match.Groups[2].Value;
                if (protocol == "hs")
                {
                    usingHttps = true;
                }
                else if (protocol == "h")
                {
                    usingHttps = false;
                }
                else if (protocol == "m")
                {
                    usingHttps = originalUri.Scheme == "https";
                }
                else if (protocol.StartsWith("p"))
                {
                    usingHttps = false;
                    port = int.Parse(protocol.Substring(1));
                }
                else if (protocol.StartsWith("s"))
                {
                    usingHttps = true;
                    port = int.Parse(protocol.Substring(1));
                }
                else
                {
                    throw new Exception();
                }
            }
            catch (Exception)
            {
                throw new BadRequestException("Invalid hostname or protocol");
            }

            string scheme = usingHttps ? "https" : "http";
            UriBuilder builder = new UriBuilder();
            builder.Scheme = scheme;
            builder.Host = hostName;
            builder.Port = port;
            builder.Path = originalUri.AbsolutePath;
            var queryString = System.Web.HttpUtility.ParseQueryString(originalUri.Query);

            foreach (var key in queryString.AllKeys)
            {
                queryString[key] = await FilterSelfUrl(queryString[key]);
            }

            // TODO: Find a better way to replace the workaround
            builder.Query = queryString.ToString().Replace("%2c", ",");
            return builder.Uri;
        }

        public async Task<string> FilterSelfUrl(string content)
        {
            return await DomainRegex.ReplaceAsync(content, async m =>
                {
                    try
                    {
                        var result = await MatchFullUrl(new Uri(m.Value));
                        return result.ToString();
                    }
                    catch (Exception)
                    {
                        return m.Value;
                    }
                }
            );
        }
    }
}