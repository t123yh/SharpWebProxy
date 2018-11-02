using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SharpWebProxy
{
    public static class Utils
    {
        private static Random random = new Random();
        
        public static readonly Regex DomainRegex = new Regex(
            @"(?<!:)(?:http\:|https\:)?\/\/(?:[a-z0-9-_]+\.)+(?:" + gTLDs.RegexList +
            @")(?:\:\d+)?(?:\/(?:[\w\/#!:.?+=&%@!\-])*)?(?![\w\/#!:.?+=&%@!\-])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        
        public static string RandomString(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static void DiscardChanges<TEntity>(this DbContext context) where TEntity : class
        {
            var changedEntries = context.ChangeTracker.Entries<TEntity>()
                .Where(x => x.State != EntityState.Unchanged).ToList();

            foreach (var entry in changedEntries)
            {
                switch (entry.State)
                {
                    case EntityState.Modified:
                        entry.CurrentValues.SetValues(entry.OriginalValues);
                        entry.State = EntityState.Unchanged;
                        break;
                    case EntityState.Added:
                        entry.State = EntityState.Detached;
                        break;
                    case EntityState.Deleted:
                        entry.State = EntityState.Unchanged;
                        break;
                }
            }
        }
    }

    public static class RegexExtensions
    {
        public static async Task<string> ReplaceAsync(this Regex regex, string input,
            Func<Match, Task<string>> replacementFn)
        {
            var sb = new StringBuilder();
            var lastIndex = 0;

            foreach (Match match in regex.Matches(input))
            {
                sb.Append(input, lastIndex, match.Index - lastIndex)
                    .Append(await replacementFn(match).ConfigureAwait(false));

                lastIndex = match.Index + match.Length;
            }

            sb.Append(input, lastIndex, input.Length - lastIndex);
            return sb.ToString();
        }
    }

    internal static class StringExtensions
    {
        internal static string SubstringTrim(this string value, int startIndex, int length)
        {
            if (length == 0)
            {
                return string.Empty;
            }

            int endIndex = startIndex + length - 1;

            while (startIndex <= endIndex && char.IsWhiteSpace(value[startIndex]))
            {
                startIndex++;
            }

            while (endIndex >= startIndex && char.IsWhiteSpace(value[endIndex]))
            {
                endIndex--;
            }

            int newLength = endIndex - startIndex + 1;

            return
                newLength == 0 ? string.Empty :
                newLength == value.Length ? value :
                value.Substring(startIndex, newLength);
        }
    }
}