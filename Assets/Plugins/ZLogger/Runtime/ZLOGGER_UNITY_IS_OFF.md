# ZLogger.Unity is compiled nowhere, on purpose

`ZLogger.Unity.asmdef` carries a define constraint on `POFOOTBALL_ZLOGGER_ENABLED`,
which is defined for no platform in this project. The assembly therefore does not
compile in the Editor and does not compile into a player.

## Why

It could not build for a player even if something wanted it to.

The asmdef ships with `excludePlatforms: ["Editor"]` — it is a *player-only*
assembly — and its `precompiledReferences` are seven DLLs under
`Assets/Plugins/NuGet`, every one of which `Editor_NuGetPluginGuard` pins to
**Editor-only** because they are the MCP bridge's dependency closure and have no
business in a retail build. A player-only assembly whose every dependency is
Editor-only is a contradiction, and it resolved the way contradictions do: the
first Android build this project ever ran died with

    ZLoggerUnityDebugLoggerProvider.cs(146,6): error CS0246: The type or namespace
    name 'ProviderAliasAttribute' could not be found

It had never been noticed because the Editor never compiles this assembly and
nobody had produced an Android player before 2026-09-09.

## Why it was not simply deleted

Nothing in this repository references `ZLogger.Unity` — not a script under
`Assets/Scripts`, not an asmdef, not the MCP package that dragged it in. It is
dead weight. But it arrived as part of a vendored plugin drop rather than as a
decision, and deleting a vendor's files makes the next drop of that vendor a
three-way merge. One define constraint is reversible, greppable, and leaves the
files exactly as the vendor shipped them.

To bring it back: define `POFOOTBALL_ZLOGGER_ENABLED` for the platforms that need
it, and un-pin its seven NuGet dependencies for those same platforms — which means
accepting that they ship.
