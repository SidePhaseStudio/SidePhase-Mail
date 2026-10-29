# Promotions (advertising)

SidePhase Mail is planned to stay free, supported by **one clearly labelled promotion banner**, with an optional
paid ad-free upgrade. Decisions so far:

* **No third-party advertising networks or SDKs** (no AdsJumbo or similar). None is integrated or referenced.
* **SidePhase Studio promotions only**, bundled with the app and individually approved.
* **No advertising server in this repository.** A separate *SidePhase Ads API* can be connected later, but only when
  explicitly configured.

## The placement

One banner, **centred in the main window's footer**, about **728 × 90** (it narrows with the window). It is:

* labelled **Promotion** (and **Promotion · Test content** for the built-in preview banner);
* in the Dark Glass card style, with no pop-ups, animation, autoplay, video or sound;
* never inside the inbox, the message list, a message or a compose window;
* **collapsed completely** when there is no promotion to show (the default today), when the window is shorter than
  680 epx, with the ad-free entitlement, or after the user presses *Hide* (until restart).

Each banner has *Why am I seeing this?* and *Hide*. Its link goes through the same confirmation dialog as links in
email. A banner stays for at least 10 minutes (no rotation).

## Sources (advertising.json → `mode`)

| Mode | What shows | Network |
| --- | --- | --- |
| `House` (**default**) | Approved SidePhase Studio promotions from `src/SidePhase.Mail.App/Assets/Promotions/promotions.json` | None |
| `Demo` | The built-in test banner (Settings → About → *Preview the promotion banner*) | None |
| `Off` | Nothing | None |
| `Production` | Banners from the SidePhase Ads API at `endpoint` | HTTPS to that endpoint only |

`Production` must be set **by hand in `advertising.json`** together with an `https://` `endpoint` (no query string,
no credentials). There is no setting for it in the app; without a valid endpoint it behaves like `Off`.

### Adding an approved promotion

1. Add an entry to `Assets/Promotions/promotions.json` with `"approved": true` (optional `starts`/`ends` dates).
2. Text only, or with a banner image: a PNG/JPEG **in the same folder**, referenced by file name, designed at
   728 × 90 (provide 1456 × 180 for sharp high-DPI display). Images are never URLs, so nothing is downloaded.
3. Content rules (checked before showing): headline ≤ 70 characters, body ≤ 140, call to action ≤ 24, no markup or
   control characters, link must be `https://`. Entries that break a rule are skipped.
4. Rebuild. The shipped file contains one example entry with `"approved": false`, so nothing shows.

## Privacy rules (enforced by design and tests)

* The module (`src/SidePhase.Mail.Advertising`) **does not reference the mail engine** (Core, Infrastructure,
  MailKit, MimeKit); a test fails if it ever does.
* Bundled promotions involve no network access at all.
* The Ads API is only told an `AdRequest`: placement, app version and display language. A test fails if anything is
  added to it. There are no cookies, identifiers or impression/click reporting, and remote banners are text only.
* No email contents, subjects, addresses, contacts or behaviour are ever used for promotions.

## Architecture

```
SidePhase.Mail.Advertising (net10.0, no mail references)
  SponsoredContent, AdPlacement.Footer, PromotionSource   banner model + content rules
  HousePromotionProvider    approved entries from the bundled promotions.json (dates, validation)
  DemoAdProvider            built-in test banner
  RemoteAdProvider          the separate SidePhase Ads API (HTTPS JSON, validated, cookie-free)
  AdvertisingSettings(+Store), AdvertisingPolicy, IAdEntitlement/LocalAdEntitlement, AdService

SidePhase.Mail.App/Advertising/PromotionFooter   the footer banner (row 2 of MainWindow)
```

To **remove promotions entirely**: delete the Advertising project, the app's `Advertising` folder and
`Assets/Promotions`, the `<ads:PromotionFooter>` element and its row in `MainWindow.xaml`, the `AdService`
registration in `App.xaml.cs`, and the *Promotions* cards in Settings.

## Still to do (needs SidePhase Studio)

1. Approve the first promotions (text and artwork).
2. If the Ads API is built: host it separately, document what its server logs keep, update `PRIVACY.md` first.
3. Ad-free upgrade: replace `LocalAdEntitlement` with a Microsoft Store add-on check
   (`Windows.Services.Store.StoreContext`), and remove the *Simulate the ad-free upgrade* setting.
