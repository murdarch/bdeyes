# Security policy

## Supported versions

Security fixes are applied to the latest published preview release and `main`.

## Reporting a vulnerability

Use GitHub's private vulnerability-reporting flow for this repository. If that flow is unavailable, contact the repository owner through their GitHub profile before sharing technical detail publicly.

Do not open a public issue containing:

- a Beads or Dolt password;
- a credential file or protected environment file;
- a private key or access token;
- confidential bead content; or
- an unredacted command, screenshot, or log containing any of the above.

A useful report identifies the affected bdeyes version, operating system, `bd` version, reproduction boundary, and expected security property without including live secrets.

## Security model

bdeyes is a read-only client. Every ledger invocation uses global `bd --readonly` mode. The application does not read Dolt tables or `.beads/issues.jsonl` directly and has no mutation surface.

bdeyes stores the last workspace path, expanded issue IDs, and an optional `bd` executable path in local application data. It does not read or store ledger passwords or Beads credential files. Authentication remains the responsibility of `bd`.

The application renders workspace content. Local desktop access, screenshots, crash artifacts, and diagnostic output must be protected according to the sensitivity of that content.
