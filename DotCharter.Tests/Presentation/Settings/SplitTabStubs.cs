using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using DotCharter.Localization;
using DotCharter.Services.Infrastructure.Backup;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>The seams the four tabs split out of General take, as recording stubs.</summary>
public static class SplitTabStubs
{
    /// <summary>
    ///     The real English strings, but a culture write is RECORDED rather than
    ///     applied: the app's localization is process-wide, and a test that moved
    ///     it would race every other class in the gate.
    /// </summary>
    internal sealed class RecordingLocalization : ILocalizationService
    {
        private static ILocalizationService Real => LocalizationService.Instance;

        public List<string?> CulturesSet { get; } = [];

        public IReadOnlyList<LanguageOption> LanguageOptions => Real.LanguageOptions;
        public string ConfiguredCulture => Real.ConfiguredCulture;
        public CultureInfo Culture => Real.Culture;
        public bool IsRightToLeft => false;
        public string this[string key] => Real[key];
        public string Format(string key, params object[] args) => Real.Format(key, args);
        public string Plural(string oneKey, string manyKey, int count, params object[] extra) => Real.Plural(oneKey, manyKey, count, extra);
        public string PluralWith(string oneKey, string manyKey, int count, params object[] args) => Real.PluralWith(oneKey, manyKey, count, args);
        public void SetConfiguredCulture(string? configValue) => CulturesSet.Add(configValue);
        public void LoadCommunityLanguages(string folder) { }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    internal sealed class FakeSetup : IFirstRunSetupPrompt
    {
        public int Opened { get; private set; }
        public void Open() => Opened++;
    }

    internal sealed class FakeFiles : IFileStorageService
    {
        public string? Answer { get; set; }
        public List<string> Asked { get; } = [];

        public Task<string?> OpenFilePickerAsync(string title, IReadOnlyList<FilePickerFilter>? filters = null,
            string? startingDirectory = null)
        {
            Asked.Add("open:" + title);
            return Task.FromResult(Answer);
        }

        public Task<string?> OpenFolderPickerAsync(string title, string? startingDirectory = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> SaveFilePickerAsync(string title, string suggestedFileName,
            IReadOnlyList<FilePickerFilter>? filters = null, string? startingDirectory = null)
        {
            Asked.Add("save:" + suggestedFileName);
            return Task.FromResult(Answer);
        }
    }

    internal sealed class FakeBackup : IConfigBackupService
    {
        public List<string> Calls { get; } = [];
        public Exception? Throw { get; set; }
        public IReadOnlyList<string> NotRestoredSettings { get; set; } = [];

        public ConfigBackupResult Export(string archivePath)
        {
            Calls.Add("export:" + archivePath);
            if (Throw is not null) throw Throw;
            return new ConfigBackupResult(12, []);
        }

        public ConfigBackupResult Import(string archivePath)
        {
            Calls.Add("import:" + archivePath);
            if (Throw is not null) throw Throw;
            return new ConfigBackupResult(7, ["credentials.json"]) { NotRestoredSettings = NotRestoredSettings };
        }
    }

    /// <summary>Records every call by method name; a stand-in for a wide service the tab touches in one or two places.</summary>
    public class CallRecorder : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod?.Name ?? "?");
            var type = targetMethod?.ReturnType;
            return type is null || type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }

    public static T Recorder<T>() where T : class => DispatchProxy.Create<T, CallRecorder>();

    public static List<string> CallsOf(object recorder) => ((CallRecorder)recorder).Calls;
}
