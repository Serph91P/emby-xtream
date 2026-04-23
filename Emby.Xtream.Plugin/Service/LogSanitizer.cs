using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class LogSanitizer
    {
        private static readonly Regex IpRegex = new Regex(
            @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex VersionContextRegex = new Regex(
            @"(?:Version[= ]|version )\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex XtreamCredRegex = new Regex(
            @"/live/[^/]+/[^/]+/",
            RegexOptions.Compiled);

        private static readonly Regex EmailRegex = new Regex(
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            RegexOptions.Compiled);

        private static readonly Regex ProviderHostRegex = new Regex(
            @"(https?://)([^/:]+)(:\d+)?(/player_api\.php|/live/|/movie/|/series/)",
            RegexOptions.Compiled);

        private static readonly Regex QueryCredentialRegex = new Regex(
            @"([?&](?:username|password)=)[^&\s]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex QueryApiKeyRegex = new Regex(
            @"([?&]api_key=)[^&\s]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BearerTokenRegex = new Regex(
            "(Authorization\\s*:\\s*Bearer\\s+)([^\\s,;\"']+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex JsonTokenFieldRegex = new Regex(
            "(\"(?:access|refresh|token)\"\\s*:\\s*\")(.*?)(\")",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Sanitizes a single log line by redacting PII: known credentials, IP addresses,
        /// Xtream URL credentials, emails, and provider hostnames.
        /// </summary>
        public static string SanitizeLine(string line,
            string username, string password,
            string dispatcharrUser, string dispatcharrPass)
        {
            if (string.IsNullOrEmpty(line)) return line;

            var s = line;

            // Redact specific config values if non-empty
            if (!string.IsNullOrEmpty(username))
                s = s.Replace(username, "<redacted>");
            if (!string.IsNullOrEmpty(password))
                s = s.Replace(password, "<redacted>");
            if (!string.IsNullOrEmpty(dispatcharrUser))
                s = s.Replace(dispatcharrUser, "<redacted>");
            if (!string.IsNullOrEmpty(dispatcharrPass))
                s = s.Replace(dispatcharrPass, "<redacted>");

            // Redact IP addresses, but preserve version numbers (e.g. Version=1.2.0.0)
            // Replace version patterns with placeholders first, then redact IPs, then restore
            var versionMatches = VersionContextRegex.Matches(s);
            var sbVer = new System.Text.StringBuilder(s);
            for (int i = versionMatches.Count - 1; i >= 0; i--)
            {
                var vm = versionMatches[i];
                var placeholder = "\x00VER" + i + "\x00";
                sbVer.Remove(vm.Index, vm.Length);
                sbVer.Insert(vm.Index, placeholder);
            }
            s = IpRegex.Replace(sbVer.ToString(), "<ip-redacted>");
            var sbRestore = new System.Text.StringBuilder(s);
            for (int i = 0; i < versionMatches.Count; i++)
            {
                sbRestore.Replace("\x00VER" + i + "\x00", versionMatches[i].Value);
            }
            s = sbRestore.ToString();

            // Redact Xtream credentials in URLs: /live/user/pass/
            s = XtreamCredRegex.Replace(s, "/live/<user>/<pass>/");

            // Redact query parameter credentials and API keys.
            s = QueryCredentialRegex.Replace(s, "$1<redacted>");
            s = QueryApiKeyRegex.Replace(s, "$1<redacted>");

            // Redact common token formats from headers and JSON payload snippets.
            s = BearerTokenRegex.Replace(s, "$1<redacted>");
            s = JsonTokenFieldRegex.Replace(s, "$1<redacted>$3");

            // Redact email patterns
            s = EmailRegex.Replace(s, "<email-redacted>");

            // Redact hostnames in stream URLs
            s = ProviderHostRegex.Replace(s, "$1<provider-host>$3$4");

            return s;
        }
    }
}
