using System.Buffers.Binary;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing.Rscp;
using SkiaSharp;

namespace DotCharter.Tests.Tools.ReadmeMedia;

/// <summary>
///     Writes the README demo content (issue #912) to a folder: the flagship chart project
///     ("DotCharter Demo": <c>.rscp</c>, rendered <c>song.wav</c>, drawn <c>cover.png</c>) and a
///     <c>library/</c> folder of <c>.dcsong</c> packages for the Library and Song Select shots.
///     Everything - title, lyrics, tune, cover, audio - is generated here; see
///     <c>docs/agents/readme-media.md</c> for how the pictures are made from it.
/// </summary>
internal static class ReadmeDemoContent
{
    public const string ProjectFolder = "DotCharter Demo";

    public const string ProjectFile = "DotCharter Demo.rscp";

    /// <summary>Writes everything under <paramref name="root" /> and returns the project file.
    /// The <c>.dcsong</c> packages need ffmpeg and are skipped (empty list) without it.</summary>
    public static async Task<(string ProjectPath, IReadOnlyList<string> Packages)> WriteAllAsync(string root)
    {
        var projectDir = Path.Combine(root, ProjectFolder);
        Directory.CreateDirectory(projectDir);
        var projectPath = await WriteSongAsync(ReadmeDemoSong.Flagship, projectDir, ProjectFile);
        WriteChat(projectDir, ReadmeDemoSong.StableId(ReadmeDemoSong.Flagship.Title));

        var packages = new List<string>();
        var libraryDir = Path.Combine(root, "library");
        Directory.CreateDirectory(libraryDir);
        foreach (var variant in ReadmeDemoSong.Extras.Prepend(ReadmeDemoSong.Flagship))
        {
            var work = Path.Combine(root, "work", variant.Title);
            Directory.CreateDirectory(work);
            var path = await WriteSongAsync(variant, work, variant.Title + ".rscp");
            var package = Path.Combine(libraryDir, variant.Title + ".dcsong");
            try
            {
                var project = new RscpChartSerializer().Load(path);
                await new DcSongWriter(null, () => null).WriteAsync(new DcSongWriteRequest
                {
                    Project = project, AudioPath = Path.Combine(work, "song.wav"), OutputPath = package,
                    CoverPath = Path.Combine(work, "cover.png"), ProjectDirectory = work
                });
                packages.Add(package);
            }
            catch (DcSongWriteException)
            {
                // No ffmpeg here: the project alone is still written.
            }
        }

        return (projectPath, packages);
    }

    /// <summary>
    ///     A short saved AI chat beside the project, for the chat-pane picture: it is what the pane
    ///     shows for this chart. The text is written here, not produced by a model; the chart on
    ///     screen is the one it talks about.
    /// </summary>
    private static void WriteChat(string projectDir, string chartId)
    {
        var at = new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);
        var chat = AiChatConversation.Start(chartId, "example-model", at) with { Id = "c0de0000000000000000000000000912" };
        chat = chat.Append(new AiChatTurn(AiChatRole.User,
            "Finger the verse chords so my hand stays around the second fret.", at));
        chat = chat.Append(new AiChatTurn(AiChatRole.Assistant,
            "Em, C, G and D can all be fingered from the second-fret position, so the hand never has to leave it " +
            "between bars 5 and 8. I proposed fingerings for those four chord shapes and left every note as it is. " +
            "The proposals are on the review card: accept or reject each one there.", at.AddSeconds(8)));
        chat = chat.Append(new AiChatTurn(AiChatRole.User, "Why is the G chord fingered 2-1-3?", at.AddMinutes(2)));
        chat = chat.Append(new AiChatTurn(AiChatRole.Assistant,
            "The index finger takes the second fret of the A string, which is where the hand sits for the whole " +
            "verse. The middle finger plays the low E string and the ring finger the high E string, so there is " +
            "no stretch. If you prefer 3-2-4, say so and I will re-propose it.", at.AddMinutes(2).AddSeconds(9)));
        var folder = Path.Combine(projectDir, AiChatStore.FolderName, chartId);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, chat.Id + ".json"), AiChatJson.Serialize(chat));
    }

    private static async Task<string> WriteSongAsync(ReadmeDemoVariant variant, string dir, string fileName)
    {
        var (project, notes) = ReadmeDemoSong.Build(variant);
        WriteWav(Path.Combine(dir, "song.wav"), ReadmeDemoAudio.Render(notes));
        File.WriteAllBytes(Path.Combine(dir, "cover.png"), Cover(variant));
        var path = Path.Combine(dir, fileName);
        new RscpChartSerializer().Save(path, project, project.Beats[^1].Time);
        await Task.CompletedTask;
        return path;
    }

    private static void WriteWav(string path, short[] interleavedStereo)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);
        var dataBytes = interleavedStereo.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)2);
        w.Write(ReadmeDemoAudio.SampleRate);
        w.Write(ReadmeDemoAudio.SampleRate * 4);
        w.Write((short)4);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        var buffer = new byte[dataBytes];
        for (var i = 0; i < interleavedStereo.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), interleavedStereo[i]);
        w.Write(buffer);
    }

    /// <summary>The cover: a dark gradient, a fretboard and a run of glowing dots - drawn, not photographed.</summary>
    public static byte[] Cover(ReadmeDemoVariant variant)
    {
        const int size = 1000;
        using var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        var top = SKColor.FromHsv(variant.Hue, 55, 38);
        var bottom = SKColor.FromHsv((variant.Hue + 30) % 360, 70, 10);
        using (var background = new SKPaint())
        {
            background.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, size), [top, bottom],
                SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, size, size, background);
        }

        using var line = new SKPaint { Color = new SKColor(255, 255, 255, 40), StrokeWidth = 3, IsAntialias = true };
        for (var s = 0; s < 6; s++) canvas.DrawLine(60, 520 + (s * 40), size - 60, 520 + (s * 40), line);
        for (var f = 0; f <= 7; f++) canvas.DrawLine(60 + (f * 125), 520, 60 + (f * 125), 520 + (5 * 40), line);

        var accent = SKColor.FromHsv(variant.Hue, 30, 100);
        var random = new Random(variant.Title.Length * 31);
        var x = 120f;
        var y = 520f + (random.Next(0, 6) * 40);
        for (var i = 0; i < 7; i++)
        {
            using var glow = new SKPaint
            {
                Color = accent.WithAlpha(110), IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18)
            };
            canvas.DrawCircle(x, y, 34, glow);
            using var dot = new SKPaint { Color = accent, IsAntialias = true };
            canvas.DrawCircle(x, y, 18, dot);
            x += 125;
            y = Math.Clamp(y + (random.Next(-2, 3) * 40), 520, 520 + (5 * 40));
        }

        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var soft = new SKPaint { Color = new SKColor(255, 255, 255, 190), IsAntialias = true };
        using var bold = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 84);
        using var regular = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 40);
        DrawFitted(canvas, variant.Title, bold, white, 100, 410, size - 200);
        DrawFitted(canvas, variant.Artist, regular, soft, 100, 465, size - 200);
        DrawFitted(canvas, variant.Album.ToUpperInvariant(), regular, soft, 100, 790, size - 200);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static void DrawFitted(SKCanvas canvas, string text, SKFont font, SKPaint paint, float x, float y, float maxWidth)
    {
        var width = font.MeasureText(text);
        if (width > maxWidth) font.Size *= maxWidth / width;
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }
}
