using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Scaffolding.Internal;
using Microsoft.Extensions.Options;
using SharpWebProxy.Data;

namespace SharpWebProxy
{
    public class DomainNameReplacer
    {
        private readonly IOptions<SiteConfig> _config;
        private readonly ApplicationDbContext _dbContext;

        public SiteConfig Config => _config.Value;

        public DomainNameReplacer(IOptions<SiteConfig> options, ApplicationDbContext dbContext)
        {
            _config = options;
            _dbContext = dbContext;
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

            var result = await _dbContext.Domains.Where(x => x.Name == domain).FirstOrDefaultAsync();
            if (result != null)
                return result.Code;

            bool strictMode = false;
            addRecord:
            try
            {
                var code = Transform(domain, strictMode);
                _dbContext.Domains.Add(new Domain() {Name = domain, Code = code});
                await _dbContext.SaveChangesAsync();
                return code;
            }
            catch (Exception ex)
            {
                // TODO: Handle this for other databse
                var msg = ex.InnerException.Message.ToLower();
                if (msg.Contains("unique") && msg.Contains("code"))
                {
                    if (!strictMode)
                    {
                        _dbContext.DiscardChanges<Domain>();
                        strictMode = true;
                        goto addRecord;
                    }
                    else
                    {
                        throw new Exception($"Domain transform failed. Duplicate domain {domain}");
                    }
                }
                else if (msg.Contains("unique") && msg.Contains("name"))
                {
                    result = await _dbContext.Domains.Where(x => x.Name == domain).FirstOrDefaultAsync();
                    if (result != null)
                        return result.Code;
                }

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


        public async Task<Uri> MatchFullUrl(Uri originalUri, bool returnIfInvalid = false)
        {
            try
            {
                var h = originalUri.Host.ToLower();
                var match = _config.Value.HostRegex.Match(h);
                if (!match.Success)
                {
                    throw new Exception("Invalid hostname.");
                }

                bool usingHttps;
                string hostName;
                int port = -1;
                try
                {
                    var hostCode = match.Groups[1].Value;
                    hostName = (await _dbContext.Domains.Where(x => x.Code == hostCode).FirstOrDefaultAsync())?.Name;
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
            catch (Exception)
            {
                if (returnIfInvalid)
                    return originalUri;
                throw;
            }
        }

        public async Task<string> FilterSelfUrl(string content)
        {
            return await Utils.DomainRegex.ReplaceAsync(content, async m =>
                {
                    var result = await MatchFullUrl(new Uri(m.Value), true);
                    return result.ToString();
                }
            );
        }
    }
}