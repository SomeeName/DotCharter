namespace DotCharter.Services.Session;

/// <summary>Keeps the previous audio durable until the attachment has committed its reference.</summary>
internal sealed class AudioAttachmentPublication(string destination, string? stage, IAppLogger logger) : IDisposable
{
    private string? _backup;
    private bool _published;
    private bool _committed;
    private bool _preserveBackup;

    public void Publish()
    {
        if (stage == null) return;
        if (File.Exists(destination))
        {
            _backup = destination + "." + Guid.NewGuid().ToString("N") + ".previous";
            File.Copy(destination, _backup, overwrite: false);
            AtomicFile.FlushFile(_backup);
        }
        AtomicFile.Publish(stage, destination);
        _published = true;
    }

    public void RollBack()
    {
        if (!_published) return;
        try
        {
            var backup = _backup;
            if (backup == null)
            {
                File.Delete(destination);
            }
            else
            {
                // Publish consumes its input on failure; never hand it the only backup.
                AtomicFile.Write(destination, output =>
                {
                    using var original = File.OpenRead(backup);
                    original.CopyTo(output);
                });
            }
            _published = false;
        }
        catch (Exception ex)
        {
            _preserveBackup = true;
            logger.Error(AppLogCategory.Audio, $"Audio rollback failed; original retained at '{_backup}'.", ex);
            throw;
        }
    }

    public void Complete() => _committed = true;

    public void Dispose()
    {
        if (!_preserveBackup && (!_published || _committed) && _backup != null) BestEffortDelete.File(_backup);
    }
}
