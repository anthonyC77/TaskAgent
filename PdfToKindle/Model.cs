namespace PdfToKindle;

/// <summary>Livre intermédiaire, indépendant du PDF et de l'EPUB.</summary>
public sealed class Book
{
    public string Title { get; set; } = "Sans titre";
    public string? Author { get; set; }
    /// <summary>Code ISO 639-1 (fr, en…). Important : le Kindle choisit son dictionnaire avec.</summary>
    public string Language { get; set; } = "fr";
    public List<Chapter> Chapters { get; } = new();
    public ImageStore Images { get; init; } = new();
    public string? CoverImageId { get; set; }

    /// <summary>Copie le livre en remplaçant les textes (les images sont partagées).</summary>
    public Book CloneWithText(Func<string, string> translate, string language, string titleSuffix = "")
    {
        var copy = new Book
        {
            Title = translate(Title) + titleSuffix,
            Author = Author,
            Language = language,
            Images = Images,
            CoverImageId = CoverImageId,
        };
        foreach (var ch in Chapters)
        {
            var c = new Chapter { Title = translate(ch.Title), SourceFile = ch.SourceFile };
            foreach (var b in ch.Blocks)
            {
                c.Blocks.Add(b switch
                {
                    HeadingBlock h => new HeadingBlock(translate(h.Text), h.Level),
                    ParagraphBlock p => new ParagraphBlock(translate(p.Text)),
                    _ => b,
                });
            }
            copy.Chapters.Add(c);
        }
        return copy;
    }
}

public sealed class Chapter
{
    public string Title { get; set; } = "";
    public string? SourceFile { get; set; }
    public List<Block> Blocks { get; } = new();
}

public abstract record Block;
public sealed record HeadingBlock(string Text, int Level) : Block;
public sealed record ParagraphBlock(string Text) : Block;
/// <param name="FullPage">Image qui occupe toute la page (scan, planche…) : affichée pleine largeur.</param>
public sealed record ImageBlock(string ImageId, bool FullPage) : Block;

public sealed record ImageAsset(string Id, byte[] Data, string MediaType, string Extension, int Width, int Height)
{
    public string FileName => Id + Extension;
}

/// <summary>Stocke les images une seule fois (dédoublonnage par empreinte SHA-1).</summary>
public sealed class ImageStore
{
    private readonly Dictionary<string, ImageAsset> _byHash = new();
    private readonly List<ImageAsset> _all = new();

    public IReadOnlyList<ImageAsset> All => _all;

    public ImageAsset Add(byte[] data, string mediaType, string extension, int width, int height)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data));
        if (_byHash.TryGetValue(hash, out var existing)) return existing;
        var asset = new ImageAsset($"img{_all.Count + 1:D4}", data, mediaType, extension, width, height);
        _byHash[hash] = asset;
        _all.Add(asset);
        return asset;
    }

    public ImageAsset? Get(string id) => _all.FirstOrDefault(a => a.Id == id);

    /// <summary>Retire les images qui ne sont plus référencées (logos répétés supprimés…).</summary>
    public void RemoveUnused(IEnumerable<string> usedIds)
    {
        var used = usedIds.ToHashSet();
        _all.RemoveAll(a => !used.Contains(a.Id));
        foreach (var k in _byHash.Where(kv => !used.Contains(kv.Value.Id)).Select(kv => kv.Key).ToList())
            _byHash.Remove(k);
    }
}
