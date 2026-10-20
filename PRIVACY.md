# Privacy

DotCharter runs on your computer. There is no DotCharter server, no account, no
analytics and no telemetry. Your songs, charts, projects and settings stay on
your machine unless you use one of the features below.

Every connection the app can make is listed here, with when it happens, what is
sent and whose key or account it uses. Anything not listed does not happen. If
you want to check, every address is in the source code, and an automated test
fails when a new address is added without being listed in this file.

## At a glance

| Service | Address | When | What is sent | Whose key |
|---|---|---|---|---|
| Anthropic (Claude) | `api.anthropic.com` | You ask the AI helper something | Your prompt and the chart excerpt it needs | Your own API key |
| OpenAI | `api.openai.com` | Same, if you pick OpenAI | Same | Your own API key |
| OpenRouter | `openrouter.ai` | Same, if you pick OpenRouter | Same | Your own API key |
| Google (Gemini) | `generativelanguage.googleapis.com` | Same, if you pick Gemini | Same | Your own API key |
| DeepSeek | `api.deepseek.com` | Same, if you pick DeepSeek | Same | Your own API key |
| TONE3000 | `www.tone3000.com` | You sign in, search or download amp captures | Your search text and your sign-in token | Your own TONE3000 account |
| MusicBrainz | `musicbrainz.org` | New project suggests Latin-script names for a song | Artist, title and album | None (anonymous) |
| YouTube | `www.youtube.com`, `youtu.be` | You use File ▸ Audio from YouTube | The video link you paste, through yt-dlp | None |
| GitHub | `api.github.com`, `github.com` | Update check at startup (can be turned off), update and model downloads, optional sign-in | The app version in the User-Agent; your token only if you signed in | Your own GitHub account (optional) |
| Hugging Face | `huggingface.co` | You import a stem-separation model from a link you paste | Nothing beyond the download request | None |

## Details

### AI providers (your key, your choice)

The AI helpers do nothing until you give the app an API key for a provider.
Then, and only when you press the button that asks the model for something, the
app sends the request to the provider you chose: your prompt, plus the part of
the chart the request is about. Never the audio. The app sends nothing on its
own, and nothing to a provider you have not given a key for. What the provider
does with the request is governed by that provider's terms and your account with
them. The key is stored on your computer, and it is removed from crash reports
and diagnostics (see below).

You can also point the app at a model running on your own computer (LM Studio
or Ollama on `localhost`), or at the Claude command-line tool you have
installed. Then the request stays local or goes where that tool sends it.

### TONE3000

Signing in opens TONE3000 in your browser. The app listens on `localhost`
(port 47821) only to receive the sign-in answer, and does not accept anything
from other computers. The sign-in token is kept on your computer and is sent
only to TONE3000. Searching and downloading amp captures talk to TONE3000
directly.

### MusicBrainz

When you create a project from audio whose artist, title or album is not in Latin
script, the app can ask MusicBrainz for the official Latin-script names. Only the
artist, title and album are sent, anonymously, limited to one request per
second as MusicBrainz requires. Nothing is applied until you accept it.

### YouTube

This runs only when you use File ▸ Audio from YouTube. The link you paste is
handed to a copy of `yt-dlp` on your computer; it talks to YouTube from your own
connection. You are responsible for the rights to what you download (see
[LEGAL.md](LEGAL.md)).

A build can be made without it. Such a build registers nothing for it, shows no
menu entry and never contacts these hosts; the addresses stay listed here because
they remain in the source.

### GitHub: updates and downloads

By default the app asks GitHub at startup whether a newer release exists. It
also downloads release files from GitHub: updates, voice packs, the lyric model
and the stem-separation model. These are ordinary requests; GitHub sees your IP
address and the app version. Signing in to GitHub (optional, for private release
access) uses GitHub's device sign-in, and the token stays on your computer. The
startup check can be turned off in Settings.

### Hugging Face

Model downloads from Hugging Face happen only when you paste a link yourself
under stem-separation models.

## Terms of the services we connect to

Reviewed 2026-10-14 against each provider's published terms. Status is what the
app does today; an open item has an issue.

| Provider | What is sent | Terms | Status |
|---|---|---|---|
| MusicBrainz | Artist, title and album; a User-Agent of the form `DotCharter/<version> ( <repository URL> )` with the real app version | [API rate limiting and User-Agent rules](https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting) | Compliant. One request per second is enforced in the client; the contact is the repository URL. The version used to be a fixed `1.0` and is now the running version. |
| TONE3000 | Search text and the user's own sign-in token; downloads only on request | [Terms](https://www.tone3000.com/terms), [API terms](https://tone3000.com/api/terms) | Open: the API terms require a "Powered by TONE3000" credit and creator and licence information. Downloads happen only when asked and the catalog is not mirrored. The client id is a public key the owner supplies, not a secret. |
| Anthropic, OpenAI, OpenRouter, Google Gemini, DeepSeek | The user's prompt and chart excerpt, with the user's own API key | [Anthropic](https://www.anthropic.com/legal/commercial-terms), [OpenAI](https://openai.com/policies/business-terms/), [OpenRouter](https://openrouter.ai/terms), [Gemini](https://ai.google.dev/gemini-api/terms), [DeepSeek](https://cdn.deepseek.com/policies/en-US/deepseek-open-platform-terms-of-service.html) | Compliant. Only user-supplied keys, none in the source; each key is used only for its owner's requests. |

## Crash reports

Crash reports are **off by default** and **never sent automatically**. If you
turn on the crash report option in Settings, then after an unhandled error the
app writes a text file to a folder on your computer (the newest 20 are kept).
Nothing is uploaded. You can read the file and decide whether to attach it to a
bug report yourself.

The file contains:

- the app version, the time (UTC) and where the crash was caught;
- the error type, its message and the stack trace (inner errors included);
- system facts: operating system, architecture, .NET runtime, processor count,
  memory in use, display language;
- the last 200 lines of the app log.

Before the file is written, everything in it goes through a redaction pass that
removes your OS user name, the user folder in file paths, access tokens, API
keys, `key=value` pairs that look like secrets, and long hex strings that look
like keys. The redaction is conservative but not a promise; look at the file
before you share it. The diagnostics bundle you can export goes through the same
pass.

## Your files

DotCharter does not upload your audio, charts or projects anywhere. Stem
separation, note detection and lyric alignment run on your computer.

## Changes

When a feature that connects to a new address is added, this file is updated in
the same change. Questions: open an issue on the project's GitHub page.
