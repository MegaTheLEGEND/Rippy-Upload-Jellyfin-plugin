# Rippy Upload (Jellyfin plugin)

Receives MP4/MKV uploads from [Rippy](https://github.com/MegaTheLEGEND/Rippy) and saves them to an administrator-configured directory, then refreshes the Jellyfin library. Only Jellyfin users the administrator has explicitly allowed can upload.

Targets Jellyfin **10.11.x** (.NET 9). For another server version, edit `JellyfinVersion` (and `TargetFramework` for Jellyfin 12+, which uses .NET 10) in `Jellyfin.Plugin.RippyUpload/Jellyfin.Plugin.RippyUpload.csproj` and `targetAbi` in `meta.json`.

## Build and install

```bash
dotnet publish Jellyfin.Plugin.RippyUpload -c Release -o out
```

Copy `out/Jellyfin.Plugin.RippyUpload.dll` and `meta.json` into a new folder in the Jellyfin plugins directory, for example `<jellyfin-data>/plugins/RippyUpload_1.0.0.0/`, then restart Jellyfin.

## Configuration

Dashboard → Plugins → **Rippy Upload** (also in the Dashboard sidebar):

| Setting | Meaning |
| --- | --- |
| Upload destination directory | Absolute path, writable by the Jellyfin service, ideally inside a library folder. Created if missing. Uploads fail with 503 until set. |
| Maximum file size (MB) | Per-upload limit. `0` = unlimited. Default 10240 (10 GB). |
| Per-user quota (MB) | Most data one user may have in the upload directory at once. Default 10240 (10 GB), `0` = unlimited. See "Per-user quota" below. |
| Maximum concurrent uploads | Extra uploads get HTTP 429. Default 2. |
| Allow video uploads | One checkbox per Jellyfin user. Off for everyone by default, administrators included. |

Jellyfin has no official plugin extension point for adding fields to its native Users > user pages, so permissions are managed on this plugin page. They are stored by Jellyfin user ID in the plugin configuration file and persist across restarts.

## Per-user quota

Intended workflow: users upload into the destination (a review/limbo folder, including `_Needs Review` and its subfolders), and an administrator later moves finished files into the main libraries.

- The plugin records which user uploaded each file (`RippyUpload.usage.json` in Jellyfin's plugin configuration folder). A user's usage is the total size of their uploaded files that are **still in the destination**. Moving or deleting a file (or changing it so its size differs) frees that space automatically.
- Uploads that would push a user past the quota get `507` `quota_exceeded`. Space for in-progress uploads is reserved up front, so parallel uploads cannot overshoot.
- Usage is shown next to each user on the plugin page. Users can read their own numbers from `GET /Rippy/Status`.
- Only files uploaded after this plugin was installed are attributed. Renaming a file inside the destination also frees its quota. If the destination folder is unavailable (for example an unmounted drive), nothing is pruned.

## Authentication and authorization

Every request must carry a Jellyfin user session token (from `POST /Users/AuthenticateByName`). Send it as either:

```
Authorization: MediaBrowser Client="Rippy", Device="Rippy", DeviceId="<stable id>", Version="1.0", Token="<token>"
```

or `X-Emby-Token: <token>`.

The server takes the user identity from the authenticated session, then checks it against the allow-list. Administrator-created API keys carry no user identity and are rejected (401).

## API

### `POST /Rippy/Upload?filename=<name>[&folder=<relative/path>]`

- **Body:** the raw file bytes (not multipart). Streamed to disk; never buffered in memory.
- **Content-Type:** `video/mp4`, `video/x-matroska`, or `application/octet-stream`. Anything else gets 415.
- **Filename:** `filename` query parameter (URL-encoded), or `X-Filename` header if the parameter is absent. It must be a plain name ending in `.mp4` or `.mkv` (case-insensitive). Normalized to Unicode NFC. Rejected: path separators, `:` `<` `>` `"` `|` `?` `*`, `..`, leading/trailing dots, leading `-`, leading/trailing spaces, control/format (e.g. bidi override)/private-use characters, names over 200 UTF-8 bytes (and, on Windows, reserved device names and `~`). Files are stored under exactly this name directly in the destination directory.
- **Folder (optional):** `folder` query parameter (URL-encoded), a relative path with `/` between levels, for example `_Needs Review/Some Show/Season 1`. It is created under the destination directory if missing. Up to 4 levels. Each level follows the same character rules as filenames; `..`, leading dots, backslashes, empty levels, and absolute paths are rejected (400 `invalid_folder`). Omit it to store the file at the top of the destination directory.
- **Required header:** `X-Content-SHA256: <64 hex chars>` (SHA-256 of the file). Requests without it are rejected (400), and so are uploads whose received data does not match it (422).
- The first bytes are checked against the extension (MP4 `ftyp` box, Matroska EBML header).
- Data is written to `<destination>/.rippy-incoming/<random>.part` and moved to its final name only after the full body has arrived and passed all checks. Interrupted uploads are deleted. Existing files are never overwritten.

**Success: `201 Created`**

```json
{"status": "ok", "filename": "Movie.mkv", "folder": "", "size": 123456789, "sha256": "<hex>"}
```

**Errors** (JSON `{"error": "<code>", "message": "<text>"}` except where noted):

| Status | `error` | Cause |
| --- | --- | --- |
| 400 | `invalid_filename` | Missing, unsafe, or wrong-extension filename |
| 400 | `invalid_folder` | Unsafe, empty-level, or too deeply nested `folder` |
| 400 | `invalid_content` | Content does not match the extension |
| 400 | `missing_checksum` / `invalid_checksum` | `X-Content-SHA256` missing or malformed |
| 400 | `empty_body` / `incomplete` / `aborted` | Empty, truncated, or interrupted upload |
| 401 | `unauthenticated` | No/invalid token (empty body when rejected by Jellyfin itself), or API key used |
| 403 | `forbidden` | User is not allowed to upload |
| 409 | `exists` | A file with this name already exists |
| 413 | `too_large` | Exceeds the configured limit |
| 415 | n/a | Unsupported Content-Type |
| 422 | `checksum_mismatch` | SHA-256 differs from `X-Content-SHA256` |
| 429 | `busy` | Concurrent upload limit reached (`Retry-After: 30`) |
| 507 | `quota_exceeded` | The user's quota in the upload directory would be exceeded |
| 500 | `storage_error` | Destination not writable or write failed |
| 503 | `not_configured` | Destination not set or not an absolute path |

A library scan is queued after each successful upload; bursts of uploads are coalesced.

### `GET /Rippy/List?folder=<relative/path>[&recursive=true]`

Lets Rippy see what has already been uploaded, so it can skip duplicates and continue TV episode numbering. Same authentication and the same permission as uploading (401/403 otherwise). `folder` follows the upload rules; omit it for the destination root. Only `.mp4`/`.mkv` files are listed; hidden entries, `.part` files, and symlinks are skipped. Returns at most 10000 entries (`truncated: true` if cut off).

```json
{"exists": true, "folder": "_Needs Review/Some Show", "files": [{"path": "Season 1/Some Show - S01E01.mp4", "size": 734003200}], "folders": ["Season 1"], "truncated": false}
```

If the folder does not exist the response is `200` with `"exists": false` and empty lists. Without `recursive`, only direct children are returned (up to 4 levels deep with `recursive=true`). Paths are relative to the listed folder. Files an administrator later moves out of the destination are no longer listed; uploading a name that already exists in the target folder still returns 409.

### `GET /Rippy/Status`

Same authentication. Returns `200` with:

```json
{"canUpload": true, "configured": true, "maxFileSizeBytes": 17179869184, "usedBytes": 3221225472, "quotaBytes": 10737418240, "allowedExtensions": [".mp4", ".mkv"]}
```

`maxFileSizeBytes` and `quotaBytes` are `0` when unlimited; `usedBytes` is what this user currently has in the destination. Rippy can call this before sending a large file.

## Python example (for future Rippy integration)

```python
import hashlib
import os
import requests

SERVER = "http://jellyfin:8096"
AUTH = 'MediaBrowser Client="Rippy", Device="Rippy", DeviceId="rippy-1", Version="1.0"'


def login(username: str, password: str) -> str:
    r = requests.post(
        f"{SERVER}/Users/AuthenticateByName",
        json={"Username": username, "Pw": password},
        headers={"Authorization": AUTH},
        timeout=30,
    )
    r.raise_for_status()
    return r.json()["AccessToken"]


def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def upload(path: str, token: str, folder: str = "") -> dict:
    name = os.path.basename(path)
    content_type = "video/mp4" if name.lower().endswith(".mp4") else "video/x-matroska"
    headers = {
        "Authorization": f'{AUTH}, Token="{token}"',
        "Content-Type": content_type,
        "X-Content-SHA256": sha256_of(path),
    }
    with open(path, "rb") as f:  # file object => streamed, not loaded into memory
        r = requests.post(
            f"{SERVER}/Rippy/Upload",
            params={"filename": name, "folder": folder} if folder else {"filename": name},
            headers=headers,
            data=f,
            timeout=(10, 3600),
        )
    if r.status_code != 201:
        raise RuntimeError(f"{r.status_code}: {r.text}")
    return r.json()
```

Python helper for the list call:

```python
def list_uploaded(token: str, folder: str = "", recursive: bool = False) -> dict:
    r = requests.get(
        f"{SERVER}/Rippy/List",
        params={"folder": folder, "recursive": str(recursive).lower()},
        headers={"Authorization": f'{AUTH}, Token="{token}"'},
        timeout=30,
    )
    r.raise_for_status()
    return r.json()
```

## Security review

Checked against the OWASP File Upload Cheat Sheet:

| Control | Status |
| --- | --- |
| Extension allow-list (`.mp4`, `.mkv`), case-insensitive, checked after normalization | Done |
| Don't trust Content-Type; verify file signature | Content-Type allow-listed (415) and signature checked against extension. The signature check is a sanity check, not proof the file is a valid or harmless video. |
| Filename safety (length, charset, traversal, NTFS streams, reserved names) | Done. Client-supplied names are kept (Jellyfin metadata matching needs them) instead of random names; collisions are rejected, never overwritten. |
| Size limit | Done (configurable, enforced on Content-Length and on bytes actually received) |
| Authentication and authorization | Done. Jellyfin session token plus per-user allow-list, checked server-side before the body is read |
| Store outside the web root | Done by design: files are written to the library directory and served only through Jellyfin's authenticated API |
| Least-privilege file permissions | On Linux/macOS files are created `rw-r-----`. Make sure the Jellyfin user owns the destination directory. |
| CSRF | Not applicable: authentication is a token header, not a cookie |
| Integrity | Mandatory SHA-256; incomplete or mismatched uploads are discarded |
| Antivirus / sandboxing | Not included. Scan the destination directory (e.g. ClamAV) if untrusted users can upload. |
| Disk exhaustion | Per-file cap, concurrency cap, and a per-user quota on what is currently in the upload directory. Put the destination on its own volume or quota so uploads cannot fill the system disk. Stale `.part` files older than 24 h are removed on the next upload. |
| Transport | Use HTTPS (reverse proxy). Send the token in the `Authorization` header, not as an `api_key` query parameter, which Jellyfin also accepts but which ends up in logs. |

### Matching Rippy's folder layout

Rippy stores confirmed movies at the top level and anything it is unsure about under `_Needs Review`. Pass its relative folder as `folder`:

| Rippy output | `folder` | `filename` |
| --- | --- | --- |
| Confirmed movie | (omit) | `Title (Year).mp4` |
| Unconfirmed movie | `_Needs Review` | `Title (Year).mp4` |
| TV episode | `_Needs Review/<Show>/Season 1` | `<Show> - S01E03.mp4` |

To check for existing episodes, call `GET /Rippy/List?folder=_Needs Review/<Show>&recursive=true`; to check one movie, list the folder it would go in and look for its name.

Set the plugin's destination to the root Rippy would use on the NAS. Trailing dots in show names (Windows strips them anyway) must be removed before sending.

## Notes

- Behind a reverse proxy, allow large bodies and disable request buffering (nginx: `client_max_body_size 0; proxy_request_buffering off;`).
- Removing a user's checkbox takes effect on their next request; deleting a user invalidates their tokens.
- This plugin has not been built or tested by its author in this repository snapshot; build and verify it against your server version.
