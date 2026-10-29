# Google OAuth setup for SidePhase Mail

SidePhase Mail is a Windows desktop public client. Users choose **Add account → Google → Sign in with Google**. They do not create a Cloud project, enter a Client ID or supply a Google password to SidePhase Mail. Mail, OAuth tokens and processing stay on the PC; connections go directly to Google. There is no SidePhase backend or OAuth proxy.

## Verified in this setup

On 29 October 2026, the Studio Desktop client and external Testing audience were configured in `sidephase-mail`,
the owner's Gmail account was listed as a test user, and the three consent scopes below were saved. Developer
notifications use support@sidephasestudio.com; the consent-screen support address temporarily uses the owner's Gmail
address, approved for testing only. The required Desktop value is saved as an Actions secret and in ignored local build
configuration. A real browser sign-in succeeded after including it, both connection tests passed and Gmail folders and
inbox messages synchronised in the portable Windows build. Real sending/delivery, expiry/revocation/removal and installed
MSIX sign-in remain unverified. Public verification has not been submitted: product/privacy URLs and the eligible Studio
support identity must be completed first.

## Application registration

The Studio Desktop Client ID is configured once in the `SidePhaseGoogleClientId` property in **Directory.Build.props**. It is embedded as assembly metadata in Infrastructure and therefore travels with both portable and Microsoft Store/MSIX builds. This is a public identifier, safe to distribute and commit. Do not put the downloaded Google credentials JSON or the Desktop client value in the repository.

To override it for a development build, pass `-p:SidePhaseGoogleClientId=YOUR_DESKTOP_CLIENT_ID`. GitHub Actions also accepts the repository Actions **variable** `SIDEPHASE_GOOGLE_CLIENT_ID` (not a secret). A nonempty variable overrides the committed ID; an empty variable uses the committed default. For local developer testing only, the running process can use `SIDEPHASE_GOOGLE_CLIENT_ID`. Customers need none of these settings. Existing per-user Google Client ID settings are ignored.

**Google Desktop credential required by the live endpoint:** although the current Google Desktop documentation labels `client_secret` optional, a real request against this registration on 29 October 2026 returned `invalid_request: client_secret is missing`. The publisher must also supply the issued **Desktop** client value at build time. For this checkout, `GoogleOAuth.local.props` (Git-ignored) contains `<Project><PropertyGroup><SidePhaseGoogleDesktopClientSecret>ISSUED_DESKTOP_VALUE</SidePhaseGoogleDesktopClientSecret></PropertyGroup></Project>`. CI reads the repository Actions **secret** `SIDEPHASE_GOOGLE_DESKTOP_CLIENT_SECRET`. Both build channels embed the value as metadata and send it only to Google's token endpoint during exchange/refresh. It cannot be confidential inside a distributed desktop app and is not a security boundary; PKCE and per-user authorisation protect access. Never substitute a confidential Web application client secret. Developer runtime overrides must supply a matching `SIDEPHASE_GOOGLE_CLIENT_ID` and `SIDEPHASE_GOOGLE_DESKTOP_CLIENT_SECRET`.

The client uses authorization code + S256 PKCE, a random state and the system browser. A temporary TCP listener receives the callback at `http://127.0.0.1:<random-port>/`. Desktop clients support loopback callbacks without manually registering each port. There is no embedded browser sign-in, custom URI registration, web client, UWP client or confidential client secret. The issued public Desktop registration value is supplied as described above; a real sign-in must validate the complete flow.

## Google Cloud Console: create or recreate the setup

1. Sign in to [Google Cloud Console](https://console.cloud.google.com/) as the Studio project owner. Select the existing **SidePhase Mail** project (`sidephase-mail`), or create a project owned by the Studio for a fresh deployment. Separate development and production projects are useful if changing verified scopes later.
2. Open **Google Auth Platform → Branding** (older navigation: APIs & Services → OAuth consent screen). Set the application name to **SidePhase Mail**. Set a support email and developer notification email. The desired customer contact is **support@sidephasestudio.com**. Google restricts the support dropdown to eligible Google Accounts/Groups: connect that identity before public release. Any temporary personal support address is for testing only. Publish the reviewed privacy policy and an accurate product homepage on the Studio's verified domain, and enter their actual URLs. Do not invent a URL or submit the repository's draft policy as an already published policy.
3. Choose **Audience → External**, initially **Testing**. Add the exact Google accounts that will test the app under **Test users**. Google limits testing to the listed users (up to 100); refresh tokens for this mail scope in Testing normally expire after seven days. This is not a production configuration.
4. Under **Clients → Create client**, choose **Desktop app**, name it **SidePhase Mail Windows Desktop**, and create it. Copy only the Client ID into `Directory.Build.props`. Do not choose Web application or UWP. Obtain the issued Desktop client value from the creation dialog or its download and supply it through the ignored local props file and GitHub Actions secret above. Do not commit the credentials JSON or value.
5. Under **Data access → Add or remove scopes**, declare exactly:
   - `https://mail.google.com/` — read, sync, send, draft, organise and permanently delete mail through IMAP/SMTP.
   - `openid` — identify the signed-in account.
   - `https://www.googleapis.com/auth/userinfo.email` — Google Console's email identity scope; requested by the client using its `email` shorthand.
6. **APIs:** this implementation uses Gmail IMAP/SMTP rather than Gmail REST, so it does not require enabling the Gmail API to synchronise or send. Do not enable unrelated APIs. If replacing the transport with Gmail REST later, enable **Gmail API** in APIs & Services → Library first and revisit the scopes. OpenID/email identity is obtained during OAuth; no People API is needed.
7. Build the app. Use the same Studio Client ID in every distributed build and architecture. Allow time for new Google settings to propagate.

## Why keep MailKit rather than add Gmail REST?

The existing MailKit engine already provides TLS-only IMAP/SMTP, XOAUTH2, a local SQLite cache, unified inbox, lazy message bodies/attachments, drafts, sending, flags, folder moves, archive, junk and permanent deletion from Trash. Reusing it preserves the Dark Glass UI and the existing message identity and cache behavior. Gmail REST would require another transport/cache adapter and does not itself remove restricted-scope verification.

IMAP/SMTP requires the restricted `https://mail.google.com/` scope. Google's minimum-scope policy requires justification including actual permanent deletion, not merely reading/sending. SidePhase Mail already exposes confirmed permanent deletion from Trash (`MessageCommandService` → `DeletePermanentlyAsync` → UID EXPUNGE), which must be demonstrated during review. Ordinary Delete still moves to Trash. Google decides whether that justification is sufficient. Do not add immediate deletion from Inbox solely to justify a permission. If permanent deletion is removed, or Google requires downscoping, use Gmail REST with `gmail.modify` (also restricted) for reading, sending and trashing, rather than requesting full-mail IMAP access for reduced functionality.

## Test real Gmail sign-in before claiming success

1. Add your Gmail address as a test user in the selected project's Audience page. Run the newly built app on Windows 11. For Store validation, also install/test the MSIX build; it is a full-trust WinUI desktop app and uses the same browser/loopback flow.
2. Choose Add account → Google, enter your address, continue and choose Sign in with Google. Your default browser must open Google's own page with SidePhase Mail and the expected permissions. Finish sign-in and consent yourself. Declining mail access must fail cleanly without saving an account.
3. Return to SidePhase Mail. Both IMAP and SMTP are tested before account persistence. Confirm the account appears and Unified Inbox synchronises actual messages. Check the selected Google account matches the mailbox; test choosing a different account and rejecting it during reauthentication.
4. Read a message and attachment, change read/unread and star, move/archive/junk, save a draft, and send a test message to an address you control. Verify delivery and server-side changes in Gmail. Test permanent deletion only on a disposable message after confirmation. These operations are not proven by a build or mocked token endpoint.
5. Restart the app: tokens should reload from DPAPI without another prompt. Leave it running beyond access-token expiry and confirm sync/send refresh silently. Revoke access from your Google Account → third-party connections; after the current token/session is rejected the app must ask to sign in again. Use Settings → Accounts → Sign in again and confirm recovery.
6. Remove the account and confirm its cached mail and saved credentials disappear locally; server mail remains. Removal is local sign-out, not global Google-grant revocation (which can affect your other devices). Revoke the app separately in Google Account settings if desired.
7. Cancel sign-in, close the browser tab, deny consent, go offline and reconnect. Cancellation/timeouts and outages must not save a partial account or discard other accounts' credentials.

Tokens use the existing Windows DPAPI CurrentUser secret store, separate from settings/SQLite/logs. Refreshes are serialised per account; a missing/expired/revoked token requests reauthentication, transient provider/network failures retry later. Account removal serialises against refresh so a late refresh cannot recreate its token file. Data locations are in docs/ACCOUNT_PROVIDERS.md.

## Public verification and Microsoft Store release

Microsoft Store approval does not approve Google OAuth. Before unrestricted public Gmail sign-in:

1. Replace the temporary personal consent-screen support email with the eligible **support@sidephasestudio.com** identity. Complete homepage, logo if used, privacy policy, authorised domains and domain ownership verification. The policy must describe local storage, data deletion, direct provider traffic and compliance with Google's Limited Use requirements; review the actual app behavior before publishing it.
2. Complete brand verification and restricted-scope verification in **Google Auth Platform → Verification centre**. Supply the Windows app/download or reviewer access, exact scope justification, architecture/data-flow explanation and a demo video showing sign-in, consent and each restricted-data feature, including permanent deletion. Do not submit placeholder URLs or an untested demo claim.
3. Explain that the app runs locally, stores tokens with Windows DPAPI, caches/processes mail locally and has no developer servers receiving mail, passwords or tokens. Document any optional external integrations accurately.
4. Google's published restricted-scope guidance ties the third-party security assessment to access to restricted data **from or through a third-party server**. This local-only design is grounds to request that the assessment is not applicable; it is **not** an exemption from restricted-scope/brand verification and not a guarantee of Google's decision. Ask the verification team to confirm applicability for this exact architecture. If server-side access is introduced or Google determines an assessment is required, expect an approved assessor/CASA process and annual reassessment. Never route Gmail data through a new backend merely to obtain verification.
5. Publish to Production only after the required reviews and real sign-in checks. Going to Production alone does not remove unverified-app warnings or user caps. Keep the Studio project and client registration active for token refresh and all customers.

## References (checked 29 October 2026)

- [Google OAuth for Desktop Apps: system browser, PKCE, loopback and optional secret](https://developers.google.com/identity/protocols/oauth2/native-app)
- [Gmail OAuth IMAP/SMTP protocol](https://developers.google.com/workspace/gmail/imap/xoauth2-protocol)
- [Minimum-scope requirements, including IMAP/SMTP](https://support.google.com/cloud/answer/13807380?hl=en)
- [Gmail API scope classifications](https://developers.google.com/workspace/gmail/api/auth/scopes)
- [Restricted-scope verification and security-assessment conditions](https://developers.google.com/identity/protocols/oauth2/production-readiness/restricted-scope-verification)
- [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy)
