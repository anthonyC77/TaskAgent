using System.Collections.Generic;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System;

namespace TaskAgent.Kindle;

public sealed class Book
{
    public string Title { get; set; } = "Sans titre";

    public string? Author { get; set; }

    public string Language { get; set; } = "fr";

    public List<Chapter> Chapters { get; } = new List<Chapter>();

    public ImageStore Images { get; init; } = new ImageStore();

    public string? CoverImageId { get; set; }

    public Book CloneWithText(Func<string, string> translate, string language, string titleSuffix = "")
    {
        Book book = new Book
        {
            Title = translate(Title) + titleSuffix,
            Author = Author,
            Language = language,
            Images = Images,
            CoverImageId = CoverImageId
        };
        foreach (Chapter chapter2 in Chapters)
        {
            Chapter chapter = new Chapter
            {
                Title = translate(chapter2.Title),
                SourceFile = chapter2.SourceFile
            };
            foreach (Block block in chapter2.Blocks)
            {
                List<Block> blocks = chapter.Blocks;
                Block item;
                if (block is HeadingBlock headingBlock)
                {
                    item = new HeadingBlock(translate(headingBlock.Text), headingBlock.Level);
                }
                else
                {
                    item = ((!(block is ParagraphBlock paragraphBlock)) ? block : new ParagraphBlock(translate(paragraphBlock.Text)));
                }
                blocks.Add(item);
            }
            book.Chapters.Add(chapter);
        }
        return book;
    }
}

public sealed class Chapter
{
    public string Title { get; set; } = "";

    public string? SourceFile { get; set; }

    public List<Block> Blocks { get; } = new List<Block>();
}

public abstract record Block;

public sealed record HeadingBlock(string Text, int Level) : Block;

public sealed record ParagraphBlock(string Text) : Block;

public sealed record ImageBlock(string ImageId, bool FullPage) : Block;

public sealed record ImageAsset(string Id, byte[] Data, string MediaType, string Extension, int Width, int Height)
{
    public string FileName => Id + Extension;
}

public sealed class ImageStore
{
    private readonly Dictionary<string, ImageAsset> _byHash = new Dictionary<string, ImageAsset>();

    private readonly List<ImageAsset> _all = new List<ImageAsset>();

    public IReadOnlyList<ImageAsset> All => _all;

    public ImageAsset Add(byte[] data, string mediaType, string extension, int width, int height)
    {
        string key = Convert.ToHexString(SHA1.HashData(data));
        if (_byHash.TryGetValue(key, out ImageAsset value))
        {
            return value;
        }
        ImageAsset imageAsset = new ImageAsset($"img{_all.Count + 1:D4}", data, mediaType, extension, width, height);
        _byHash[key] = imageAsset;
        _all.Add(imageAsset);
        return imageAsset;
    }

    public ImageAsset? Get(string id)
    {
        return _all.FirstOrDefault((ImageAsset a) => a.Id == id);
    }

    public void RemoveUnused(IEnumerable<string> usedIds)
    {
        HashSet<string> used = usedIds.ToHashSet();
        _all.RemoveAll((ImageAsset a) => !used.Contains(a.Id));
        foreach (string item in (from kv in _byHash
            where !used.Contains(kv.Value.Id)
            select kv.Key).ToList())
        {
            _byHash.Remove(item);
        }
    }
}
