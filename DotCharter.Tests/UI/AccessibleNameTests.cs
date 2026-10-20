using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>An action control a screen reader cannot NAME is an action nobody can
///     find.</b> Every <c>Button</c>/<c>ToggleButton</c>/<c>RadioButton</c>… in
///     <c>DotCharter.UI/Views</c> whose face is a <c>PathIcon</c> or a bare glyph
///     carries an <c>AutomationProperties.Name</c>.
///     <para>
///         This is the accessibility twin of every other silent-XAML-fault lint
///         in this repo: an unnamed icon button builds, renders, looks right, and
///         reaches assistive technology and every automation tool as
///         <c>Button ""</c>. Nothing warns. Nothing throws. The control simply has
///         no identity — and a control with no identity cannot be operated,
///         scripted or reported on.
///     </para>
///     <para>
///         <b>A <c>ToolTip.Tip</c> is deliberately NOT accepted as a name.</b> A
///         tooltip is announced on HOVER, which is a pointer gesture — exactly the
///         gesture the user who needs the name does not have. 35 of the 37
///         controls this lint found on its first run already carried one, which is
///         precisely why accepting them would have made the whole guard vacuous.
///         The fix is to carry BOTH, off the same resource key.
///         <see cref="ATooltipIsNeverAnAccessibleName" /> pins that.
///     </para>
///     <para>
///         Scope is <c>Views/**</c>. <c>Styles/</c> is where control TEMPLATES are
///         declared and a template's button is named by its templated parent, so
///         it is not scanned — and markup nested under a <c>Style</c>/
///         <c>ControlTheme</c>/<c>Setter</c> inside a view is skipped for the same
///         reason.
///     </para>
/// </summary>
public sealed class AccessibleNameTests
{
    /// <summary>
    ///     Controls that DO something when pressed — and, since 0.61, the two
    ///     that draw their own caption beside a box or a bullet.
    ///     <para>
    ///         <b>A <c>CheckBox</c>/<c>RadioButton</c>/<c>ListBoxItem</c> belongs
    ///         HERE rather than with the value controls because its
    ///         <c>Content</c> IS its name</b> — the caption is drawn on the
    ///         control, so a screen reader already announces it. A
    ///         <c>ComboBox</c>/<c>Slider</c>/<c>TextBox</c> draws no caption ever,
    ///         which is why those take the unconditional bar in
    ///         <see cref="EveryRowInputControl_CarriesAnAccessibleName" />. The
    ///         split is the whole reason 75 CheckBoxes produced only four
    ///         findings: 71 of them carry real words on their face.
    ///     </para>
    ///     <para>
    ///         <c>ListBoxItem</c> is in the list for the future rather than the
    ///         present: <c>Views/</c> declares NONE literally (measured
    ///         2026-09-04) — every list's rows come out of an
    ///         <c>ItemTemplate</c>, and the LIST is what carries the name. A
    ///         literal one added later is covered without a further change here.
    ///     </para>
    /// </summary>
    private static readonly string[] ActionControls =
    [
        "Button", "ToggleButton", "RadioButton", "RepeatButton", "SplitButton", "DropDownButton",
        "CheckBox", "ListBoxItem"
    ];

    private static readonly string[] TextCarriers =
        ["TextBlock", "AccessText", "Label", "SelectableTextBlock"];

    /// <summary>Markup under one of these is a TEMPLATE body, not a live control.</summary>
    private static readonly string[] TemplateHosts = ["Style", "ControlTheme", "Setter"];

    private static readonly string[] NameProperties =
        ["AutomationProperties.Name", "AutomationProperties.LabeledBy"];

    /// <summary>
    ///     Every exemption states WHY. An entry with no reason is a hole in the
    ///     lint, not an exception to it — the point of the list is that adding to
    ///     it costs an argument, and
    ///     <see cref="EveryExemption_NamesAFileThatStillNeedsIt" /> deletes it the
    ///     moment it stops being true.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.OrdinalIgnoreCase);
    // SettingsView.axaml's search-clear button was the last entry here — named
    // off Action_Clear (2026-08-11), the same shape as the other 36, so the
    // icon-button list is EMPTY now and any addition costs an argument.

    /// <summary>
    ///     Value controls that take their meaning from the ROW they sit in. They
    ///     carry no readable face of their own — a <c>ComboBox</c> shows its
    ///     current VALUE, not what it is for, a <c>TextBox</c> shows what YOU
    ///     typed, and a <c>Slider</c> shows nothing at all — so the row's label
    ///     is the name, off the SAME resource key (or, where the label is data
    ///     rather than a key, the same binding).
    ///     <para>
    ///         <b>Three of these were the whole list until 0.61</b>
    ///         (<c>ComboBox</c>/<c>Slider</c>/<c>ToggleSwitch</c>), and the
    ///         editor design pass counted the cost: 98 unnamed value controls
    ///         across 34 views that this lint watched the whole time and could
    ///         not see, because a <c>TextBox</c> is not a <c>ComboBox</c>. The
    ///         bar for every one of them is the same and always was.
    ///     </para>
    ///     <para>
    ///         <c>MaskedTextBox</c>, <c>DatePicker</c>, <c>TimePicker</c>,
    ///         <c>CalendarDatePicker</c> and <c>ColorView</c> appear in no view
    ///         today (measured 2026-09-04) and are listed anyway: the cost of a
    ///         name that is never matched is zero, and the cost of noticing the
    ///         first one six months late is a re-run of this whole sweep.
    ///     </para>
    /// </summary>
    private static readonly string[] RowInputControls =
    [
        "ComboBox", "Slider", "ToggleSwitch",
        "TextBox", "NumericUpDown", "MaskedTextBox", "AutoCompleteBox", "DeferredAutoCompleteBox", "DeferredChoiceBox", "ListBox",
        "DatePicker", "TimePicker", "CalendarDatePicker", "ColorPicker", "ColorView"
    ];

    /// <summary>
    ///     Same contract as <see cref="Exempt" />: every entry states WHY, and
    ///     <see cref="EveryRowInputExemption_NamesAFileThatStillNeedsIt" />
    ///     deletes it the moment it stops being true. EMPTY since 2026-08-11 for
    ///     Settings, and **EMPTY REPO-WIDE since 2026-08-15**, when the last 125
    ///     unnamed controls under <c>Views/</c> were named. An entry here is not
    ///     an exception to the rule — it is a hole in it, and the list only ever
    ///     shrinks.
    ///     <para>
    ///         <b>It stayed empty through the 0.61 widening</b>, and the two
    ///         cases that argued hardest for an entry both turned out to need
    ///         none:
    ///     </para>
    ///     <para>
    ///         A READ-ONLY readout with a visible caption — the chart-diff
    ///         dialog's compared-file box — is not exempt: its caption is a Loc
    ///         key sitting one element away, so it gets the name off that key
    ///         like every other row. "It is only a readout" is an argument about
    ///         whether the control matters, not about whether it can be named.
    ///     </para>
    ///     <para>
    ///         A SEARCH box with no label row — the palette, Settings, the two
    ///         song lists, the phrase catalogue — takes its name from its
    ///         <c>PlaceholderText</c>/<c>Watermark</c> key, and ONLY because that
    ///         watermark is a Loc key in every one of them: the name is then the
    ///         same authored, translated string the sighted user reads in the
    ///         empty box, not a second string invented here. A watermark that was
    ///         a hard-coded literal would name nothing in German or Japanese, and
    ///         the one box whose watermark IS a literal — the custom-model URL in
    ///         <c>StemModelsSettingsTab</c>, whose placeholder is an example URL
    ///         — is named off its section title instead.
    ///     </para>
    /// </summary>
    private static readonly Dictionary<string, string> RowInputExempt = new(StringComparer.OrdinalIgnoreCase);

    // ── the lint ──────────────────────────────────────────────────────────

    /// <summary>
    ///     <b>A value control with no name announces as <c>ComboBox ""</c>.</b>
    ///     Unlike the icon-button lint below, nothing on these controls' faces
    ///     could ever name them — a ComboBox shows its current VALUE, a TextBox
    ///     shows what you typed into it and a Slider shows nothing at all, while
    ///     what the control is FOR lives in the row's label beside it. So the bar
    ///     is unconditional: every <c>ComboBox</c>/<c>Slider</c>/
    ///     <c>ToggleSwitch</c>/<c>TextBox</c>/<c>NumericUpDown</c>/<c>ListBox</c>/
    ///     <c>AutoCompleteBox</c>/<c>ColorPicker</c>… anywhere under
    ///     <c>DotCharter.UI/Views</c> carries an <c>AutomationProperties.Name</c>
    ///     (or <c>LabeledBy</c>), off the same resource key as its visible label
    ///     — or, where the label is data rather than a key, off the same binding.
    ///     <para>
    ///         <b>This was <c>Views/Settings</c> only until 2026-08-15</b>, with
    ///         the rest of the app carried as a standing backlog in
    ///         <c>docs/agents/ui/misc-surfaces/diagnostics-rtl-scale-palette.md</c>
    ///         — 125 controls when it was re-measured, 56 of them in
    ///         <c>PlayModeView.axaml</c> alone. They are all named now, so the
    ///         scope is the whole of <c>Views/</c> and
    ///         <see cref="RowInputExempt" /> is EMPTY: the backlog cannot come
    ///         back one control at a time.
    ///     </para>
    ///     <para>
    ///         <b>…and it covered only three control TYPES until 0.61</b>, which
    ///         is the same hole one axis over: <c>Views/</c> held <b>98</b>
    ///         unnamed value controls across 34 files while this lint ran green
    ///         on every one of those files — 50 <c>TextBox</c>, 23
    ///         <c>ListBox</c>, 18 <c>NumericUpDown</c>, 5 <c>AutoCompleteBox</c>,
    ///         2 <c>ColorPicker</c> — plus four <c>CheckBox</c>es with neither a
    ///         caption nor a name, which the action-control fact now catches.
    ///         All 102 are named; the counts are 0/411 and 0/864 as of
    ///         2026-09-04. <b>A new input control type gets added to
    ///         <see cref="RowInputControls" /> when it is first used, not after
    ///         the next sweep counts it.</b>
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryRowInputControl_CarriesAnAccessibleName()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        Assert.True(Directory.Exists(views), $"Views not found at {views}");

        var scanned = 0;
        var inspected = 0;
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (RowInputExempt.ContainsKey(name)) continue;

            scanned++;
            foreach (var control in RowInputControlsIn(XDocument.Load(file, LoadOptions.SetLineInfo)))
            {
                inspected++;
                if (HasAccessibleName(control)) continue;

                var line = ((IXmlLineInfo)control).HasLineInfo()
                    ? ((IXmlLineInfo)control).LineNumber.ToString(CultureInfo.InvariantCulture)
                    : "?";
                offenders.Add($"{name}:{line}  <{control.Name.LocalName}>");
            }
        }

        // The scan must not pass by finding nothing to read.
        Assert.True(scanned > 80, $"only {scanned} views were scanned — the lint is looking in the wrong place");
        Assert.True(inspected > 380, $"only {inspected} row input controls were reached — the walk is broken");

        Assert.True(offenders.Count == 0,
            "These value controls have no accessible name, so they reach a screen reader — and every " +
            "automation tool — as an unnamed control. The row's label is the name: add " +
            "AutomationProperties.Name off the same resource key (or the same binding, where the label " +
            "is data):\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Mirror of <see cref="EveryExemption_NamesAFileThatStillNeedsIt" />
    /// for the row-input list: a stale entry silently covers whatever is added to
    /// that file next. The list is EMPTY and only ever shrinks, so this is the
    /// guard that keeps it that way.</summary>
    [Fact]
    public void EveryRowInputExemption_NamesAFileThatStillNeedsIt()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));

        foreach (var (name, reason) in RowInputExempt)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name} is exempted with no stated reason");

            var file = UiSourceTree.EnumerateFiles(views, name, SearchOption.AllDirectories).FirstOrDefault();
            Assert.True(file != null, $"{name} is exempted but no longer exists — drop the entry");

            var stillOffends = RowInputControlsIn(XDocument.Load(file!)).Any(c => !HasAccessibleName(c));

            Assert.True(stillOffends,
                $"{name} is exempted but every row input control in it is named now — drop the entry, or " +
                "the exemption silently covers whatever is added to that file next.");
        }
    }

    [Fact]
    public void EveryIconOnlyActionControl_CarriesAnAccessibleName()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        Assert.True(Directory.Exists(views), $"Views not found at {views}");

        var scanned = 0;
        var inspected = 0;
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Exempt.ContainsKey(name)) continue;

            scanned++;
            foreach (var control in ActionControlsIn(XDocument.Load(file, LoadOptions.SetLineInfo)))
            {
                inspected++;
                if (HasAccessibleName(control)) continue;
                if (CarriesReadableText(control)) continue;

                var line = ((IXmlLineInfo)control).HasLineInfo()
                    ? ((IXmlLineInfo)control).LineNumber.ToString(CultureInfo.InvariantCulture)
                    : "?";
                offenders.Add($"{name}:{line}  <{control.Name.LocalName} " +
                              $"Classes=\"{control.Attribute("Classes")?.Value ?? "-"}\" " +
                              $"Command=\"{control.Attribute("Command")?.Value ?? "-"}\">");
            }
        }

        // The scan must not pass by finding nothing to read.
        Assert.True(scanned > 80, $"only {scanned} views were scanned — the lint is looking in the wrong place");
        Assert.True(inspected > 800, $"only {inspected} action controls were reached — the walk is broken");

        Assert.True(offenders.Count == 0,
            "These action controls have no accessible name and no readable text on their face, so they " +
            "reach a screen reader — and every automation tool — as an unnamed button. A ToolTip.Tip is " +
            "NOT a name: it is announced on hover, which is the one gesture this user does not have. Add " +
            "AutomationProperties.Name off the same resource key:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     …and the exemption list cannot rot: an entry naming a file that no
    ///     longer exists, or one that no longer has an unnamed control, is a hole
    ///     that silently re-opens the whole file.
    /// </summary>
    [Fact]
    public void EveryExemption_NamesAFileThatStillNeedsIt()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));

        foreach (var (name, reason) in Exempt)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name} is exempted with no stated reason");

            var file = UiSourceTree.EnumerateFiles(views, name, SearchOption.AllDirectories).FirstOrDefault();
            Assert.True(file != null, $"{name} is exempted but no longer exists — drop the entry");

            var stillOffends = ActionControlsIn(XDocument.Load(file!))
                .Any(c => !HasAccessibleName(c) && !CarriesReadableText(c));

            Assert.True(stillOffends,
                $"{name} is exempted but every action control in it is named now — drop the entry, or " +
                "the exemption silently covers whatever is added to that file next.");
        }
    }

    /// <summary>
    ///     <b>The non-vacuity proof, in the gate rather than in a commit
    ///     message.</b> The predicate pair is exercised against the four shapes
    ///     that matter — and the tooltip case is the one that decides whether this
    ///     lint means anything at all, because almost every control it found
    ///     already had one.
    /// </summary>
    [Theory]
    // An icon button with nothing but a tooltip: a FINDING.
    [InlineData("""<Button ToolTip.Tip="{localization:Loc X}"><PathIcon Data="{StaticResource I}" /></Button>""", true)]
    // The same button once it is named: clean.
    [InlineData("""<Button AutomationProperties.Name="{localization:Loc X}" ToolTip.Tip="{localization:Loc X}"><PathIcon Data="{StaticResource I}" /></Button>""", false)]
    // Named through a property element (the MultiBinding shape): clean.
    [InlineData("""<Button><AutomationProperties.Name><MultiBinding /></AutomationProperties.Name><PathIcon /></Button>""", false)]
    // Real words on its face name it already: clean.
    [InlineData("""<Button Content="{localization:Loc Action_Delete}" />""", false)]
    // …but a bare GLYPH is not words. "—", "+", "✕" name nothing.
    [InlineData("""<Button Content="—" ToolTip.Tip="{localization:Loc X}" />""", true)]
    [InlineData("""<Button Content="✕" />""", true)]
    // …and neither is a lone LETTER: the mixer's "M"/"S" are Mute and Solo only
    // to a reader who can see the fader beside them. A tooltip is not the fix.
    [InlineData("""<ToggleButton Content="M" ToolTip.Tip="{localization:Loc Inspector_Mute}" />""", true)]
    [InlineData("""<ToggleButton Content="S" />""", true)]
    [InlineData("""<ToggleButton><TextBlock Text="H" /></ToggleButton>""", true)]
    // Two letters is a word a reader can hold onto; a NUMBER is its own label.
    [InlineData("""<Button Content="OK" />""", false)]
    [InlineData("""<Button Content="+1" />""", false)]
    [InlineData("""<Button Content="14" />""", false)]
    // A text child names it; an icon-named resource does not.
    [InlineData("""<Button><TextBlock Text="{localization:Loc Action_Delete}" /></Button>""", false)]
    [InlineData("""<Button Content="{DynamicResource IconClose}" />""", true)]
    // Words inside a PANEL do not name it: Avalonia names that button after the panel.
    [InlineData("""<Button><StackPanel><PathIcon /><TextBlock Text="{localization:Loc Toolbar_Mix}" /></StackPanel></Button>""", true)]
    [InlineData("""<Button AutomationProperties.Name="{localization:Loc Toolbar_Mix}"><StackPanel><PathIcon /><TextBlock Text="{localization:Loc Toolbar_Mix}" /></StackPanel></Button>""", false)]
    // A star/no-star pair drawn as two glyph TextBlocks is still unnamed.
    [InlineData("""<Button><Panel><TextBlock Text="★" /><TextBlock Text="☆" /></Panel></Button>""", true)]
    // A CheckBox with a caption names itself; a BARE one — the box alone, with
    // the words sitting in the row's label element beside it — does not, and a
    // tooltip does not rescue it any more than it rescues a button.
    [InlineData("""<CheckBox Content="{localization:Loc Settings_Mono}" IsChecked="{Binding Mono}" />""", false)]
    [InlineData("""<CheckBox IsChecked="{Binding IsAnchor}" />""", true)]
    [InlineData("""<CheckBox IsChecked="{Binding Resolved}" ToolTip.Tip="{localization:Loc Notes_Resolved}" />""", true)]
    [InlineData("""<CheckBox IsChecked="{Binding Resolved}" AutomationProperties.Name="{localization:Loc Notes_Resolved}" />""", false)]
    public void ATooltipIsNeverAnAccessibleName(string markup, bool isFinding)
    {
        var control = XElement.Parse(markup);
        Assert.Equal(isFinding, !HasAccessibleName(control) && !CarriesReadableText(control));
    }

    /// <summary>A template body is not a live control — its button is named by
    /// whatever the template is applied to, so the walk must not descend into
    /// one and then report the theme.</summary>
    [Fact]
    public void AButtonInsideAControlTemplate_IsNotWalked()
    {
        var doc = XDocument.Parse("""
                                  <UserControl>
                                    <UserControl.Styles>
                                      <Style Selector="Button.X">
                                        <Setter Property="Template">
                                          <ControlTemplate><Button /></ControlTemplate>
                                        </Setter>
                                      </Style>
                                    </UserControl.Styles>
                                    <Button />
                                  </UserControl>
                                  """);

        Assert.Single(ActionControlsIn(doc));
    }

    /// <summary>
    ///     <b>The non-vacuity proof for the row-input half.</b> The bar there is
    ///     UNCONDITIONAL — no readable-face escape hatch — because a ComboBox's
    ///     face is its current value and a Slider's is nothing at all. So a
    ///     ComboBox with items, a tooltip and a placeholder is still a finding,
    ///     and only an actual name clears it.
    /// </summary>
    [Theory]
    // Bare, and the three near-misses that name nothing: a tooltip (hover only),
    // a placeholder (gone the moment a value exists) and readable ITEMS (which
    // name the options, never the control).
    [InlineData("""<ComboBox SelectedIndex="{Binding X}" />""", true)]
    [InlineData("""<Slider Value="{Binding X}" ToolTip.Tip="{localization:Loc X}" />""", true)]
    [InlineData("""<ComboBox PlaceholderText="{localization:Loc X}" />""", true)]
    [InlineData("""<ComboBox><ComboBoxItem Content="{localization:Loc Action_Delete}" /></ComboBox>""", true)]
    [InlineData("""<ToggleSwitch IsChecked="{Binding X}" />""", true)]
    // The 0.61 additions, and the near-misses that each one shipped with. A
    // TextBox's own TEXT is the user's data and its watermark is gone the moment
    // they type, so neither names it — the row's label does, and where there is
    // no row a watermark that is a Loc key is carried across as the NAME too.
    [InlineData("""<TextBox Text="{Binding Artist}" />""", true)]
    [InlineData("""<TextBox Text="{Binding Q}" PlaceholderText="{localization:Loc X}" />""", true)]
    [InlineData("""<TextBox Text="{Binding Q}" AutomationProperties.Name="{localization:Loc X}" PlaceholderText="{localization:Loc X}" />""", false)]
    // A read-only readout is still a control a reader lands on.
    [InlineData("""<TextBox Text="{Binding ComparedFileName}" IsReadOnly="True" />""", true)]
    // A spinner shows a NUMBER; the unit beside it is not its name either.
    [InlineData("""<NumericUpDown Value="{Binding Ms}" Minimum="0" Maximum="1000" />""", true)]
    [InlineData("""<NumericUpDown Value="{Binding Ms}" AutomationProperties.Name="{localization:Loc Settings_Milliseconds}" />""", false)]
    // A list is named by what it is a list OF — never by its rows, and never by
    // the empty-state sentence that is showing only while it has none.
    [InlineData("""<ListBox ItemsSource="{Binding Sections}" />""", true)]
    // …and an x:Name is a CODE handle, not an announcement.
    [InlineData("""<ListBox Name="SongList" ItemsSource="{Binding Songs}" />""", true)]
    [InlineData("""<ListBox ItemsSource="{Binding Sections}" AutomationProperties.Name="{localization:Loc Inspector_Sections}" />""", false)]
    [InlineData("""<AutoCompleteBox Text="{Binding ToneA}" FilterMode="Contains" />""", true)]
    [InlineData("""<ColorPicker Color="{Binding TrackColor}" />""", true)]
    [InlineData("""<ColorPicker Color="{Binding Color}" AutomationProperties.Name="{Binding DisplayName}" />""", false)]
    // Named, the three ways the app does it: a resource key, the row's label
    // element, and — where the label is DATA — the same binding the label uses.
    [InlineData("""<ComboBox AutomationProperties.Name="{localization:Loc Dialog_Type}" />""", false)]
    [InlineData("""<Slider AutomationProperties.LabeledBy="{Binding #RowLabel}" />""", false)]
    [InlineData("""<ComboBox AutomationProperties.Name="{Binding DisplayName}" />""", false)]
    public void ARowInputControlIsNamedOnlyByAnAutomationProperty(string markup, bool isFinding)
        => Assert.Equal(isFinding, !HasAccessibleName(XElement.Parse(markup)));

    /// <summary>A ComboBox declared inside a <c>ControlTheme</c> is part of some
    /// control's TEMPLATE — named by whatever the template is applied to — so the
    /// row-input walk must not descend into one either.</summary>
    [Fact]
    public void ARowInputControlInsideAControlTheme_IsNotWalked()
    {
        var doc = XDocument.Parse("""
                                  <UserControl>
                                    <UserControl.Resources>
                                      <ControlTheme TargetType="NumericUpDown">
                                        <Setter Property="Template">
                                          <ControlTemplate><Slider /></ControlTemplate>
                                        </Setter>
                                      </ControlTheme>
                                    </UserControl.Resources>
                                    <ComboBox AutomationProperties.Name="{localization:Loc X}" />
                                  </UserControl>
                                  """);

        Assert.Single(RowInputControlsIn(doc));
    }

    // ── the predicates ────────────────────────────────────────────────────

    private static IEnumerable<XElement> ActionControlsIn(XDocument doc) =>
        doc.Descendants()
            .Where(e => ActionControls.Contains(e.Name.LocalName, StringComparer.Ordinal))
            .Where(e => !e.Ancestors().Any(a => TemplateHosts.Contains(a.Name.LocalName, StringComparer.Ordinal)));

    private static IEnumerable<XElement> RowInputControlsIn(XDocument doc) =>
        doc.Descendants()
            .Where(e => RowInputControls.Contains(e.Name.LocalName, StringComparer.Ordinal))
            .Where(e => !e.Ancestors().Any(a => TemplateHosts.Contains(a.Name.LocalName, StringComparer.Ordinal)));

    private static bool HasAccessibleName(XElement e) =>
        e.Attributes().Any(a => NameProperties.Contains(a.Name.LocalName, StringComparer.Ordinal))
        || e.Elements().Any(c => NameProperties.Contains(c.Name.LocalName, StringComparer.Ordinal));

    /// <summary>
    ///     Words that ARE the control's content — which name it as well as any attribute would.
    ///     <para>
    ///         Only DIRECT words count: a <c>Content</c> string, text written straight inside the
    ///         control, or ONE text element as its whole content. Words inside a panel do not: Avalonia
    ///         names such a button after the PANEL, and the live app exposed 18 of the editor's 141
    ///         automation elements as "Avalonia.Controls.StackPanel" (2026-09-23; the ground truth is
    ///         <c>Perf/ButtonAutomationNameUiPerfSmokeTests</c>). Before that this looked at every
    ///         descendant and passed all of them.
    ///     </para>
    /// </summary>
    private static bool CarriesReadableText(XElement e)
    {
        if (IsReadable(e.Attribute("Content")?.Value)) return true;

        foreach (var text in e.Nodes().OfType<XText>())
        {
            if (IsReadable(text.Value))
                return true;
        }

        var content = e.Elements()
            .Select(c => c.Name.LocalName.EndsWith(".Content", StringComparison.Ordinal) ? c.Elements().FirstOrDefault() : c)
            .Where(c => c != null && !c.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .ToList();
        return content.Count == 1
               && TextCarriers.Contains(content[0]!.Name.LocalName, StringComparer.Ordinal)
               && (IsReadable(content[0]!.Attribute("Text")?.Value)
                   || content[0]!.Nodes().OfType<XText>().Any(t => IsReadable(t.Value)));
    }

    /// <summary>
    ///     Text a human could read aloud as the control's name.
    ///     <para>
    ///         A glyph is not one. <c>"—"</c>, <c>"+"</c>, <c>"✕"</c>, <c>"★"</c>
    ///         and <c>"››"</c> are all in this app as button faces and every one of
    ///         them announces as nothing — which is why the bar was "contains a
    ///         letter or a digit" rather than "is not empty".
    ///     </para>
    ///     <para>
    ///         <b>…and a LONE LETTER is not one either (0.60).</b> A single letter
    ///         is an abbreviation the reader is expected to expand from the layout
    ///         around it: the mixer's <c>M</c> and <c>S</c> are Mute and Solo to
    ///         anyone who can see the fader beside them and are the letters "em"
    ///         and "ess" to everyone else. Eleven of those shipped in the two mixer
    ///         views, plus the note inspector's <c>H</c>/<c>P</c>/<c>T</c> hammer-on
    ///         segment and the track ribbon's <c>T</c>/<c>S</c>/<c>P</c>/<c>L</c>
    ///         chips — 18 in all, every one of them already carrying the right
    ///         resource key on a <c>ToolTip.Tip</c> that only hover reaches.
    ///     </para>
    ///     <para>
    ///         A DIGIT still clears the bar on its own, and that is deliberate
    ///         rather than a gap. A number IS its own label — the practice
    ///         planner's <c>1</c>/<c>3</c>/<c>7</c>/<c>14</c> horizon strip and the
    ///         fret nudges' <c>−1</c>/<c>+1</c>/<c>−12</c>/<c>+12</c> announce
    ///         exactly what a sighted reader gets, and naming all four segments of
    ///         a strip off its one row key would replace four distinct
    ///         announcements with four identical ones. Requiring two characters
    ///         flatly would have reclassified those seven with nothing to say
    ///         about them.
    ///     </para>
    ///     <para>
    ///         A markup extension counts as text unless it names an ICON: a
    ///         <c>Content="{DynamicResource IconClose}"</c> is a geometry, not a
    ///         word.
    ///     </para>
    /// </summary>
    private static bool IsReadable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var v = value.Trim();

        // "{}" is XAML's escape for a literal that starts with a brace.
        if (v.StartsWith('{') && !v.StartsWith("{}", StringComparison.Ordinal))
        {
            return !v.Contains("Icon", StringComparison.Ordinal)
                   && !v.Contains("Glyph", StringComparison.Ordinal)
                   && !v.Contains("Geometry", StringComparison.Ordinal);
        }

        return v.Any(char.IsDigit) || v.Count(char.IsLetter) >= 2;
    }
}
