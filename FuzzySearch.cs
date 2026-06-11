#nullable enable
using System;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // Private copy for the map tool. Deliberately NOT shared with the console's FuzzySearch —
    // the addon owns its own copy so it can be extracted as a submodule with zero game/console
    // dependencies. See MAPTOOL_DESIGN.md "No shared code with the console."
    public static class FuzzySearch
    {
        // Higher = better match. Returns -1 if no match.
        public static int Score(string query, string candidate)
        {
            if (string.IsNullOrEmpty(query)) { return 0; }

            string queryLower = query.ToLowerInvariant();
            string candidateLower = candidate.ToLowerInvariant();

            int subIndex = candidateLower.IndexOf(queryLower, StringComparison.Ordinal);
            if (subIndex >= 0) { return 1000 - subIndex; }

            int queryIndex = 0;
            int score = 0;
            for (int ci = 0; ci < candidateLower.Length && queryIndex < queryLower.Length; ci++)
            {
                if (candidateLower[ci] == queryLower[queryIndex]) { score++; queryIndex++; }
            }

            if (queryIndex < queryLower.Length) { return -1; }
            return score;
        }
    }
}
