# Security Policy

Luthn handles privacy-sensitive operational data. Treat the repository, issue
tracker, and build logs as public.

## Supported Versions

Security fixes are applied to the current `main` branch. Until versioned
releases are published, older commits and image revisions are not maintained as
separate supported release lines.

## Do not commit

- API keys, tokens, passwords, cookies, credentials, connection strings
- customer originals
- contracts, invoices, tax, finance, or accounting source records
- raw email/message exports containing personal or customer data

## Reporting

Do not open a public issue for a suspected vulnerability. Use GitHub's private
vulnerability reporting feature from the repository's **Security** tab.

Include the affected version or image digest, impact, reproduction steps, and
any proposed mitigation. Remove real credentials and private user data from the
report. The maintainer will acknowledge the report and coordinate disclosure
after a fix is available.
