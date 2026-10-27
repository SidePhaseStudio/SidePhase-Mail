# Dark Glass design system

SidePhase Mail's look is built from a small set of tokens on top of Fluent Design. All tokens live in
`src/SidePhase.Mail.App/Themes/Palette.xaml`; reusable styles live in `Themes/Styles.xaml`.

## Principles

* **Luxurious, not flashy.** Deep charcoal surfaces, one accent, no decorative gradients in the content.
* **Native materials.** The window uses the real Mica backdrop (`MicaBackdrop`), Acrylic on Windows 10,
  never a simulated blur. Content sits on translucent layers so the material shows through subtly.
* **Hierarchy through weight and tone,** not boxes: hairline strokes, soft fills and typography do the work.
* **Respect the user's system.** Transparency off or a contrast theme → solid surfaces / system colours.

## Layers (dark)

The window is one sheet of Mica Alt glass. The sidebar sits directly on it; the message list, reading pane and
full-page views float on it as rounded translucent panels separated by a 6 px gap rather than divider lines.

| Token | Value | Use |
| --- | --- | --- |
| `SpWindowTintBrush` | `#0B0D12` gradient, 18% top → 40% bottom | Over Mica: deepens it and adds soft top lighting |
| `SpWindowSolidBrush` | `#0B0D12` | Solid themes and fallbacks |
| `SpPanelBrush` / `SpLayerBrush` | `#151820` @ 58% | Message list, settings, welcome panels |
| `SpReaderBrush` | `#171A23` @ 65% | Reading pane and compose panels |
| `SpPanelStrokeBrush` | white 12% → 3% (top → bottom) | Panel rims, like light catching the edge of glass |
| `SpBodyCardBrush` | white @ 3% | The card a message body sits on |
| `SpControlFillBrush` | white @ 4% | Toolbar button groups |
| `SpAcrylicBrush` | in-app Acrylic, `#1A1E27` tint | Floating surfaces: toasts, hover quick actions |
| `SpCardBrush` / `SpCardHoverBrush` | white @ 3.5% / 6% | Cards, settings rows, attachments |
| `SpStrokeBrush` / `SpStrokeStrongBrush` | white @ 6.5% / 12% | Hairline borders |
| `SpDividerBrush` | white @ 4.5% | Separators inside panels |

## Text

| Token | Dark | Light |
| --- | --- | --- |
| `SpTextPrimaryBrush` | `#F5F6FA` | `#14161C` |
| `SpTextSubtleBrush` | `#C9CED9` | `#2F3440` |
| `SpTextSecondaryBrush` | `#9DA5B4` | `#5B6272` |
| `SpTextTertiaryBrush` | `#737B8C` | `#7D8494` |

Fonts: Segoe UI Variable Display (titles), Text (body), Small (captions, badges); Segoe Fluent Icons.

## Accent

Default `#8B7CFF`. `ThemeService` derives every accent shade at runtime (`ColorMath.DeriveAccent`):
fills are darkened until white text reaches 4.5:1, accent text is adjusted to 4.5:1 against the surface.
Selection is the accent at 16% (hover 22%); badges at 20% with a light accent label.

## Components

* **Message row** — 36 px avatar (28 px compact), 7 px unread dot, semibold sender when unread, subject
  dimmed when read, one-line preview (hidden in compact). Quick actions appear on hover on an elevated pill.
* **Section headers** — 12 px semibold, tracked, tertiary colour; the Pinned header has a count pill and chevron.
* **Buttons** — `SpSubtleButtonStyle` (transparent, soft fill on hover), `SpIconButtonStyle` (34 px),
  `SpQuickActionButtonStyle` (28 px), `SpPrimaryButtonStyle` (accent), `SpSecondaryButtonStyle`.
* **Settings card** — `controls:SettingsCard`: icon, header, description, control on the right.
* **Corner radii** — 4 (small chips, quick actions) / 6 (buttons, list rows) / 8 (panels, cards, toolbar groups, toasts) / 12 (dialogs).
* **Toolbar groups** — related reading-pane actions share a faint rounded surface (`SpToolbarGroupStyle`).

## Motion

Short and quiet: 83 ms brush transitions on buttons with a gentle press-in (96% scale), a 180 ms fade when a
new message opens, Fluent entrance transitions for pages, the welcome screen and list changes.
