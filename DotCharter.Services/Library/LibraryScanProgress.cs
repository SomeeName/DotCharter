namespace DotCharter.Services.Library;

public sealed record LibraryScanProgress(int ProcessedFiles, int TotalFiles, string CurrentFile, int SongsFound)
{
    public double Fraction => TotalFiles == 0 ? 0.0 : (double)ProcessedFiles / TotalFiles;
}
