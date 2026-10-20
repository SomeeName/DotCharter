using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
/// The stem mixer's Guitar roles switch: play the guitar stem as Lead
///     (what sits in the centre) and Rhythm (what is panned to the sides).
///     <para>
///         <b>A capability that cannot work never looks live.</b> The switch is
///         enabled only once the guitar stem's stereo image has been checked and can
///         be split; otherwise it stays visibly disabled with the reason printed
///         (no guitar stem, mono, everything centred, nothing centred, silent,
///         unreadable). The check is the cheap one
///         (<see cref="IGuitarRoleStems.Assess" />: a sidecar, or one streaming pass
///         of correlation); what the split itself finds is remembered beside the
///         stem, so a refusal is shown from the start the next time.
///     </para>
///     <para>
///         The mixer owns the substitution: it reads <c>ChartProject.GuitarRolesSplit</c>
///         and the derived pair on its probe worker and calls <see cref="Update" />
///         after every rebuild. This type flips the project flag and asks for a
///         rebuild; it never touches the engine.
///     </para>
/// </summary>
public sealed partial class GuitarRolesViewModel : ObservableObject
{
    private readonly IGuitarRoleStems? _stems;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger? _logger;
    private readonly Action _rebuildMixer;

    private ChartProject? _project;
    private string? _guitarPath;
    private GuitarRoleAssessment? _assessment;
    private string? _derivedOnceFor;
    private int _generation;
    private bool _syncing;

    public GuitarRolesViewModel(IGuitarRoleStems? stems, ILocalizationService localization, IUiDispatcher ui,
        Action rebuildMixer, IAppLogger? logger = null)
    {
        _stems = stems;
        _localization = localization;
        _ui = ui;
        _rebuildMixer = rebuildMixer;
        _logger = logger;
    }

    /// <summary>The row shows whenever the mixer has stems: with no guitar stem it
    /// still shows, disabled, saying why.</summary>
    [ObservableProperty] private bool _isShown;

    /// <summary>The switch is live: the image was checked and can be split.</summary>
    [ObservableProperty] private bool _canToggle;

    /// <summary>Two-way bound: the user's switch. True while the mixer plays the pair.</summary>
    [ObservableProperty] private bool _isOn;

    /// <summary>A check or a split is running.</summary>
    [ObservableProperty] private bool _isWorking;

    /// <summary>The reason it is disabled, the progress, or the measured split.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    /// <summary>The last verdict, for tests and diagnostics.</summary>
    internal GuitarRoleAssessment? Assessment => _assessment;

    /// <summary>
    ///     Called by the mixer on the UI thread at the end of every rebuild.
    /// </summary>
    /// <param name="project">The project the mixer shows.</param>
    /// <param name="hasStems">The mixer has stems at all.</param>
    /// <param name="guitarPath">The guitar stem's file, when the project has one on disk.</param>
    /// <param name="rolesActive">The engine plays the derived pair in its place.</param>
    internal void Update(ChartProject? project, bool hasStems, string? guitarPath, bool rolesActive)
    {
        if (!ReferenceEquals(project, _project) ||
            !string.Equals(guitarPath, _guitarPath, StringComparison.OrdinalIgnoreCase))
        {
            _generation++;
            _assessment = null;
            _derivedOnceFor = null;
            IsWorking = false;
        }

        _project = project;
        _guitarPath = guitarPath;
        IsShown = project != null && hasStems;
        if (!IsShown || project == null)
        {
            Disable(string.Empty);
            return;
        }

        if (_stems == null || guitarPath == null)
        {
            Disable(_localization["Mixer_GuitarRoles_NoGuitar"]);
            return;
        }

        if (rolesActive)
        {
            _derivedOnceFor = guitarPath;
            SetOn(true);
            CanToggle = true;
            Status = ResultText(_assessment);
            if (_assessment == null && !IsWorking) Check(guitarPath);
            return;
        }

        if (IsWorking) return;

        if (project.GuitarRolesSplit)
        {
            // On, but the pair is missing or stale (a re-separation, a cleaned
            // folder): derive it again from the guitar stem — once. A second miss
            // means the files cannot be kept, and retrying would loop.
            if (string.Equals(_derivedOnceFor, guitarPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Warning(AppLogCategory.Audio,
                    "Guitar roles: the derived stems are still missing after a fresh split; turning the split off.");
                project.GuitarRolesSplit = false;
                Disable(_localization["Mixer_GuitarRoles_Unreadable"]);
                return;
            }

            Split(project, guitarPath);
            return;
        }

        SetOn(false);
        if (_assessment is { } known) ApplyVerdict(known);
        else Check(guitarPath);
    }

    partial void OnIsOnChanged(bool value)
    {
        if (_syncing) return;
        if (_project == null || _guitarPath == null || !CanToggle)
        {
            SetOn(!value);
            return;
        }

        if (value)
        {
            Split(_project, _guitarPath);
        }
        else
        {
            _project.GuitarRolesSplit = false;
            if (_assessment is { } known) ApplyVerdict(known);
            _rebuildMixer();
        }
    }

    private void Check(string guitarPath)
    {
        var generation = _generation;
        IsWorking = true;
        if (!IsOn)
        {
            // Playing the pair already proves the image splits; the check then only
            // fetches the numbers for the result line and must not grey the switch.
            CanToggle = false;
            Status = _localization["Mixer_GuitarRoles_Checking"];
        }
        _ = Task.Run(() =>
        {
            GuitarRoleAssessment assessment;
            try
            {
                assessment = _stems!.Assess(guitarPath);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"Guitar roles: the stereo check failed: {ex.Message}");
                assessment = new GuitarRoleAssessment(GuitarRoleVerdict.Unreadable, default, null);
            }

            _ui.PostNormal(() =>
            {
                if (generation != _generation) return;
                IsWorking = false;
                _assessment = assessment;
                if (IsOn)
                {
                    CanToggle = true;
                    Status = ResultText(assessment);
                }
                else
                {
                    ApplyVerdict(assessment);
                }
            });
        });
    }

    private void Split(ChartProject project, string guitarPath)
    {
        var generation = _generation;
        _derivedOnceFor = guitarPath;
        IsWorking = true;
        CanToggle = false;
        SetOn(true);
        Status = _localization.Format("Mixer_GuitarRoles_SplittingFmt", PercentText.Format(0, 0));
        var progress = new CoalescedProgress(this, generation);
        _ = Task.Run(() =>
        {
            GuitarRoleDerivation result;
            try
            {
                result = _stems!.Derive(guitarPath, progress);
            }
            catch (Exception ex)
            {
                _logger?.Error(AppLogCategory.Audio, "Guitar roles: the split failed.", ex);
                result = new GuitarRoleDerivation(new GuitarRoleAssessment(GuitarRoleVerdict.Unreadable, default, null), null);
            }

            _ui.PostNormal(() =>
            {
                if (generation != _generation || !ReferenceEquals(project, _project)) return;
                IsWorking = false;
                _assessment = result.Assessment;
                if (result.Pair != null)
                {
                    project.GuitarRolesSplit = true;
                    CanToggle = true;
                    Status = ResultText(result.Assessment);
                    _rebuildMixer();
                }
                else
                {
                    project.GuitarRolesSplit = false;
                    SetOn(false);
                    ApplyVerdict(result.Assessment);
                }
            });
        });
    }

    private void ApplyVerdict(GuitarRoleAssessment assessment)
    {
        CanToggle = assessment.CanSplit;
        Status = assessment.CanSplit ? ResultText(assessment) : ReasonText(assessment.Verdict);
    }

    private void Disable(string reason)
    {
        SetOn(false);
        CanToggle = false;
        IsWorking = false;
        Status = reason;
    }

    private void SetOn(bool value)
    {
        _syncing = true;
        try { IsOn = value; }
        finally { _syncing = false; }
    }

    private string ResultText(GuitarRoleAssessment? assessment) =>
        assessment is { Verdict: GuitarRoleVerdict.Splittable, LeadShare: { } lead }
            ? _localization.Format("Mixer_GuitarRoles_ResultFmt", PercentText.Format(lead, 0), PercentText.Format(1 - lead, 0))
            : string.Empty;

    /// <summary>The reason a verdict refuses the split; the key per verdict is pinned by
    /// <c>GuitarRolesViewModelTests</c>.</summary>
    internal string ReasonText(GuitarRoleVerdict verdict) => verdict switch
    {
        GuitarRoleVerdict.Mono => _localization["Mixer_GuitarRoles_Mono"],
        GuitarRoleVerdict.Silent => _localization["Mixer_GuitarRoles_Silent"],
        GuitarRoleVerdict.AllCentred => _localization["Mixer_GuitarRoles_AllCentred"],
        GuitarRoleVerdict.NothingCentred => _localization["Mixer_GuitarRoles_NothingCentred"],
        GuitarRoleVerdict.Unreadable => _localization["Mixer_GuitarRoles_Unreadable"],
        _ => string.Empty,
    };

    /// <summary>Progress from the worker, posted to the UI only when the whole
    /// percentage changes.</summary>
    private sealed class CoalescedProgress(GuitarRolesViewModel owner, int generation) : IProgress<double>
    {
        private int _last = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Clamp(value * 100, 0, 100);
            if (Interlocked.Exchange(ref _last, percent) == percent) return;
            owner._ui.Post(() =>
            {
                if (generation != owner._generation || !owner.IsWorking) return;
                owner.Status = owner._localization.Format("Mixer_GuitarRoles_SplittingFmt", PercentText.Format(percent / 100.0, 0));
            });
        }
    }
}
