# CLAUDE.md — WPF UI assembly

The net48 entry assembly ACT loads. WPF view + theme + plugin host. The repo-root
`CLAUDE.md` covers the two-process architecture and build; this file covers the UI.

Files:
- `Theme.xaml` — the single `ResourceDictionary`: palette, icons, typography, control styles.
- `DiscordTriggersView.xaml` / `.cs` — the tabbed view. Code-behind is ACT glue + view-only concerns.
- `DiscordTriggersPlugin.cs` — `IActPluginV1` host; mounts the view in a WinForms `TabPage` via `ElementHost`.
- `ViewBehaviors.cs` — `static` attached properties (`PasswordBoxBinding`, `LogListCopy`).
- `VistaFolderPicker.cs` — modern shell folder picker (`IFileOpenDialog` interop, no dependency).

`DataContext` is `DiscordTriggersViewModel` (in Core). All state/commands live there; the
code-behind never holds UI state.

## Load-time invariant (do not break)

No type **defined in this assembly** may derive from / implement a Costura-merged dependency
(ACT calls `GetTypes()` before our resolver attaches). Consequences for UI work:
- WPF base types (`UserControl`, `IValueConverter`) are GAC → safe to subclass.
- Never subclass `Microsoft.Xaml.Behaviors` here. It's XAML-only (`i:Interaction.Triggers` +
  `LaunchUriOrFileAction` for links). View helpers that would be `Behavior<T>` are written as
  `static` attached-property classes instead (see `ViewBehaviors.cs`).
- The MVVM ViewModel lives in **Core**, not here (it derives from merged `CommunityToolkit.Mvvm`).
- Core stays UI-agnostic: no WPF/WinForms in Core. View-only concerns — `CollectionView`
  filtering, folder dialogs, `ElementHost` — live in this assembly's code-behind.

## UX design conventions

Fluent 2, light theme. All values are `{StaticResource}` keys in `Theme.xaml` — use them, don't
hard-code colors/sizes.

### Color
- Surfaces flat, no shadows. Page `Surface` `#EFF4FA`; cards pure white `Card` `#FFFFFF` with a
  `CardBorder` `#DEE7F2` hairline, `CornerRadius=4`.
- Single accent `Accent` `#2F6FE0` (+ `AccentHover`/`AccentPressed`/`AccentSoft`/`AccentSoftHover`).
- Text: `TextPrimary` / `TextSecondary` / `TextMuted`; on accent fills use `OnAccent`.
- Inputs: `FieldBg`/`FieldBorder`/`FieldStrongBorder` (Fluent inputs get a 2px accent underline on focus).
- Status: `StatusOnline` green / `StatusOffline` red.
- `FlyoutShadow` is the **only** elevation — transient surfaces (popups) only. Cards stay flat.

### Typography (Segoe UI Variable)
- Root font `Segoe UI Variable Text, Segoe UI` @ 14. Use the optical cuts:
  **Small** (≤12px captions), **Text** (13–18px body), **Display** (≥20px titles).
- Styles: `SectionHeader` (14 SemiBold, Body Strong), `CardTitle` (14/20 Body),
  `CardDesc` (Small, 12/16 Caption), `Caption` (Small, 12/16 muted).
- Minimums: **12px Regular, 14px SemiBold** — never go below. Small SemiBold overline labels
  use the Small cut at 12.
- Set `LineHeight` on wrapping roles (Caption 16, Body 20, Subtitle 28).

### Icons — Segoe Fluent Icons font (Windows 11 only)
- Every icon is a glyph code point, not geometry. Stored as
  `<sys:String x:Key="Icon.Name">&#xE713;</sys:String>` (root declares
  `xmlns:sys="clr-namespace:System;assembly=mscorlib"`). `IconFont` = `Segoe Fluent Icons`.
- The OS ships the font; nothing to bundle. **There is no SVG/`Path`/`Geometry` icon** — keep it
  that way (the two ComboBox carets are also the `Icon.ChevronDown` glyph).
- Render via the `IconGlyph` `TextBlock` style, or inline
  `<TextBlock FontFamily="{StaticResource IconFont}" Text="{StaticResource Icon.X}" />`.
  `SettingsCard`/`NavTab` take the glyph via `Tag` (a string).
- **FontSize must be a crisp step: 16, 20, 24, 32, 40, 48, 64.** Off-step sizes blur. In use:
  16 for inline/settings/card glyphs, 20 for nav-rail + engine-card header icons.
- Color glyphs with `Foreground` (not `Fill`); recolor triggers set `Foreground`.
- Add an icon: look up the code point on the Microsoft Learn *Segoe Fluent Icons* page, add an
  `Icon.Name` string. `Icon.Chevron` = ChevronRight `E76C` (rotated via `RenderTransform` for
  disclosure); `Icon.ChevronDown` = `E70D`.

### Layout & components
- Page = `ScrollViewer Padding="20,16"` → `StackPanel MaxWidth="640"`. Left nav rail is 186px.
- Spacing rhythm: cards and callouts carry **bottom-only** `Margin="0,0,0,4"`. `SettingsCard`
  and `InfoCallout` both follow this — match it for any new row so gaps stay even.
- `SettingsCard` (`HeaderedContentControl`): the settings row. `Tag`=icon glyph, `Header`=title
  or title+description panel, `Content`=the control on the right. Omit `Tag` for sub-rows (icon
  space stays reserved so titles align).
- Nav: `NavTabControl` + `NavTab`. `TabItem.Tag`=icon glyph, `Header`=label; selected → accent
  bar + accent icon.
- Buttons: implicit `Button` = neutral fill; `PrimaryButton` = accent fill.
- Other reusable styles: `EngineChoiceCard` (selectable RadioButton card), `Segmented` +
  `SegmentButton` (segmented control), `PickerButton` + `PickerList` (the voice picker flyout),
  `DisclosureToggle` (Advanced chevron), `HelpGlyphButton` (the `?`), `ThinProgress`,
  `ToggleSwitch`, `InfoCallout` (AccentSoft notice band).

### Choice-idiom hierarchy
Pick the control by branching weight: **choice-cards** for a top-level either/or (engine) ›
**segmented control** for 2–4 tiers (quality, CPU threads) › **grouped + searchable dropdown**
for long lists (voices).

### Binding patterns (no value converters where avoidable)
- Paired-bool pattern for RadioButton/segmented two-way binding (e.g. `IsSapi`/`IsOnnx`,
  `IsThreads1/2/4`) — no converter needed.
- `BooleanToVisibilityConverter` (`BoolToVis`) folds sections.
- Searchable voice picker = `ToggleButton` + `Popup` + locale-grouped `ListBox` +
  code-behind `Filter` (`OnnxVoiceFilter` / `OnVoiceSearchChanged` / `OnVoiceListSelectionChanged`).
  `SelectedItem` is **not** bound (filtering would null it); selection is pushed from code-behind.

## CI gate

XAML must pass XAML Styler: `dotnet xstyler -p -c Settings.XamlStyler` (run from repo root,
settings auto-discovered). `IndentSize` 2, design-time `d:` attributes kept. After editing any
`.xaml`, run it before committing.
