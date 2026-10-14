#!/usr/bin/env python3
"""Imports factual game metadata (info and artwork links) from a local catalog data folder.

Usage: python3 -I tools/import_metadata.py <data-folder> <output-folder>

Reads <data>/catalog.js and <data>/pages/*.json. It never reads releases/ and drops every download-related
field (providers, direct, sizeGb, ...). Image URLs are kept only from the publishers' own CDNs.

catalog.json holds facts only and is committed. pages.json holds description text copied from store pages, which
is copyrighted: it stays local (git-ignored) and must not be committed or redistributed.
"""
import json
import re
import sys
from pathlib import Path
from urllib.parse import urlparse

ALLOWED_IMAGE_HOSTS = ("image.api.playstation.com", "steamstatic.com")
GAME_FIELDS = ("id", "title", "platform", "developer", "publisher", "releaseDate", "year",
               "genres", "features", "metacritic")


def allowed(url):
    if not isinstance(url, str):
        return None
    host = urlparse(url).hostname or ""
    return url if url.startswith("https://") and any(
        host == h or host.endswith("." + h) for h in ALLOWED_IMAGE_HOSTS) else None


def load_games(catalog_js):
    text = catalog_js.read_text(encoding="utf-8")
    start = text.index("var games = [") + len("var games = ")
    depth, i, in_str, esc = 0, start, False, False
    while True:
        c = text[i]
        if in_str:
            if esc: esc = False
            elif c == "\\": esc = True
            elif c == '"': in_str = False
        elif c == '"': in_str = True
        elif c == "[": depth += 1
        elif c == "]":
            depth -= 1
            if depth == 0:
                break
        i += 1
    body = re.sub(r"\\x([0-9A-Fa-f]{2})", lambda m: "\\u00" + m.group(1), text[start:i + 1])
    return json.loads(body)


def clean_game(game):
    out = {k: game[k] for k in GAME_FIELDS if k in game and game[k] not in ("", [], None)}
    art = game.get("art") or {}
    covers = [u for u in [allowed(art.get("cover"))] + [allowed(u) for u in art.get("coverFallbacks") or []] if u]
    clean_art = {
        "cover": covers[0] if covers else None,
        "hero": allowed(art.get("hero")),
        "logo": allowed(art.get("logo")),
        "tint": art.get("tint") if isinstance(art.get("tint"), str) else None,
    }
    clean_art = {k: v for k, v in clean_art.items() if v}
    if clean_art:
        out["art"] = clean_art
    return out


def clean_page(page):
    shots = []
    for shot in page.get("shots") or []:
        url, thumb = allowed(shot.get("url")), allowed(shot.get("thumb"))
        if url or thumb:
            shots.append({"url": url or thumb, "thumb": thumb or url})
    about = [p for p in page.get("about") or [] if isinstance(p, str) and p.strip()]
    summary = page.get("summary") if isinstance(page.get("summary"), str) else ""
    if not about and not shots and not summary:
        return None
    return {"summary": summary, "about": about, "shots": shots}


def main():
    data, out = Path(sys.argv[1]), Path(sys.argv[2])
    games = [clean_game(g) for g in load_games(data / "catalog.js") if g.get("id")]
    pages = {}
    for game in games:
        page_path = data / "pages" / (game["id"] + ".json")
        if page_path.is_file():
            page = clean_page(json.loads(page_path.read_text(encoding="utf-8")))
            if page:
                pages[game["id"]] = page
    out.mkdir(parents=True, exist_ok=True)
    (out / "catalog.json").write_text(json.dumps(games, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    (out / "pages.json").write_text(json.dumps(pages, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"{len(games)} games, {len(pages)} pages -> {out}")


if __name__ == "__main__":
    main()
