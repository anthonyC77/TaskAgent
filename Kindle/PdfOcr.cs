using PDFtoImage;
using SkiaSharp;
using TaskAgent.Services;
using TesseractOCR;
using TesseractOCR.Enums;
using OcrImage = TesseractOCR.Pix.Image;

namespace TaskAgent.Kindle;

public enum OcrMode { Off, Auto, Force }

/// <summary>Un paragraphe reconnu par l'OCR, en coordonnées PDF (points, origine en bas à gauche comme PdfPig).</summary>
public sealed record OcrParagraph(IReadOnlyList<string> Lines, double Top, double Bottom, double FontSize);

/// <summary>Schéma repéré sur la page (là où l'OCR ne lit que du charabia) : découpé dans le rendu de la page, en PNG.</summary>
public sealed record OcrFigure(byte[] Png, double Top, int Width, int Height, double AreaRatio);

public sealed record OcrPage(List<OcrParagraph> Paragraphs, List<OcrFigure> Figures);

/// <summary>
/// OCR des pages scannées : PDFium (via PDFtoImage) « photographie » la page, puis Tesseract y lit le texte.
/// PDFium sait aussi décoder les scans JBIG2 / JPEG2000 que PdfPig ne lit pas.
/// Un moteur par PDF : Tesseract n'est pas utilisable depuis plusieurs fils à la fois.
/// </summary>
public sealed class PdfOcr : IDisposable
{
    private readonly byte[] _pdf;
    private readonly int _dpi;
    private readonly Engine _engine;

    private PdfOcr(byte[] pdf, int dpi, Engine engine) => (_pdf, _dpi, _engine) = (pdf, dpi, engine);

    /// <summary>
    /// Ouvre le moteur (en téléchargeant les modèles de langue au besoin, donc seulement si un scan est rencontré).
    /// Toute impossibilité devient une InvalidOperationException au message explicite.
    /// </summary>
    public static PdfOcr Open(string pdfPath, string languages, int dpi, Action<string> log, CancellationToken ct)
    {
        try
        {
            OcrData.EnsureAsync(languages, log, ct).GetAwaiter().GetResult(); // appelé hors du fil de l'interface
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException("Téléchargement des modèles OCR impossible : " + ex.Message, ex);
        }

        Engine engine;
        try
        {
            // On donne la résolution de rendu : sinon Tesseract la devine (moins précis, et il l'écrit sur la console).
            engine = new Engine(OcrData.Dir, languages, EngineMode.LstmOnly,
                initialValues: new Dictionary<string, object> { ["user_defined_dpi"] = dpi.ToString() });
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new InvalidOperationException(OperatingSystem.IsWindows()
                ? "OCR indisponible : installe le « Microsoft Visual C++ Redistributable 2015-2022 (x64) » " +
                  "(https://aka.ms/vs/17/release/vc_redist.x64.exe) puis relance TaskAgent."
                : "OCR indisponible sur ce système pour l'instant (moteur Tesseract fourni pour Windows uniquement).", ex);
        }
        return new PdfOcr(File.ReadAllBytes(pdfPath), dpi, engine);
    }

    /// <summary>Lit une page (numérotée à partir de 1) et renvoie ses paragraphes dans l'ordre de lecture.</summary>
    /// <param name="ignore">Zones (en points, origine en bas à gauche) des illustrations déjà gardées en image :
    /// le texte que l'OCR y lit (étiquettes d'un schéma) ferait doublon, et souvent du charabia.</param>
    public OcrPage ReadPage(int pageNumber, double pageHeightPoints, IReadOnlyList<(double Left, double Top, double Right, double Bottom)>? ignore = null)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Rendu PDF non disponible sur ce système.");
#pragma warning disable CA1416 // garde ci-dessus ; l'analyseur réclame aussi Android, sans objet pour une appli de bureau
        using var bitmap = Conversion.ToImage(_pdf, (Index)(pageNumber - 1),
            options: new RenderOptions(Dpi: _dpi, BackgroundColor: SKColors.White));
#pragma warning restore CA1416
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var image = OcrImage.LoadFromMemory(png.ToArray());
        using var page = _engine.Process(image);

        var pxToPt = 72.0 / _dpi;
        var result = new List<OcrParagraph>();
        var boxes = new List<SKRectI>(); // position en pixels de chaque paragraphe gardé (même ordre que result)
        foreach (var block in page.Layout)
        foreach (var paragraph in block.Paragraphs)
        {
            if (paragraph.BoundingBox is not { } box || paragraph.Confidence < 30) continue;
            var cx = (box.X1 + box.X2) / 2.0 * pxToPt;
            var cy = pageHeightPoints - (box.Y1 + box.Y2) / 2.0 * pxToPt;
            if (ignore?.Any(r => cx >= r.Left - 2 && cx <= r.Right + 2 && cy <= r.Top + 2 && cy >= r.Bottom - 2) == true) continue;

            var lines = new List<string>();
            var heights = new List<double>();
            foreach (var line in paragraph.TextLines)
            {
                var text = line.Text?.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                lines.Add(text);
                if (line.BoundingBox is { } lb) heights.Add(lb.Height);
            }
            if (lines.Count == 0 || !lines.Any(l => l.Any(char.IsLetter))) continue;

            // La hauteur d'une ligne (jambages compris) vaut environ 1,3 × la taille de police.
            heights.Sort();
            var lineHeight = heights.Count > 0 ? heights[heights.Count / 2] : box.Height / (double)lines.Count;
            result.Add(new OcrParagraph(lines,
                Top: pageHeightPoints - box.Y1 * pxToPt,
                Bottom: pageHeightPoints - box.Y2 * pxToPt,
                FontSize: lineHeight * pxToPt / 1.3));
            boxes.Add(new SKRectI(box.X1, box.Y1, box.X2, box.Y2));
        }

        // Schémas : le charabia et ses étiquettes ne deviennent pas du texte, mais une image découpée dans la page.
        var junk = TextFixes.FindDiagramJunk(result.Select(p => string.Join(" ", p.Lines)).ToList());
        var figures = new List<OcrFigure>();
        for (var i = 0; i < junk.Length; i++)
        {
            if (!junk[i]) continue;
            var area = boxes[i];
            var j = i;
            while (j + 1 < junk.Length && junk[j + 1]) { j++; area.Union(boxes[j]); }
            // Marge autour du schéma, et pas de miette : en dessous d'un demi-pouce de haut, on jette juste le charabia.
            area.Inflate(_dpi / 15, _dpi / 15);
            area.Intersect(new SKRectI(0, 0, bitmap.Width, bitmap.Height));
            if (area.Height >= _dpi / 2 && area.Width >= _dpi)
            {
                using var crop = new SKBitmap();
                if (bitmap.ExtractSubset(crop, area))
                {
                    using var data = crop.Encode(SKEncodedImageFormat.Png, 100);
                    figures.Add(new OcrFigure(data.ToArray(), pageHeightPoints - area.Top * pxToPt, area.Width, area.Height,
                        area.Width * (double)area.Height / (bitmap.Width * (double)bitmap.Height)));
                }
            }
            i = j;
        }
        return new OcrPage(result.Where((_, k) => !junk[k]).ToList(), figures);
    }

    public void Dispose() => _engine.Dispose();
}

/// <summary>
/// Modèles de langue Tesseract (« tessdata_best », les plus précis), téléchargés une fois
/// dans le dossier de données de TaskAgent au premier usage.
/// </summary>
public static class OcrData
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string Dir { get; } = Path.Combine(AppPaths.DataDir, "tessdata");

    /// <summary>« fra+eng » → télécharge fra.traineddata et eng.traineddata s'ils manquent.</summary>
    public static async Task EnsureAsync(string languages, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        foreach (var lang in languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var file = Path.Combine(Dir, lang + ".traineddata");
            if (File.Exists(file)) continue;

            log($"Téléchargement du modèle OCR « {lang} » (une seule fois, quelques Mo)…");
            var url = $"https://github.com/tesseract-ocr/tessdata_best/raw/main/{lang}.traineddata";
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Modèle OCR « {lang} » introuvable ({(int)res.StatusCode}). " +
                                                    "Codes valides : fra, eng, deu, spa, ita… séparés par « + ».");
            var tmp = file + ".part";
            await using (var fs = File.Create(tmp))
                await res.Content.CopyToAsync(fs, ct);
            File.Move(tmp, file, overwrite: true);
            log($"   ✓ {lang}.traineddata ({new FileInfo(file).Length / 1_048_576} Mo)");
        }
    }
}
