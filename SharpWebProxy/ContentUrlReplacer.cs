using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Extreme.Net;
using Microsoft.Extensions.Options;

namespace SharpWebProxy
{
    public class ContentUrlReplacer
    {
        private readonly DomainNameReplacer _domainReplacer;
        private readonly IOptions<SiteConfig> _config;

        public SiteConfig Config => _config.Value;

        public ContentUrlReplacer(DomainNameReplacer domainReplacer, IOptions<SiteConfig> config)
        {
            _domainReplacer = domainReplacer;
            _config = config;
        }
        
        public async Task<string> ReplaceUrlInText(string content)
        {
            var replacedList = new List<Tuple<string, string>>();
            string generalReplaced = await Utils.DomainRegex.ReplaceAsync(content, async m =>
                {
                    try
                    {
                        var result = await _domainReplacer.ReplaceSingleUrl(m.Value);
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
                sb.Replace(replace, $"{await _domainReplacer.QueryOrAddDomain(replace)}-m.{Config.AccessUrl}");
            }

            foreach (var (token, replacement) in replacedList)
            {
                sb.Replace(token, replacement);
            }

            return sb.ToString();
        }

        public async Task<string> ReplaceGoogleSearch(string content)
        {
            StringBuilder sb = new StringBuilder();
            var splitContent = content.Split("\n");
            foreach (var x in splitContent)
            {
                int sp = x.IndexOf(";", StringComparison.Ordinal);
                if (sp == -1)
                {
                    sb.AppendLine(x);
                    continue;
                }
                int len = int.Parse(x.Substring(0, sp), System.Globalization.NumberStyles.HexNumber);
                if (x.Length - sp != len)
                {
                    sb.AppendLine(x);
                    continue;
                }
                string item = x.Substring(sp + 1);
                string result = await ReplaceUrlInText(item);
                sb.AppendJoin((result.Length + 1).ToString("x"), ";", result, "\n");
            }

            return sb.ToString();
        }
    }
}