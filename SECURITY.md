# Security policy

## Supported versions

DotCharter is a small project. Security fixes go into the **latest release**
only; please update before reporting, and say which version you are on.

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub's private vulnerability reporting:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.
3. Describe the problem, how to reproduce it (a minimal file or steps), the
   version and OS, and what an attacker could do with it.

Only the maintainers can see the report. You will get an answer there; the
advisory, the fix and the credit (if you want it) are coordinated in the same
place before anything is made public.

## What is in scope

Anything that lets untrusted input do more than it should, for example:

- a crafted chart, project (`.rscp`), song package (`.psarc`), tab, audio or
  preset file that crashes the app in a way that corrupts memory, runs code or
  writes outside the folder it should;
- the updater or the release assets (payload manifest, checksums) accepting
  something it should reject;
- the OAuth loopback listener, or any other network surface, exposing data or
  accepting requests it should not;
- secrets (API keys) stored or logged in clear text.

Out of scope: problems in third-party plugins (VST3) you load yourself, and
issues that need an attacker who already controls your user account.
