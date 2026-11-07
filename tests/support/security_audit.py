"""Scan publishable source and reachable history; never print credential values."""
import json
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[2]


def git(*args):
    result = subprocess.run(["git", "--no-optional-locks", *args], cwd=ROOT, capture_output=True, check=True)
    return result.stdout


PATTERNS = {
    "telegram_bot_token": re.compile(rb"\b[0-9]{6,14}:[A-Za-z0-9_-]{20,}\b"),
    "github_token": re.compile(rb"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})\b"),
    "aws_access_key": re.compile(rb"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
    "private_key": re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
}
credentials = []
for name in ("appsettings.json", "telegram_config.json", "appsettings.Development.json", "appsettings.Production.json"):
    try:
        token = json.loads((ROOT / name).read_text(encoding="utf-8-sig")).get("Telegram", {}).get("BotToken", "")
        if PATTERNS["telegram_bot_token"].fullmatch(token.encode()):
            credentials.append(token.encode())
    except (OSError, ValueError, AttributeError):
        pass


def scan(data, path, revision=None):
    findings = []
    for category, pattern in PATTERNS.items():
        for match in pattern.finditer(data):
            findings.append({"path": path, "line": data[:match.start()].count(b"\n") + 1,
                             "category": category, **({"revision": revision} if revision else {})})
    for credential in set(credentials):
        if credential in data and not PATTERNS["telegram_bot_token"].search(data):
            findings.append({"path": path, "category": "configured_credential", **({"revision": revision} if revision else {})})
    return findings


paths = sorted(set(p.decode("utf-8", "replace") for p in git("ls-files", "--cached", "--others", "--exclude-standard", "-z").split(b"\0") if p))
worktree = []
for path in paths:
    source = ROOT / path
    if source.is_file():
        worktree.extend(scan(source.read_bytes(), path))
        if (source.name in {"telegram_config.json", "appsettings.json", "appsettings.Development.json", "appsettings.Production.json"}
                or source.name == ".env" or source.name.startswith(".env.") and source.name != ".env.example"):
            worktree.append({"path": path, "category": "private_configuration_in_publish_set"})

index = []
for raw_path in git("ls-files", "--cached", "-z").split(b"\0"):
    if raw_path:
        path = raw_path.decode("utf-8", "replace")
        index.extend(scan(git("show", ":" + path), path))

history = []
seen = set()
revisions = git("rev-list", "--all").decode().splitlines()
for revision in revisions:
    for entry in git("ls-tree", "-r", "-z", revision).split(b"\0"):
        if not entry:
            continue
        metadata, raw_path = entry.split(b"\t", 1)
        _mode, kind, oid = metadata.split()
        if kind != b"blob" or oid in seen:
            continue
        seen.add(oid)
        data = git("cat-file", "blob", oid.decode())
        history.extend(scan(data, raw_path.decode("utf-8", "replace"), revision[:12]))

remote = git("config", "--get", "remote.origin.url").decode().strip()
if "://" in remote:
    parsed = urlsplit(remote)
    remote_name = (parsed.hostname or "") + parsed.path
else:
    remote_name = remote.split("@")[-1].replace(":", "/", 1)
report = {"remote": remote_name, "publishable_files": len(paths), "history_commits": len(revisions),
          "history_blobs": len(seen), "worktree_findings": worktree, "index_findings": index, "history_findings": history}
print(json.dumps(report, indent=2))
sys.exit(1 if worktree or index else 2 if history else 0)
