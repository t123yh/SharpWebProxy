using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SharpWebProxy
{
    public class SiteConfig
    {
        public string Protocol { get; set; }

        private string _urlSuffix;

        public string UrlSuffix
        {
            get => _urlSuffix;
            set
            {
                _urlSuffix = value;
                InitializeRegex();
            }
        }

        public int? Port { get; set; }
        public string PortString => Port != null ? ":" + Port.Value : "";

        public string[] ReplaceList { get; set; }
        public string[] NoReplaceList { get; set; }

        public string AccessUrl => UrlSuffix + PortString;

        public Dictionary<string, string> DomainNameReplacement { get; set; }
        public string[] BlacklistedDomains { get; set; }

        public string[] MimeWhitelist { get; set; }
        public string[] MimeWhitelistWithoutExtension { get; set; }

        private void InitializeRegex()
        {
            _hostRegex = new Regex(
                @"^([a-zA-Z0-9-]+)-(hs|m|h|p\d+|s\d+)\." + Regex.Escape(UrlSuffix) + "$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            _selfAddressRegex = new Regex(
                @"(?:http\:|https\:)\/\/[a-zA-Z0-9]+-(?:hs|m|h|p\d+|s\d+)\." + Regex.Escape(UrlSuffix) + @"\/",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private Regex _hostRegex;
        private Regex _selfAddressRegex;

        public Regex HostRegex => _hostRegex;
        public Regex SelfAddressRegex => _selfAddressRegex;
    }
}