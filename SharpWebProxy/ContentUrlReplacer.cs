using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
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
    }
}