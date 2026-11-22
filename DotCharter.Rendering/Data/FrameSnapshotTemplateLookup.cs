namespace DotCharter.Rendering.Data;

/// <summary>Template lookup by ID for the renderers (kept out of the snapshot's partials).</summary>
public static class FrameSnapshotTemplateLookup
{
    /// <summary>Index into <see cref="FrameSnapshot.Templates" /> of the template whose <c>Id</c> is
    /// <paramref name="templateId" />, or -1. Ids stop being contiguous once a template
    /// is deleted, so the position is only a first guess, verified against the Id.</summary>
    public static int FindTemplateIndex(this FrameSnapshot snapshot, int templateId)
    {
        if (templateId < 0) return -1;
        if (templateId < snapshot.TemplateCount && snapshot.Templates[templateId].Id == templateId) return templateId;
        for (var i = 0; i < snapshot.TemplateCount; i++)
        {
            if (snapshot.Templates[i].Id == templateId) return i;
        }

        return -1;
    }
}
