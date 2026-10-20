using DotCharter.Services.Library;


namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     A song library of the owner's scale (#180: about 1,800 songs, the size of the CDLC
///     folder) built from nothing on disk: realistic artist and title shapes, a few hundred
///     artists with long and short runs, accented and Japanese names, titles long enough to
///     trim, every tuning and path shape the rows print, and ~5 % byte-copy duplicates under a
///     second file name. Deterministic: the same size gives the same library on every run.
/// </summary>
internal static class SyntheticSongLibrary
{
    public const int OwnerScale = 1800;

    private static readonly string[] Artists =
    [
        "Metallica", "Iron Maiden", "Pink Floyd", "Rush", "Tool", "Opeth", "Dream Theater", "Led Zeppelin",
        "Black Sabbath", "Megadeth", "Slayer", "Anthrax", "Pantera", "Alice In Chains", "Soundgarden", "Nirvana",
        "Radiohead", "Muse", "Queens Of The Stone Age", "Foo Fighters", "The Beatles", "The Rolling Stones", "AC/DC",
        "Guns N' Roses", "Motörhead", "Mötley Crüe", "Blue Öyster Cult", "Queensrÿche", "Sigur Rós", "Björk",
        "Måneskin", "Die Ärzte", "Beyoncé", "Ásgeir", "Rammstein", "Gojira", "Mastodon", "Ghost", "BABYMETAL",
        "ONE OK ROCK", "X JAPAN", "東京事変", "椎名林檎", "ももいろクローバーZ", "L'Arc～en～Ciel", "Red Hot Chili Peppers",
        "Rage Against The Machine", "System Of A Down", "Avenged Sevenfold", "Bullet For My Valentine", "Trivium",
        "Lamb Of God", "Killswitch Engage", "Parkway Drive", "Architects", "Bring Me The Horizon", "Sleep Token",
        "Polyphia", "Animals As Leaders", "Periphery", "Meshuggah", "Between The Buried And Me", "Coheed And Cambria",
        "The Mars Volta", "At The Drive-In", "Incubus", "Deftones", "Korn", "Linkin Park", "Disturbed", "Godsmack",
        "Stone Temple Pilots", "Pearl Jam", "Smashing Pumpkins", "Weezer", "Green Day", "Blink-182", "The Offspring",
        "Paramore", "Fall Out Boy", "My Chemical Romance", "Thrice", "Jimi Hendrix", "Stevie Ray Vaughan",
        "Eric Clapton", "B.B. King", "John Mayer", "Gary Moore", "Joe Satriani", "Steve Vai", "Yngwie Malmsteen",
        "Dire Straits", "Eagles", "Fleetwood Mac", "Lynyrd Skynyrd", "ZZ Top", "Van Halen", "Def Leppard", "Scorpions",
        "Judas Priest", "Dio", "Ozzy Osbourne", "Thin Lizzy", "Deep Purple", "Rainbow", "Whitesnake", "Europe",
        "Bon Jovi", "Aerosmith", "Kiss", "Queen", "David Bowie", "The Who", "The Kinks", "The Clash", "Ramones",
        "Sex Pistols", "The Police", "U2", "Oasis", "Blur", "Arctic Monkeys", "The Strokes", "The White Stripes",
        "The Black Keys", "Kings Of Leon", "Royal Blood", "Wolfmother", "Rival Sons", "Greta Van Fleet",
        "Alter Bridge", "Creed", "Nickelback", "Shinedown", "Three Days Grace", "Breaking Benjamin", "Evanescence",
        "Nightwish", "Within Temptation", "Epica", "Dragonforce", "Helloween", "Blind Guardian", "Sabaton",
        "Powerwolf", "Amon Amarth", "In Flames", "Dark Tranquillity", "Arch Enemy", "Children Of Bodom",
        "Insomnium", "Wintersun", "Ensiferum", "Kreator", "Sepultura", "Soulfly", "Testament", "Exodus",
        "Overkill", "Death", "Cannibal Corpse", "Obituary", "Behemoth", "Dimmu Borgir", "Emperor", "Mayhem",
        "Agalloch", "Alcest", "Deafheaven", "Pallbearer", "Electric Wizard", "Sleep", "Kyuss", "Fu Manchu",
        "Clutch", "Baroness", "Torche", "Russian Circles", "Explosions In The Sky", "Mogwai", "God Is An Astronaut",
        "Tame Impala", "King Gizzard & The Lizard Wizard", "Khruangbin", "Vulfpeck", "Snarky Puppy", "Chon",
        "Covet", "Intervals", "Plini", "Tosin Abasi", "Nita Strauss", "Orianthi", "Tom Morello", "Jack White",
        "Dan Auerbach", "St. Vincent", "Courtney Barnett", "Wet Leg", "Fontaines D.C.", "IDLES", "Turnstile",
        "Knocked Loose", "Code Orange", "Spiritbox", "Jinjer", "Lorna Shore", "Ice Nine Kills", "Motionless In White",
        "Falling In Reverse", "Pierce The Veil", "Sleeping With Sirens", "A Day To Remember", "The Amity Affliction",
        "Silverstein", "Underoath", "Norma Jean", "Converge", "The Dillinger Escape Plan", "Botch", "Every Time I Die",
        "Thursday", "Taking Back Sunday", "Brand New", "Manchester Orchestra", "Modest Mouse", "Pixies",
        "Sonic Youth", "Dinosaur Jr.", "Hüsker Dü", "Mudhoney", "Melvins", "Helmet", "Quicksand", "Fugazi",
        "Minor Threat", "Bad Brains", "Black Flag", "Dead Kennedys", "Misfits", "Danzig", "Type O Negative",
        "Paradise Lost", "My Dying Bride", "Anathema", "Katatonia", "Porcupine Tree", "Steven Wilson", "Riverside",
        "Haken", "Leprous", "Caligula's Horse", "TesseracT", "Monuments", "Karnivool", "Dead Letter Circus",
        "Tool & Friends", "A Perfect Circle", "Puscifer", "Nine Inch Nails", "Marilyn Manson", "Rob Zombie",
        "White Zombie", "Ministry", "Fear Factory", "Static-X", "Slipknot", "Stone Sour", "Mudvayne", "Sevendust"
    ];

    private static readonly string[] TitleWords =
    [
        "Black", "Fire", "Night", "Heart", "Shadow", "Light", "Dream", "Storm", "River", "Ghost", "Iron", "Blood",
        "Silver", "Electric", "Broken", "Wild", "Cold", "Burning", "Holy", "Lost", "Endless", "Crimson", "Hollow",
        "Golden", "Falling", "Rising", "Last", "First", "Dead", "Living", "Sweet", "Bitter", "Lonely", "Midnight",
        "Thunder", "Eternal", "Fading", "Sacred", "Wicked", "Velvet", "Stone", "Glass", "Paper", "Neon", "Ocean",
        "Mountain", "Desert", "Highway", "Machine", "Kingdom", "Empire", "Revolution", "Paradise", "Sanctuary",
        "Overdrive", "Echo", "Mirror", "Prophecy", "Requiem", "Lullaby", "Anthem", "Ballad", "Symphony", "Riot"
    ];

    private static readonly string[] Tunings =
    [
        "E A D G B E", "E A D G B E", "E A D G B E", "Eb Ab Db Gb Bb Eb", "D A D G B E", "C G C F A D",
        "D A D G A D", "B F# B E G# C#", "D G C F A D", "C# G# C# F# A# D#", "E A D G", "D G C F"
    ];

    private static readonly string[] Charters = ["CustomsForge", "Djpavlito", "Lordcobra", "Firekorn", "Rockfaenger", ""];

    /// <summary>The library: <paramref name="size" /> distinct songs plus ~5 % duplicates.</summary>
    public static List<LibrarySongEntry> Build(int size = OwnerScale)
    {
        var random = new Random(180);
        var songs = new List<LibrarySongEntry>(size + size / 20);
        var artistIndex = 0;
        while (songs.Count < size)
        {
            var artist = Artists[artistIndex % Artists.Length];
            if (artistIndex >= Artists.Length) artist = $"{artist} ({artistIndex / Artists.Length + 1})";
            artistIndex++;

            // A skewed run: most artists carry a handful of songs, a few carry dozens.
            var run = random.Next(100) switch { < 45 => random.Next(1, 4), < 80 => random.Next(4, 10), < 95 => random.Next(10, 25), _ => random.Next(25, 60) };
            var album = $"{TitleWords[random.Next(TitleWords.Length)]} {TitleWords[random.Next(TitleWords.Length)]}";
            for (var n = 0; n < run && songs.Count < size; n++)
            {
                var i = songs.Count;
                if (n % 8 == 7) album = $"{TitleWords[random.Next(TitleWords.Length)]} {TitleWords[random.Next(TitleWords.Length)]}";
                songs.Add(Song(i, artist, album, Title(random, i), random));
            }
        }

        // ~5 % byte-copy duplicates under a second file name, as a real library has.
        songs.AddRange(songs.Where((_, i) => i % 20 == 0).Select(s => new LibrarySongEntry
        {
            SongKey = s.SongKey, Charter = s.Charter, Version = s.Version, Title = s.Title, Artist = s.Artist,
            Album = s.Album, Year = s.Year, SongLength = s.SongLength, TuningSummary = s.TuningSummary,
            CapoSummary = s.CapoSummary, Arrangements = s.Arrangements, Difficulty = s.Difficulty,
            PackagePath = s.PackagePath.Replace("_p.psarc", "_copy_p.psarc", StringComparison.Ordinal)
        }).ToList());
        return songs;
    }

    private static string Title(Random random, int i)
    {
        var words = random.Next(1, 4);
        var title = string.Join(' ', Enumerable.Range(0, words).Select(_ => TitleWords[random.Next(TitleWords.Length)]));
        return (i % 97) switch
        {
            // Long enough that the row and the rail have to trim or wrap it.
            0 => $"{title} (Live At The Royal Albert Hall, London, 12th Of November 1997 — Remastered Deluxe Edition)",
            13 => $"{title} [feat. Somebody Very Famous & Their Entire Orchestra]",
            29 => $"{title}・夜明けのメロディー",
            41 => $"{title} – Ünïcödé Édition",
            _ => title
        };
    }

    private static LibrarySongEntry Song(int i, string artist, string album, string title, Random random)
    {
        var arrangements = new List<LibraryArrangementEntry> { new() { ArrangementName = "Lead", SngPath = $"songs/bin/generic/s{i}_lead.sng" } };
        if (i % 2 == 0) arrangements.Add(new LibraryArrangementEntry { ArrangementName = "Rhythm", SngPath = $"songs/bin/generic/s{i}_rhythm.sng" });
        if (i % 3 == 0) arrangements.Add(new LibraryArrangementEntry { ArrangementName = "Bass", SngPath = $"songs/bin/generic/s{i}_bass.sng" });
        if (i % 17 == 0) arrangements.Add(new LibraryArrangementEntry { ArrangementName = "Vocals", SngPath = $"songs/bin/generic/s{i}_vocals.sng" });
        var safeArtist = string.Concat(artist.Where(char.IsLetterOrDigit));
        var charter = Charters[i % Charters.Length];
        return new LibrarySongEntry
        {
            SongKey = $"{safeArtist}{i:D5}",
            Charter = charter,
            Version = charter.Length > 0 ? "1.0" : "",
            Title = title,
            Artist = artist,
            Album = album,
            Year = i % 23 == 0 ? 0 : 1965 + random.Next(60),
            SongLength = i % 31 == 0 ? 0 : 90 + random.Next(480),
            Difficulty = i % 5 == 0 ? 0 : Math.Round(1 + random.NextDouble() * 9, 1),
            TuningSummary = Tunings[random.Next(Tunings.Length)],
            CapoSummary = i % 11 == 0 ? 2 : 0,
            PackagePath = $@"E:\Rocksmith\dlc\{safeArtist}\{safeArtist}-song{i:D5}_p.psarc",
            Arrangements = arrangements
        };
    }
}
