namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The stem-models tab's three self-probing blocks, passed as one.</summary>
public sealed record StemModelsBlocks(
    StemBackendViewModel Backend,
    GpuRuntimeViewModel Gpu,
    StemCacheViewModel Cache);
