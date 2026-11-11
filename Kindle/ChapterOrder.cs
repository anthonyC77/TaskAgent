using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig;

namespace TaskAgent.Kindle;

public static class ChapterOrder
{
    private static readonly string[] Front = new string[19]
    {
        "cover", "couverture", "title page", "half title", "copyright", "dedication", "dédicace", "table of contents", "contents", "sommaire",
        "table des matières", "about this", "about the book", "foreword", "avant-propos", "preface", "préface", "introduction", "prologue"
    };

    private static readonly string[] Back = new string[19]
    {
        "conclusion", "epilogue", "épilogue", "afterword", "postface", "appendix", "annexe", "glossary", "glossaire", "bibliograph",
        "references", "références", "acknowledg", "remerciements", "about the author", "à propos de l'auteur", "index", "other books", "autres livres"
    };

    private static readonly Regex ChapterRx = new Regex("^(?:(?:chapter|chapitre|chap\\.?|ch\\.?|lesson|leçon|module|unit|unité)\\s*)?(\\d{1,3})(?!\\d)(?:\\s*[._\\-:)]|\\s|$)", RegexOptions.IgnoreCase);

    private static readonly Regex PartRx = new Regex("^(?:(?:part|partie)\\s*(\\d{1,2}|[ivx]{1,5})(?=[\\s._\\-:)]|$)|([ivx]{1,5})(?=[._\\-:)]|$))", RegexOptions.IgnoreCase);

    private static readonly Regex ChapterMentionRx = new Regex("\\b(?:chapter|chapitre)\\s+(\\d{1,3})\\b", RegexOptions.IgnoreCase);

    public static List<string> Sort(IEnumerable<string> files, string root)
    {
        List<string> list = files.ToList();
        Dictionary<string, (int Group, double Rank, int Sub)> keys = list.ToDictionary((string f) => f, Key);
        PlaceParts(list, keys);
        return list.OrderBy((string f) => Path.GetDirectoryName(Path.GetRelativePath(root, f)) ?? "", Comparer<string>.Create(BookBuilder.NaturalCompare)).ThenBy((string f) => keys[f].Group).ThenBy((string f) => keys[f].Rank)
            .ThenBy((string f) => keys[f].Sub)
            .ThenBy((string f) => Path.GetFileName(f), Comparer<string>.Create(BookBuilder.NaturalCompare))
            .ToList();
    }

    private static (int Group, double Rank, int Sub) Key(string file)
    {
        string text = Path.GetFileNameWithoutExtension(file).Trim();
        string lower = text.ToLowerInvariant();
        Match match = PartRx.Match(text);
        if (match != null && match.Success)
        {
            int? num = FirstChapterMentioned(file);
            if (num.HasValue)
            {
                int valueOrDefault = num.GetValueOrDefault();
                return (Group: 1, Rank: valueOrDefault, Sub: 0);
            }
            string s = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
            return (Group: 0, Rank: Front.Length + ParseNumber(s), Sub: -1);
        }
        Match match2 = ChapterRx.Match(text);
        if (match2 != null && match2.Success)
        {
            return (Group: 1, Rank: int.Parse(match2.Groups[1].Value), Sub: 1);
        }
        int num2 = Array.FindIndex(Front, (string k) => lower.StartsWith(k));
        if (num2 >= 0)
        {
            return (Group: 0, Rank: num2, Sub: 0);
        }
        int num3 = Array.FindIndex(Back, (string k) => lower.StartsWith(k));
        if (num3 >= 0)
        {
            return (Group: 3, Rank: num3, Sub: 0);
        }
        return (Group: 2, Rank: 0.0, Sub: 0);
    }

    private static void PlaceParts(List<string> files, Dictionary<string, (int Group, double Rank, int Sub)> keys)
    {
        foreach (IGrouping<string, string> item in from f in files
            group f by Path.GetDirectoryName(f) ?? "")
        {
            List<(DateTime, double)> list = (from f in item.Where((string f) =>
                {
                    (int, double, int) tuple2 = keys[f];
                    return tuple2.Item1 == 1 && tuple2.Item3 == 1;
                })
                select (Time: File.GetLastWriteTimeUtc(f), Rank: keys[f].Rank) into c
                orderby c.Time
                select c).ToList();
            int num = list.Zip(list.Skip(1)).Count((((DateTime Time, double Rank) First, (DateTime Time, double Rank) Second) p) => p.First.Rank > p.Second.Rank);
            bool flag = list.Count > 0 && num * 10 <= list.Count;
            foreach (string item2 in item.Where((string f) => keys[f].Sub == -1).ToList())
            {
                (int, double, int) tuple = keys[item2];
                if (flag)
                {
                    DateTime time = File.GetLastWriteTimeUtc(item2);
                    int num2 = list.FindIndex(((DateTime Time, double Rank) c) => c.Time >= time);
                    Dictionary<string, (int Group, double Rank, int Sub)> dictionary = keys;
                    (int, double, int) value;
                    if (num2 < 0)
                    {
                        value = (1, list[list.Count - 1].Item2 + 0.5, 0);
                    }
                    else
                    {
                        value = (1, list[num2].Item2, 0);
                    }
                    dictionary[item2] = value;
                }
                else
                {
                    keys[item2] = (tuple.Item1, tuple.Item2, 0);
                }
            }
        }
    }

    private static int ParseNumber(string s)
    {
        if (int.TryParse(s, out var result))
        {
            return result;
        }
        return s.ToLowerInvariant() switch
        {
            "i" => 1, 
            "ii" => 2, 
            "iii" => 3, 
            "iv" => 4, 
            "v" => 5, 
            "vi" => 6, 
            "vii" => 7, 
            "viii" => 8, 
            "ix" => 9, 
            "x" => 10, 
            _ => 99, 
        };
    }

    private static int? FirstChapterMentioned(string file)
    {
        try
        {
            using PdfDocument pdfDocument = PdfDocument.Open(file);
            string input = string.Join(" ", from p in pdfDocument.GetPages().Take(4)
                select p.Text);
            List<int> list = (from m in ChapterMentionRx.Matches(input)
                select int.Parse(m.Groups[1].Value)).ToList();
            return (list.Count > 0) ? new int?(list.Min()) : ((int?)null);
        }
        catch
        {
            return null;
        }
    }
}
