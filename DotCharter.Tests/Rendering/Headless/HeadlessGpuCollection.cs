namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Serializes all headless GPU render tests (Vulkan + OpenGL). One real
///     graphics context at a time keeps GPU contention down and — for Vulkan —
///     stops two instances from racing on the process-wide
///     <c>VulkanValidationDiagnostics</c> static channel.
/// </summary>
[CollectionDefinition("HeadlessGpu", DisableParallelization = true)]
public class HeadlessGpuCollection;
