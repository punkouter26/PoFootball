# Tooling inventory

Everything installed for this project, and — because several of these overlap —
which one to reach for first.

## Precedence

1. **Unity CLI + `com.unity.pipeline`** — the preferred surface for driving the
   Editor. First-party, versioned with the Editor, and the only one that also
   covers install/build/test/licensing.
2. **MCP tools** for scene and GameObject work. Scenes are authored through MCP,
   never by hand-editing `.unity` YAML and never through a bespoke Editor script
   written for the occasion.
3. Everything else is a convenience layer over those two.

## Unity CLI

`unity` 1.0.0-beta.3, installed from the beta channel:

```powershell
$env:UNITY_CLI_CHANNEL='beta'; irm https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1 | iex
```

Signed in as `punkouter26@gmail.com` (`unity auth status`).

| Command | Use |
|---|---|
| `unity status` | Which Editors are connected, on which port, in which state |
| `unity list` | Tools the Pipeline package exposes on the connected Editor |
| `unity command <name> [args]` | Invoke one of those tools |
| `unity pipeline list` / `install` / `upgrade` | Manage the Pipeline package |
| `unity test <project>` | EditMode/PlayMode tests, writes a results report |
| `unity build <project>` | Batch-mode build (used for `Builds/FootballEnv/`) |
| `unity run <project>` | Batch mode, forwards args to the Editor |
| `unity open <project>` | Open with the matching Editor version |
| `unity shell` | REPL — many commands in one warm process |
| `unity doctor` | Diagnostics |

Docs: <https://docs.unity.com/en-us/unity-cli/use-unity-cli>

`com.unity.pipeline` **0.4.0-exp.1** is what makes `status`/`list`/`command` work —
it runs a local HTTP API inside the Editor (port 7800 by default). Without it the
CLI can manage installs but cannot drive a running Editor. It also provides the
`eval` / `eval_file` commands and the MCP server surfaced by `unity mcp`.

## MCP servers

| Server | Client | Points at |
|---|---|---|
| `unity-editor-mcp` | Claude Code (**local** scope) | `unity mcp --project-path .../PoFootball` |
| `unity-pofootball` | Claude Desktop | same |
| `unity` | Claude Desktop | a *different* project (PoSumo) — left alone deliberately |
| `unityMCP` | Claude Desktop | CoplayDev server via `uvx mcpforunityserver` |

The Claude Code entry is **local scope on purpose**. A user-scope
`unity-editor-mcp` already existed pointing at another project; local scope wins
inside this directory without breaking that one.

```powershell
unity mcp configure --list      # every supported client and its config path
unity mcp configure claude      # Claude Desktop  (--yes to overwrite)
unity mcp configure claude-code # delegates to `claude mcp add --scope user`
```

## Unity packages

Added on top of the 2D/URP template:

| Package | Version | Why |
|---|---|---|
| `com.unity.ml-agents` | 4.1.0 | Training. Comms API 1.5.0 |
| `com.unity.ai.inference` | 2.6.1 | Pulled in by ML-Agents; runs the `.onnx` at inference |
| `com.unity.pipeline` | 0.4.0-exp.1 | Unity CLI ↔ Editor control |
| `com.unity.cinemachine` | 3.1.7 | Camera rigs for following the ball / broadcast framing |
| `com.unity.mathematics` | 1.3.3 | SIMD math for per-tick sensor work |
| `com.unity.burst` | 1.8.30 | Compiles the hot paths; matters at `time_scale: 20` × 6 envs |
| `com.ivanmurzak.unity.mcp` | 0.87.0 | MCP bridge + AI Game Developer window |
| `com.coplaydev.unity-mcp` | git `MCPForUnity` | MCP bridge (Coplay) |
| `com.besty.unity-skills` | git `SkillsForUnity` | 784 REST-exposed Editor skills |

`com.unity.ml-agents` 4.1.0 is newer than the release_23 tag (4.0.0). Both report
`k_ApiVersion = "1.5.0"`, which is what the handshake actually compares against
`mlagents_envs`' `API_VERSION`, so PyPI `mlagents` 1.1.0 pairs with either.

> Four Editor-automation packages coexist here because all four were requested.
> They each stand up their own bridge/server, so if the Editor ever fails to
> compile or two of them fight over a port, remove all but `com.unity.pipeline`
> first and re-add one at a time.

## Claude Code layer

`.claude/` comes from [everything-claude-unity](https://github.com/XeldarAlz/everything-claude-unity):
20 agents, 27 commands, 42 skills, 26 hooks, 5 rule files.

Commands worth knowing: `/unity-doctor` (verify the install), `/unity-workflow`,
`/unity-feature`, `/unity-fix`, `/unity-review`, `/unity-optimize`, `/unity-test`,
`/unity-build`, `/unity-ralph` (persistent verify-fix loop), `/unity-team`.

Its `PreToolUse` hooks **block direct edits to `.unity` and `.meta` files**. That is
the "author scenes through MCP" rule being enforced mechanically — don't route
around it.

`.claude/state/` and `.claude/settings.local.json` are per-machine and git-ignored;
the rest of `.claude/` is committed.

Unity-Skills needs one manual step inside the Editor: `Window > UnitySkills > AI
Config`, pick Claude Code, Install. It writes skills into `~/.claude/skills/`.
IvanMurzak's plugin likewise exposes `Window > AI Game Developer` →
"Auto-generate Skills".

## Python

`.venv/` — Python 3.10.11, `mlagents` 1.1.0, torch 2.5.1+cu121, CUDA verified
against the RTX 2060. Recreate from `requirements.txt`.

`setuptools` is pinned `<81`: 81 removed `pkg_resources`, which
`mlagents.torch_utils` imports at module scope, so a newer setuptools breaks
`mlagents-learn` at startup with `ModuleNotFoundError`.

## Git

- `.gitignore` — Unity template plus `.venv/`, `results/`, `runs/`, TensorBoard
  events, `.claude/state/`, `.ai-game-dev/`; re-includes `Assets/Agents/**/*.onnx`.
- `.gitattributes` — Unity template plus `*.onnx`/`*.nn` through LFS and
  `*.yaml`/`*.py` forced to LF.
- Unity SmartMerge is registered as the `unityyamlmerge` driver, so conflicting
  scene/prefab YAML gets a real three-way merge:

  ```
  merge.unityyamlmerge.driver = UnityYAMLMerge.exe merge -p "$BASE" "$REMOTE" "$LOCAL" "$MERGED"
  ```

git-lfs 3.7.1 is installed and its filters are active in this repo.
