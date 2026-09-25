#!/usr/bin/env python3
"""helper for the Lyrics HUD mod.

runs on the linux side (outside proton). it reads whatever is playing over
MPRIS using playerctl, grabs synced lyrics from lrclib.net and the album art,
and serves everything on http://127.0.0.1:8765 so the mod can poll it.

  GET /state          -> 10 lines of text:
                         status, title, artist, album, position (s), length (s),
                         art id, previous lyric, current lyric, next lyric
  GET /art            -> the current album art (png or jpeg)
  GET /cmd/playpause  -> toggles play/pause
  GET /cmd/next       -> next track
  GET /cmd/previous   -> previous track
"""
import bisect
import hashlib
import json
import re
import shutil
import subprocess
import textwrap
import threading
import time
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = 8765
LRCLIB = "https://lrclib.net/api"
USER_AGENT = "gorilla-tag-lyrics-hud/2.0"
MAX_ART_BYTES = 4_000_000
STAMP = re.compile(r"\[(\d+):(\d+(?:\.\d+)?)\]")
FORMAT = (
    "{{playerName}}\t{{status}}\t{{title}}\t{{artist}}\t{{album}}"
    "\t{{mpris:artUrl}}\t{{position}}\t{{mpris:length}}"
)
FIELDS = 8
STATUS_RANK = {"Playing": 0, "Paused": 1}
COMMANDS = {"playpause": "play-pause", "next": "next", "previous": "previous"}
EMPTY_STATE = "None\n\n\n\n\n\n\n\n\n"

# (artist, title) -> None while loading, otherwise a list of (seconds, text)
lyrics_cache = {}
# art url -> None while loading, otherwise raw image bytes (empty if it failed)
art_cache = {}
state = EMPTY_STATE
active_player = ""
current_art_url = ""


def tidy(text, width=60):
    text = re.sub(r"<[^>]*>", "", text)
    text = text.replace("<", "").replace(">", "").replace("\n", " ").strip()
    return textwrap.shorten(text, width, placeholder="...") if text else ""


def lyric_line(text):
    # blank timestamps are instrumental gaps
    return tidy(text, 48) or "..."


def parse_lrc(raw):
    lines = []
    for row in raw.splitlines():
        text = STAMP.sub("", row).strip()
        for minutes, seconds in STAMP.findall(row):
            lines.append((int(minutes) * 60 + float(seconds), text))
    lines.sort(key=lambda item: item[0])
    return lines


def http_json(url):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=8) as response:
        return json.loads(response.read().decode("utf-8"))


def fetch_lyrics(artist, title, length):
    found = []
    try:
        params = {"artist_name": artist, "track_name": title}
        if length > 0:
            params["duration"] = int(length)
        try:
            data = http_json(f"{LRCLIB}/get?{urllib.parse.urlencode(params)}")
        except Exception:
            query = urllib.parse.urlencode({"track_name": title, "artist_name": artist})
            results = http_json(f"{LRCLIB}/search?{query}")
            data = next((r for r in results if r.get("syncedLyrics")), {})
        if data.get("syncedLyrics"):
            found = parse_lrc(data["syncedLyrics"])
    except Exception as err:
        print(f"lyrics lookup failed for {artist} - {title}: {err}")
    lyrics_cache[(artist, title)] = found


def fetch_art(url):
    data = b""
    try:
        if url.startswith("file://"):
            path = urllib.parse.unquote(urllib.parse.urlparse(url).path)
            with open(path, "rb") as handle:
                data = handle.read(MAX_ART_BYTES + 1)
        elif url.startswith(("http://", "https://")):
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=8) as response:
                data = response.read(MAX_ART_BYTES + 1)
    except Exception as err:
        print(f"album art fetch failed: {err}")
    if len(data) > MAX_ART_BYTES:
        data = b""
    art_cache[url] = data


def to_seconds(micro):
    try:
        return int(micro) / 1_000_000
    except ValueError:
        return 0.0


def query_player():
    global active_player
    try:
        result = subprocess.run(
            ["playerctl", "-a", "metadata", "--format", FORMAT],
            capture_output=True, text=True, timeout=2,
        )
    except Exception:
        return None
    rows = [line.split("\t") for line in result.stdout.splitlines()]
    rows = [row for row in rows if len(row) == FIELDS]
    if not rows:
        active_player = ""
        return None
    # prefer whatever is actually playing, then paused, then anything else
    player, status, title, artist, album, art_url, position, length = min(
        rows, key=lambda row: STATUS_RANK.get(row[1], 2)
    )
    active_player = player
    return status, title, artist, album, art_url, to_seconds(position), to_seconds(length)


def lyric_at(lines, index):
    if 0 <= index < len(lines):
        return lyric_line(lines[index][1])
    return ""


def build_state():
    global current_art_url
    info = query_player()
    if info is None:
        current_art_url = ""
        return EMPTY_STATE
    status, title, artist, album, art_url, position, length = info

    key = (artist, title)
    if key not in lyrics_cache:
        lyrics_cache[key] = None
        threading.Thread(target=fetch_lyrics, args=(artist, title, length), daemon=True).start()

    current_art_url = art_url
    if art_url and art_url not in art_cache:
        art_cache[art_url] = None
        threading.Thread(target=fetch_art, args=(art_url,), daemon=True).start()
    # only advertise the art once it has actually been downloaded
    art_id = hashlib.md5(art_url.encode()).hexdigest()[:12] if art_cache.get(art_url) else ""

    lines = lyrics_cache[key]
    if lines is None:
        before, now, after = "", "looking up lyrics...", ""
    elif not lines:
        before, now, after = "", "no synced lyrics found", ""
    else:
        i = bisect.bisect_right([t for t, _ in lines], position) - 1
        before, now, after = lyric_at(lines, i - 1), lyric_at(lines, i), lyric_at(lines, i + 1)

    return "\n".join([
        status, tidy(title, 30), tidy(artist, 34), tidy(album, 34),
        f"{position:.2f}", f"{length:.2f}", art_id, before, now, after,
    ])


def poll_loop():
    global state
    while True:
        state = build_state()
        time.sleep(0.2)


class Handler(BaseHTTPRequestHandler):
    def reply(self, code, body=b"", content_type="text/plain; charset=utf-8"):
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/state":
            self.reply(200, state.encode("utf-8"))
        elif self.path == "/art":
            data = art_cache.get(current_art_url) or b""
            if data:
                kind = "image/png" if data.startswith(b"\x89PNG") else "image/jpeg"
                self.reply(200, data, kind)
            else:
                self.reply(404)
        elif self.path.startswith("/cmd/") and self.path[5:] in COMMANDS:
            target = ["-p", active_player] if active_player else []
            subprocess.Popen(["playerctl", *target, COMMANDS[self.path[5:]]])
            self.reply(200, b"ok")
        else:
            self.reply(404)

    def log_message(self, *args):
        pass


def main():
    if shutil.which("playerctl") is None:
        raise SystemExit("playerctl not found, install it first (paru -S playerctl)")
    threading.Thread(target=poll_loop, daemon=True).start()
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"lyrics helper running on http://127.0.0.1:{PORT} (ctrl+c to stop)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
