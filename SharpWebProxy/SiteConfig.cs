using System.Collections.Generic;

namespace SharpWebProxy
{
    public class SiteConfig
    {
        public string Protocol { get; set; }
        public string UrlSuffix { get; set; }
        public int? Port { get; set; }
        public string PortString => Port != null ? ":" + Port.Value : "";

        public string[] ReplaceList { get; set; }
        public string[] NoReplaceList { get; set; }

        public string AccessUrl => UrlSuffix + PortString;
        
        public Dictionary<string, string> DomainNameReplacement { get; set; }
        public string[] BlacklistedDomains { get; set; }
        
        public string[] MimeWhitelist { get; set; }
        public string[] MimeWhitelistWithoutExtension { get; set; }
    }
}