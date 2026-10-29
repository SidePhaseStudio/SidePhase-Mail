# Before a public beta or Store submission: actions for SidePhase Studio

Nothing in this repository submits, publishes or signs anything automatically, and it contains no production
credentials. These steps need the owner's accounts and decisions. Details are in
[PACKAGING.md](PACKAGING.md) and [ACCOUNT_PROVIDERS.md](ACCOUNT_PROVIDERS.md).

## 1. Microsoft Partner Center
- [ ] Create or open the Partner Center developer account (company account for "SidePhase Studio").
- [ ] Reserve the app name **SidePhase Mail**.
- [ ] Copy *Product identity* (Package/Identity/Name and Publisher) into `src/SidePhase.Mail.App/Package.appxmanifest`,
      replacing `SidePhaseStudio.SidePhaseMail` and `CN=SidePhase Studio (development)`. Do this **before** a wider beta
      so testers' installs upgrade into the Store app.
- [ ] Replace the placeholder images in `Assets/Package/` with final artwork (all listed sizes, 100% and 200%).
- [ ] Store listing: description, screenshots, category (Productivity), age rating questionnaire, support contact.
- [ ] Privacy policy URL: publish [PRIVACY.md](../PRIVACY.md) (after review) on SidePhase Studio's website.
- [ ] Declare the `runFullTrust` capability justification (desktop app) in the submission notes.
- [ ] Build with `-p:SidePhasePackage=msix -p:UapAppxPackageBuildMode=StoreUpload` for a `.msixupload` (see PACKAGING.md)
      and upload it **by hand**.

## 2. Signing outside the Store
- [ ] Obtain a code-signing certificate issued to SidePhase Studio, or set up Azure Trusted Signing.
- [ ] Store it only in GitHub encrypted secrets (`SIGNING_CERTIFICATE_BASE64`, `SIGNING_CERTIFICATE_PASSWORD`).
      Never commit it or upload it as an artifact. Without these, beta releases stay unsigned drafts.

## 3. Google sign-in (Gmail)
- [x] Create a Google Cloud project and an OAuth client of type *Desktop app* (sidephase-mail, 29 October 2026).
- [ ] Scope: `https://mail.google.com/` only (IMAP/SMTP need it) plus `openid email`. Nothing else.
- [ ] Publish the consent screen and complete Google's restricted-scope verification; confirm whether this local-only design requires a security assessment
      before public use; until then only listed test users can sign in.
- [ ] Embed the Studio public client ID and supply the required Desktop value through publisher build configuration (GOOGLE_SETUP.md). **Then test sign-in, sync, send and
      token refresh with a real Gmail account and record the result in RELEASE_READINESS.md.**

## 4. Microsoft sign-in (Outlook.com / Microsoft 365)
- [ ] Register an app in Microsoft Entra ID: *Accounts in any organizational directory and personal Microsoft
      accounts*, platform *Mobile and desktop*, redirect `http://localhost`.
- [ ] Delegated permissions: `IMAP.AccessAsUser.All`, `SMTP.Send`, `offline_access`, `openid`, `email`. Nothing else.
- [ ] Complete publisher verification. **Then test with a personal and a work account and record the result.**

Until steps 3 and 4 are done and verified, the app and its docs must not describe Google or Microsoft sign-in as ready.

## 5. Promotions
- [ ] To show a promotion, add it to `src/SidePhase.Mail.App/Assets/Promotions/promotions.json` with `"approved": true`
      after review (see [ADVERTISING.md](ADVERTISING.md)). The remote SidePhase Ads API stays off unless explicitly
      configured; it belongs in its own repository, not here.

## 6. Final checks
- [ ] Owner hand-check on Windows 11 using [BETA_TESTING.md](BETA_TESTING.md#checklist), including install, upgrade
      over the previous beta (accounts and saved sign-ins kept) and uninstall.
- [ ] The "sent but not arriving" report is resolved or understood.
- [ ] Raise `VersionPrefix` in `Directory.Build.props` and the manifest version together.
