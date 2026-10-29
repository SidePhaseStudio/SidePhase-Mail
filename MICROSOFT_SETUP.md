# Microsoft sign-in setup (publisher only)

Status: the app has browser-based OAuth with PKCE, refresh tokens and Windows-protected storage.
The shared Microsoft registration is not configured yet, and real Microsoft sign-in has not been tested.
Customers must not create their own registrations. No backend or Microsoft client secret is needed.

1. Sign into https://entra.microsoft.com/ using the Studio's registration owner account.
2. Open **App registrations**. Reuse an existing SidePhase Mail registration if present; otherwise create one.
3. Select **Accounts in any organizational directory and personal Microsoft accounts**.
4. Under **Authentication**, add **Mobile and desktop applications**, redirect URI `http://localhost`.
   The existing system-browser authorization-code flow uses a random loopback port and S256 PKCE.
   Do not create a web client or a client secret. Device-code/password flows are not used.
5. Configure delegated Office 365 Exchange Online permissions **IMAP.AccessAsUser.All** and **SMTP.Send**.
   The app also requests `openid`, `email` and `offline_access`. No Graph mail permissions or application permissions are needed.
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
