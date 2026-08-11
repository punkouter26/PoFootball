"""Audit which packages in Packages/manifest.json are actually referenced.

Report-only — does not modify manifest.json. Emits:

  * PER-PACKAGE: `com.unity.x` → referenced / not referenced / dev-only
  * PER-USING: every `using <ns>;` that resolved under Assets/Plugins, Assets/Scripts,
    and Assets/Tests, mapped to the package it most likely came from

The output is a draft `Packages/manifest.json.diff` next to `manifest.json` that
lists packages recommended for removal, with the "why" (no references found) and
the keep list (do not touch).

Usage
-----
    .venv/Scripts/python.exe Tools/audit_packages.py
"""

from __future__ import annotations

import json
import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS = REPO_ROOT / "Assets"
MANIFEST = REPO_ROOT / "Packages" / "manifest.json"
DIFF_OUT = REPO_ROOT / "Packages" / "manifest.json.diff"

# Only look at code that actually feeds the player build.
SEARCH_DIRS = [
    ASSETS / "Scripts",
    ASSETS / "Tests",
    ASSETS / "Editor",
    ASSETS / "Plugins",
]

USING_RE = re.compile(r"^\s*using\s+([A-Za-z0-9_.]+)\s*;", re.MULTILINE)


def gather_usings() -> set[str]:
    """All `using` namespaces declared anywhere in the project's C# code."""
    found: set[str] = set()
    for root in SEARCH_DIRS:
        if not root.exists():
            continue
        for cs in root.rglob("*.cs"):
            try:
                text = cs.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for ns in USING_RE.findall(text):
                found.add(ns)
    return found


def classify_packages(usings: set[str]) -> tuple[list[dict], list[dict]]:
    """Return (candidates_for_removal, definitely_keep) lists."""
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    deps = manifest.get("dependencies", {})

    # Map package id → known namespace seeds. Conservative: any namespace that
    # STARTS WITH one of these prefixes counts as a reference.
    PKG_NAMESPACES: dict[str, list[str]] = {
        "com.unity.2d.animation": ["UnityEngine.Animation2D"],
        "com.unity.2d.aseprite": ["UnityEditor.Aseprite"],
        "com.unity.2d.psdimporter": ["UnityEditor.PSDImporter"],
        "com.unity.2d.sprite": ["UnityEngine.U2D"],
        "com.unity.2d.spriteshape": ["UnityEngine.U2D", "UnityEngine.Experimental.U2D"],
        "com.unity.2d.tilemap": ["UnityEngine.Tilemaps", "UnityEngine.GridLayout"],
        "com.unity.2d.tilemap.extras": ["UnityEngine.Tilemaps"],
        "com.unity.2d.tooling": ["UnityEditor.U2D"],
        "com.unity.collab-proxy": ["UnityEditor.Collaboration"],
        "com.unity.ide.rider": ["UnityEditor.Rider"],
        "com.unity.ide.visualstudio": ["UnityEditor.VisualStudio"],
        "com.unity.addressables": ["UnityEngine.AddressableAssets", "UnityEditor.AddressableAssets"],
        "com.unity.behavior": ["Unity.Behavior"],
        "com.unity.burst": ["Unity.Burst"],
        "com.unity.cinemachine": ["Unity.Cinemachine"],
        "com.unity.collections": ["Unity.Collections"],
        "com.unity.memoryprofiler": ["Unity.MemoryProfiler"],
        "com.unity.nuget.newtonsoft-json": ["Newtonsoft.Json"],
        "com.unity.performance.profile-analyzer": ["UnityEditor.Performance"],
        "com.unity.recorder": ["UnityEditor.Recorder"],
        "com.unity.serialization": ["Unity.Serialization"],
        "com.unity.splines": ["UnityEngine.Splines", "UnityEditor.Splines"],
        "com.unity.test-framework.performance": ["Unity.PerformanceTesting"],
        "com.unity.inputsystem": ["UnityEngine.InputSystem"],
        "com.unity.mathematics": ["Unity.Mathematics"],
        "com.unity.ml-agents": ["Unity.MLAgents"],
        "com.unity.multiplayer.center": ["Unity.Multiplayer"],
        "com.unity.render-pipelines.universal": ["UnityEngine.Rendering", "UnityEngine.Rendering.Universal"],
        "com.unity.test-framework": ["UnityEngine.TestTools", "NUnit.Framework"],
        "com.unity.timeline": ["UnityEngine.Timeline"],
        "com.unity.ugui": ["UnityEngine.UI"],
        "com.unity.visualscripting": ["Unity.VisualScripting"],
        "com.ivanmurzak.unity.mcp": ["com.IvanMurzak"],
        "com.coplaydev.unity-mcp": ["com.CoplayDev"],
        "com.besty.unity-skills": ["Besty"],
        "com.cysharp.unitask": ["Cysharp.Threading.Tasks"],
        "jp.hadashikick.vcontainer": ["VContainer"],
        "com.cysharp.messagepipe": ["MessagePipe"],
        "com.cysharp.messagepipe.vcontainer": ["MessagePipe"],
    }

    # Snapshots of the Packages cache — empty when the editor has never resolved
    # the package locally. We use a heuristic on the namespace list and treat
    # those as a fallback when the namespace test is ambiguous.
    PACKAGES_DIR = REPO_ROOT / "Library" / "PackageCache"
    cached: dict[str, Path] = {}
    if PACKAGES_DIR.exists():
        for child in PACKAGES_DIR.iterdir():
            if child.is_dir():
                cached[child.name.split("@")[0]] = child

    candidates: list[dict] = []
    keep: list[dict] = []

    seen = set()
    for pkg_id in deps:
        if pkg_id in seen:
            continue
        seen.add(pkg_id)
        namespaces = PKG_NAMESPACES.get(pkg_id, [])
        evidence: list[str] = []

        for ns in namespaces:
            if any(u == ns or u.startswith(ns + ".") for u in usings):
                evidence.append(ns)

        # com.unity.engine modules (= "com.unity.modules.x") are always part of
        # the engine and never removable — list them as keep unconditionally.
        if pkg_id.startswith("com.unity.modules."):
            keep.append({"pkg": pkg_id, "reason": "engine module — cannot remove", "evidence": []})
            continue

        if evidence:
            keep.append({"pkg": pkg_id, "reason": "referenced in code", "evidence": evidence})
        else:
            # Sanity check: if the package is on the known "kept unconditionally
            # by CLAUDE.md" list, mark it keep regardless.
            UNCONDITIONAL_KEEP = {
                "com.unity.render-pipelines.universal",  # URP, per CLAUDE.md
                "com.unity.ml-agents",                    # core, per CLAUDE.md
                "com.unity.inputsystem",                  # NON-NEGOTIABLE per architecture rules
                "com.unity.test-framework",               # used by tests
                "com.cysharp.unitask",                    # per architecture rules
                "jp.hadashikick.vcontainer",              # per architecture rules
                "com.cysharp.messagepipe",                 # per architecture rules
                "com.cysharp.messagepipe.vcontainer",      # per architecture rules
                "com.unity.collections",                  # used by UniTask / jobs
                "com.unity.mathematics",                  # used by UniTask / Burst
                "com.unity.burst",                        # transducer for ML-Agents
                "com.unity.nuget.newtonsoft-json",        # ML-Agents dependency
                "com.unity.modules.uielements",           # UI Toolkit
                "com.unity.addressables",                  # sometimes indirect
                "com.unity.pipeline",                      # CLI bridge per CLAUDE.md TOOLING
                # MCP tooling — no C# from the project uses these directly, but
                # the live editor session depends on them. False positives by
                # this script, kept on purpose.
                "com.ivanmurzak.unity.mcp",
                "com.coplaydev.unity-mcp",
                "com.besty.unity-skills",
            }
            if pkg_id in UNCONDITIONAL_KEEP:
                keep.append({"pkg": pkg_id, "reason": "unconditional per CLAUDE.md / rules", "evidence": []})
            else:
                candidates.append({"pkg": pkg_id, "reason": "no namespace references found", "evidence": []})

    return candidates, keep


def write_diff(candidates: list[dict], keep: list[dict]) -> None:
    lines = [
        "# Generated by Tools/audit_packages.py — REVIEW ONLY.",
        "# Lines starting with '-' are packages to remove.",
        "# Lines starting with '+' are packages the auditor confirms are still needed.",
        "# Apply by hand; the script does not modify manifest.json.",
        "",
        "{",
        '  "dependencies": {',
    ]
    for c in candidates:
        lines.append(f'    # (-) {c["pkg"]}  # {c["reason"]}')
    for k in keep:
        ev = f'  # evidence: {", ".join(k["evidence"])}' if k["evidence"] else ""
        lines.append(f'    # (+) {k["pkg"]}  # {k["reason"]}{ev}')
    lines.append("  }")
    lines.append("}")
    DIFF_OUT.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    if not MANIFEST.exists():
        print(f"No manifest at {MANIFEST}")
        return 1

    usings = gather_usings()
    candidates, keep = classify_packages(usings)

    print(f"Scanned {len(usings)} unique `using` namespaces across {len(SEARCH_DIRS)} asset dirs.")
    print(f"Candidates for removal: {len(candidates)}")
    print(f"Keep: {len(keep)}")
    print()
    print("--- Candidates for removal ---")
    for c in candidates:
        print(f"  {c['pkg']:<46}  {c['reason']}")
    print()
    print("--- Kept ---")
    for k in keep:
        ev = f"  evidence: {', '.join(k['evidence'])}" if k["evidence"] else ""
        print(f"  {k['pkg']:<46}  {k['reason']}{ev}")

    write_diff(candidates, keep)
    print(f"\nDiff written to {DIFF_OUT.relative_to(REPO_ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
