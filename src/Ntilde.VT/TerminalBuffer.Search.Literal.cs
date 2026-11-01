using System;
using System.Collections.Generic;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>Linear overlap matching for long needles; short needles keep the vectorized framework path.</summary>
    private sealed class LongLiteralPattern
    {
        private readonly record struct Scalar(int Start, int Length);
        private readonly string _query;
        private readonly StringComparison _comparison;
        private readonly Scalar[] _scalars;
        private readonly int[] _fallback;

        private LongLiteralPattern(string query, bool caseSensitive, Scalar[] scalars)
        {
            _query = query;
            _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            _scalars = scalars;
            _fallback = new int[scalars.Length];
            int matched = 0;
            for (int index = 1; index < scalars.Length; index++)
            {
                var scalar = scalars[index];
                while (matched > 0 && !EqualsScalar(query, scalar.Start, scalar.Length, matched))
                    matched = _fallback[matched - 1];
                if (EqualsScalar(query, scalar.Start, scalar.Length, matched)) matched++;
                _fallback[index] = matched;
            }
        }

        public static LongLiteralPattern? Create(string query, bool caseSensitive)
        {
            if (query.Length <= 256) return null;
            var scalars = new List<Scalar>();
            for (int index = 0; index < query.Length;)
            {
                int length = ScalarLength(query, index);
                // Preserve framework matching of lone UTF-16 surrogates, including partial-pair needles.
                if (length == 1 && char.IsSurrogate(query[index])) return null;
                scalars.Add(new Scalar(index, length));
                index += length;
            }
            return new LongLiteralPattern(query, caseSensitive, scalars.ToArray());
        }

        private static int ScalarLength(string text, int index) =>
            char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

        private bool EqualsScalar(string text, int index, int length, int patternIndex)
        {
            var pattern = _scalars[patternIndex];
            // Compare complete scalars with exact framework ordinal semantics.
            // ToUpperInvariant is not equivalent (for example, long s).
            return text.AsSpan(index, length).Equals(_query.AsSpan(pattern.Start, pattern.Length), _comparison);
        }

        public void AppendMatches(string text, List<SearchColumn> columns, List<SearchRow> rows,
            List<SearchMatch> matches, Dictionary<(int Row, int Col), int>? sizedEnds, ref SearchMatchSpan[]? physicalSpans)
        {
            int matched = 0;
            for (int index = 0; index < text.Length;)
            {
                int length = ScalarLength(text, index);
                while (matched > 0 && !EqualsScalar(text, index, length, matched))
                    matched = _fallback[matched - 1];
                if (EqualsScalar(text, index, length, matched)) matched++;
                if (matched == _scalars.Length)
                {
                    matches.Add(CreateSearchMatch(index + length - _query.Length, _query.Length, columns, rows, sizedEnds, ref physicalSpans));
                    matched = _fallback[matched - 1];
                }
                index += length;
            }
        }
    }
}
