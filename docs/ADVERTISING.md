# Sponsored content (advertising)

SidePhase Mail is planned to stay free, supported by small, clearly labelled sponsored cards, with an optional paid
ad-free upgrade. This milestone adds the **framework only**:

* **No third-party ad network is integrated.**
* **Production advertising is disabled in code** (`AdvertisingPolicy.ProductionAdvertisingEnabled = false`). It stays
  off until campaigns are approved and the privacy policy describes it.
* **Advertising is off by default.** *Settings → About → Preview sponsored cards (test content)* shows the built-in
  demonstration cards, which are marked **Test content** and link only to the reserved `example.com/.org/.net` domains.
  Nothing is downloaded or reported.

## Where cards can appear

| Placement | Where | When |
| --- | --- | --- |
| `SidebarFooter` | Bottom of the left sidebar | Only while the sidebar is open |
| `EmptyReadingPane` | Under "Select a message to read" | Only while no message is open |

Cards **never** appear inside an email body, next to an open message, in the message list or in compose windows,
and there are no pop-ups, images, video, autoplay or animation. Every card shows a **Sponsored** label (and **Test
content** for demonstration cards), the advertiser's name, a *Why am I seeing this?* explanation and a button that hides
sponsored content until the app restarts. Clicking the card's link shows the same confirmation dialog as links in email
before the browser opens. A card stays for at least 10 minutes (no rotation).

## Privacy rules (enforced by design and tests)

* The advertising module (`src/SidePhase.Mail.Advertising`) **does not reference the mail engine** (`SidePhase.Mail.Core`,
  `SidePhase.Mail.Infrastructure`, MailKit, MimeKit). A test fails if it ever does.
* A provider is only ever told an `AdRequest`: **placement, app version and display language**. A test fails if
  anything is added to it, so any change gets a privacy review first.
* No email contents, subjects, addresses, contacts, folders, accounts, identifiers or behaviour are collected,
  stored or sent for advertising. There is no ad ID, no cookie (the HTTP client has cookies disabled) and no
  impression or click reporting.
* Cards are plain text. They're rendered with the app's own controls, never as HTML, so they can't run script,
  load trackers or look like part of an email. Content rules (lengths, no markup or control characters, HTTPS links
  only) are checked before a card is shown.

## Architecture

```
SidePhase.Mail.Advertising (net10.0, no mail references)
  AdPlacement, SponsoredContent      card model + content rules (IsValid)
  AdRequest                          the only data a provider receives
  IAdProvider                        DemoAdProvider (built-in test cards) | RemoteAdProvider (future API, HTTPS JSON)
  AdvertisingSettings(+Store)        advertising.json in the data folder: Mode Off/Demo/Production, Endpoint, SimulateAdFree
  AdvertisingPolicy                  ProductionAdvertisingEnabled = false, MinimumDisplayTime
  IAdEntitlement / LocalAdEntitlement   ad-free (Premium) entitlement; local simulation for now
  AdService                          decides what to show; never throws; hide-for-session

SidePhase.Mail.App/Advertising/SponsoredCard   the card control (Dark Glass card style), placed by MainWindow and ReadingPane
```

To **remove advertising entirely**: delete the `SidePhase.Mail.Advertising` project, the `Advertising` folder in the
app, the two `<ads:SponsoredCard>` elements, the `AdService` registration in `App.xaml.cs` and the *Sponsored content*
cards in Settings. Nothing in the mail engine changes.

## Future work (not done)

1. **Approved campaigns and disclosures.** Update `PRIVACY.md` (what a request contains, who runs the service, how
   long server logs are kept) and the Store listing before enabling anything.
2. **Advertising API.** `RemoteAdProvider` expects `GET {endpoint}?placement=…&version=…&lang=…` returning one card as
   JSON (format in the class comment). The endpoint must be HTTPS without a query string or credentials. Set
   `ProductionAdvertisingEnabled = true` only in a reviewed release, and configure the endpoint in `advertising.json`
   (or a build setting).
3. **Ad-free upgrade.** Replace `LocalAdEntitlement` with an implementation backed by a Microsoft Store add-on
   (`Windows.Services.Store.StoreContext.GetAppLicenseAsync()` → `AddOnLicenses`). Remove the *Simulate the ad-free
   upgrade* setting at the same time.
4. **Message list placement** is intentionally not supported.
