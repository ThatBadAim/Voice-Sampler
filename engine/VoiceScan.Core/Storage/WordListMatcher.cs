namespace VoiceScan.Core.Storage;

using System;
using System.Collections.Generic;
using System.Text;

public sealed record WordListEntry(long Id, string Phrase, string Category);

/// <summary>
/// Matches user-defined phrases against transcripts: case-insensitive, whole words, punctuation ignored.
/// A trailing '*' on a phrase matches any ending of its last word.
/// </summary>
public static class WordListMatcher
{
    public static IReadOnlyList<WordListEntry> FindHits(string? transcript, IReadOnlyList<WordListEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(transcript) || entries.Count == 0) return Array.Empty<WordListEntry>();

        string[] words = Tokenize(transcript);
        var hits = new List<WordListEntry>();
        foreach (var entry in entries)
        {
            if (Matches(words, entry.Phrase)) hits.Add(entry);
        }
        return hits;
    }

    /// <summary>Lowercases and splits on anything that is not a letter, digit or apostrophe.</summary>
    public static string[] Tokenize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool Matches(string[] words, string phrase)
    {
        string trimmed = phrase.Trim();
        bool prefix = trimmed.EndsWith('*');
        string[] pattern = Tokenize(prefix ? trimmed[..^1] : trimmed);
        if (pattern.Length == 0 || pattern.Length > words.Length) return false;

        for (int start = 0; start + pattern.Length <= words.Length; start++)
        {
            bool all = true;
            for (int k = 0; k < pattern.Length; k++)
            {
                bool last = k == pattern.Length - 1;
                string word = words[start + k];
                bool ok = last && prefix
                    ? word.StartsWith(pattern[k], StringComparison.Ordinal)
                    : word.Equals(pattern[k], StringComparison.Ordinal);
                if (!ok)
                {
                    all = false;
                    break;
                }
            }
            if (all) return true;
        }
        return false;
    }
}
