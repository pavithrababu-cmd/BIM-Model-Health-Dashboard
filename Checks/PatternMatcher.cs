using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BIMModelHealthDashboard.Checks
{
    // Caches compiled regexes from the rules file and reports invalid ones
    // instead of throwing.
    public sealed class PatternMatcher
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

        private readonly Dictionary<string, Regex?> _cache =
            new Dictionary<string, Regex?>(StringComparer.Ordinal);

        private readonly Action<string> _reportError;

        public PatternMatcher(Action<string> reportError)
        {
            _reportError = reportError;
        }

        public bool IsMatch(string? pattern, string? input)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            Regex? regex = Get(pattern);

            if (regex == null)
                return false;

            try
            {
                return regex.IsMatch(input ?? string.Empty);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        public bool MatchesAny(IEnumerable<string>? patterns, string? input) =>
            FirstMatch(patterns, input) != null;

        // Returns the first pattern that matches, or null.
        public string? FirstMatch(IEnumerable<string>? patterns, string? input)
        {
            if (patterns == null)
                return null;

            foreach (string pattern in patterns)
            {
                if (IsMatch(pattern, input))
                    return pattern;
            }

            return null;
        }

        private Regex? Get(string pattern)
        {
            if (_cache.TryGetValue(pattern, out Regex? cached))
                return cached;

            Regex? regex = null;

            try
            {
                regex = new Regex(pattern, RegexOptions.CultureInvariant, Timeout);
            }
            catch (ArgumentException ex)
            {
                _reportError($"Invalid pattern '{pattern}': {ex.Message}");
            }

            _cache[pattern] = regex;
            return regex;
        }
    }
}
