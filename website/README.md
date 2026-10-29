# SidePhase Mail public pages

Static source for https://sidephasestudio.com/sidephasemail/ and its privacy, terms and support pages.
The pages use the actual application logo, external CSS and system fonts, with no scripts, analytics, forms or dependencies.

Deploy only `sidephasemail/` into the existing Studio site's `dist/` on Lenovo. Preserve the Studio homepage,
shared CSS, unrelated paths, DNS and tunnel configuration. The dedicated site nginx configuration needs exact HTTPS
redirects for slashless product/policy paths so its internal listener port never appears in public redirects.

`PRIVACY.md` mirrors the published product privacy policy. Keep it and `sidephasemail/privacy/index.html` consistent.
The data-removal page deliberately explains retained outbox items, contacts, backups and exported files.
Google public verification remains pending; no Store listing or Google approval is claimed.

Run `python website/verify.py` to check page structure, local links, canonical URLs, policy disclosures and the app links.
For visual checks, serve this folder with `python -m http.server 8765 --bind 127.0.0.1` and open `/sidephasemail/`.

The deployed additive server locations are recorded in `nginx.locations.conf`; merge them inside the existing server
block, validate the configuration and reload only the dedicated Studio nginx instance. `sitemap-entries.xml` records
only this product's entries to merge into the existing sitemap; it must not replace the site's sitemap wholesale.
