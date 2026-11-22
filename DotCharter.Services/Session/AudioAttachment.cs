using DotCharter.Tone.Audio;

namespace DotCharter.Services.Session;

/// <summary>Validates an attachment and commits its file, reference and engine in one engine turn.</summary>
internal sealed class AudioAttachment(IAudioPlaybackService audio, AudioLoadQueue queue, IAppLogger logger, Action clearAudio, string refused)
{
    private sealed record Prepared(string Input, string Destination, string? Stage, NativeFileBuffer? Bytes, WaveformSamples Waveform);

    public async Task<bool> LoadAsync(string source, string destination, Func<bool> mayCommit,
        Func<Task> restorePlayback, Action<WaveformSamples> commit)
    {
        var copying = !string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);
        var stage = copying ? destination + "." + Guid.NewGuid().ToString("N") + ".attach" + Path.GetExtension(destination) : null;
        NativeFileBuffer? bytes = null;
        try
        {
            if (stage != null) await Task.Run(() => File.Copy(source, stage, overwrite: false));
            var input = stage ?? source;
            var waveform = await audio.GenerateWaveformAsync(input);
            if (!mayCommit()) return false;
            if (!waveform.HasData) throw new InvalidDataException(refused);
            bytes = await NativeFileBuffer.ReadAsync(input);
            var prepared = new Prepared(input, destination, stage, bytes, waveform);
            return await queue.RunAsync(() => LoadPreparedAsync(prepared, mayCommit, restorePlayback, commit));
        }
        finally
        {
            bytes?.Release();
            if (stage != null) BestEffortDelete.File(stage);
        }
    }

    private async Task<bool> LoadPreparedAsync(Prepared prepared, Func<bool> mayCommit,
        Func<Task> restorePlayback, Action<WaveformSamples> commit)
    {
        if (!mayCommit()) return false;
        using var publication = new AudioAttachmentPublication(prepared.Destination, prepared.Stage, logger);
        try
        {
            clearAudio();
            await DecodeAsync(prepared.Input, prepared.Destination, prepared.Bytes);
            if (mayCommit() && prepared.Stage != null)
            {
                if (prepared.Bytes == null) clearAudio();
                publication.Publish();
                // Very large files need a path-based reopen after publication.
                if (prepared.Bytes == null) await DecodeAsync(prepared.Destination, prepared.Destination, null);
            }
            if (!mayCommit())
            {
                audio.FreeMasterAudio();
                publication.RollBack();
                await restorePlayback();
                return false;
            }
            commit(prepared.Waveform);
            publication.Complete();
            return true;
        }
        catch (Exception failure)
        {
            try
            {
                audio.FreeMasterAudio();
                publication.RollBack();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(failure, rollbackFailure);
            }
            finally
            {
                await restorePlayback();
            }
            throw;
        }
    }

    private async Task DecodeAsync(string path, string displayPath, NativeFileBuffer? bytes)
    {
        if (bytes != null)
        {
            await audio.LoadAudioFromMemoryAsync(bytes.Memory, Path.GetExtension(displayPath).TrimStart('.').ToLowerInvariant(), displayPath);
        }
        else
        {
            await audio.LoadAudioAsync(path);
        }
        var length = audio.GetTotalAudioLengthMs();
        if (!double.IsFinite(length) || length <= 0) throw new InvalidDataException(refused);
    }
}
