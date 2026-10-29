# Microsoft sign-in setup (publisher only)

Status: the app has browser-based OAuth with PKCE, refresh tokens and Windows-protected storage.
The shared Microsoft registration was created on 29 October 2026 and is bundled in builds.
Client ID: `cff3a5ae-ddc3-4688-a1e7-9ee28ba2b868`. Real Outlook.com sign-in and inbox synchronization succeeded in the portable Windows build on 29 October 2026.
Customers must not create their own registrations. No backend or Microsoft client secret is needed.

Configured in Entra: personal plus organizational accounts, the native `http://localhost` callback,
the five delegated scopes below, app logo and public homepage/privacy/terms URLs. The unused default
`User.Read` permission was removed. No tenant-wide admin consent has been granted.

Public-release blocker: the publisher is still unverified. The current tenant uses an `onmicrosoft.com`
domain. Entra requires a DNS-verified custom domain matching the Partner Center primary contact domain
before publisher verification can proceed. Do not claim that any work/school tenant can consent yet.

Live evidence: the Outlook account is stored with Microsoft OAuth and outlook.office365.com.
The local cache contains synchronized Inbox, Sent, Archive, Junk and Deleted folders, including 13 Inbox messages
at verification time. This confirms personal Outlook sign-in and download, not actual sending/delivery,
expiry refresh, revocation/reconnection, removal, work/school access or installed-MSIX sign-in.

1. Sign into https://entra.microsoft.com/ using the Studio's registration owner account.
2. Open **App registrations**. Reuse an existing SidePhase Mail registration if present; otherwise create one.
3. Select **Accounts in any organizational directory and personal Microsoft accounts**.
4. Under **Authentication**, add **Mobile and desktop applications**, redirect URI `http://localhost`.
   The existing system-browser authorization-code flow uses a random loopback port and S256 PKCE.
   Do not create a web client or a client secret. Device-code/password flows are not used.
5. Under **Microsoft Graph → Delegated permissions**, declare **IMAP.AccessAsUser.All** and **SMTP.Send**.
   Also declare `openid`, `email` and `offline_access`. Remove the unused default `User.Read`.
   The catalogue exposes these protocol scopes; runtime token requests use the Outlook resource URLs.
   No Graph Mail.ReadWrite/Mail.Send calls or application permissions are needed. Do not grant tenant-wide consent.
6. Set the public Application (client) ID in `Directory.Build.props` as `SidePhaseMicrosoftClientId`.
   GitHub builds can override it using repository variable `SIDEPHASE_MICROSOFT_CLIENT_ID`.
   Local developer environment overrides use the same variable name; never put tokens in build configuration.
7. Configure branding with the actual app logo, support contact `support@sidephasestudio.com`, and:
   - Homepage: https://sidephasestudio.com/sidephasemail/
   - Privacy: https://sidephasestudio.com/sidephasemail/privacy/
   - Terms: https://sidephasestudio.com/sidephasemail/terms/
8. Check publisher-domain and publisher-verification eligibility in Entra. Organization consent policies may
   require a verified publisher or administrator approval; Store distribution does not bypass those policies.
9. Test **Add account** with a personal Outlook.com account and a work/school Microsoft 365 mailbox.
   Confirm correct account identity, inbox synchronization, actual sending/delivery, refresh after expiry,
   revoked access/reconnection, and account removal in both portable and installed MSIX builds.
   Use disposable test mail for move/delete checks.

Some mailboxes require IMAP to be enabled. Work/school organizations can disable IMAP, SMTP AUTH or
third-party consent, even with correct OAuth registration. Report that requirement accurately rather than
suggesting passwords or weakening tenant security. Local account removal does not revoke provider-wide consent.

References:
- https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth
- https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app
- https://learn.microsoft.com/en-us/entra/identity-platform/reply-url
- https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview
