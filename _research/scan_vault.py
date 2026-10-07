import re, html, subprocess, sys, json

TARGETS = [
    # (id, label)
    ("1025725", "Classic Game Design Postmortem: Swinging with Spider-Man (Treyarch, GDC2019)"),
    ("1026422", "Concrete Jungle Gym (Insomniac, GDC2019)"),
    ("1026084", "Concrete Jungle Gym alt id"),
    ("1035867", "Grappling with Success: Smooth Movement"),
    ("1034283", "Higher Faster Farther: Evolving Traversal"),
    ("1029003", "Companion Traversal in God of War"),
    ("1027649", "NARAKA: BLADEPOINT From Prototype"),
    ("1025208", "Parkour: How to Improve Freedom"),
    ("1024699", "Player Traversal Mechanics in the ..."),
    ("1024117", "Player Traversal Mechanics alt id"),
    ("1019687", "The Evolution of Sonic Dashing"),
    ("1014620", "DONKEY KONG: Swinging Across"),
    ("1029215", "Level Design Summit: Moving Mountains"),
    ("1034293", "Between Legacy and Modernity (Assassin's Creed)"),
    ("1017767", "One with Lara: The Croft ..."),
    ("1012661", "Among Friends: An Uncharted 2 ..."),
    ("1025208", "Parkour"),
]

def fetch(sid):
    url = "https://www.gdcvault.com/play/%s/" % sid
    p = subprocess.run(["curl", "-sL", "--max-time", "45", url],
                       capture_output=True)
    return url, p.stdout.decode("utf-8", "replace")

def parse(url, h):
    out = {"url": url, "title": None, "speaker": None, "company": None,
           "year": None, "slides": [], "iframes": []}
    b = re.sub(r"(?is)<script.*?</script>", " ", h)
    b = re.sub(r"(?is)<style.*?</style>", " ", b)
    b = html.unescape(b)

    def field(label):
        m = re.search(re.escape(label) + r":\s*</?[^>]*>\s*([^<]{2,120})", b)
        return m.group(1).strip() if m else None

    out["speaker"] = field("Speaker(s)") or field("Speaker")
    out["company"] = field("Company Name(s)")
    # title
    m = re.search(r"Session Name:\s*</?[^>]*>\s*([^<]{5,160})", b)
    if m:
        out["title"] = m.group(1).strip()
    # conference / year from dataLayer
    m = re.search(r"'conferenceName':\s*'([^']+)'", h)
    if m:
        out["year"] = m.group(1)
    m = re.search(r"'sessionID':\s*'([^']+)'", h)
    if m:
        out.setdefault("sessionID", m.group(1))
    m = re.search(r"'sessionTrack':\s*'([^']+)'", h)
    if m:
        out["track"] = m.group(1)

    # slides / downloads anywhere in raw html (incl. scripts)
    for m in re.finditer(r'(?i)["\'(]([^"\'()\s]*(?:\.pdf|/slides/|presentation|download)[^"\'()\s]*)["\')]',
                         h):
        u = m.group(1)
        if u not in out["slides"]:
            out["slides"].append(u)
    for m in re.finditer(r'(?i)src="([^"]*blazestreaming[^"]*)"', h):
        out["iframes"].append(m.group(1))
    # any visible "Slides" word?
    out["hasSlidesWord"] = bool(re.search(r'(?i)>\s*Slides\s*<', b))
    return out

results = []
for sid, label in TARGETS:
    url, h = fetch(sid)
    if not h.strip():
        results.append({"id": sid, "label": label, "error": "empty fetch"})
        continue
    r = parse(url, h)
    r["id"] = sid
    r["label"] = label
    results.append(r)

json.dump(results, open("vault_scan.json", "w", encoding="utf-8"),
          indent=1, ensure_ascii=False)

for r in results:
    print("=" * 78)
    print(r["id"], "|", r.get("label", ""))
    if r.get("error"):
        print("   ERROR:", r["error"]); continue
    print("   title   :", r.get("title"))
    print("   speaker :", r.get("speaker"), "|", r.get("company"))
    print("   conf    :", r.get("year"), "| track:", r.get("track"))
    print("   slidesWd:", r.get("hasSlidesWord"))
    for s in r["slides"][:6]:
        print("   SLIDE?:", s)
    if not r["slides"]:
        print("   SLIDE?: (none found in html)")
