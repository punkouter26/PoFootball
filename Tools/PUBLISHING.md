# Publishing PoFootball

One command builds the signed AAB and uploads it to Play **internal testing** as a **draft**:

    .\Tools\publish.ps1

- `-DryRun` rehearses everything and lands nothing in the console.
- `-SkipBuild` uploads the existing `Builds\Android\PoFootball.aab`.
- Close this project in the Unity editor first, or the headless build fails on the lock.
- bundleVersionCode is bumped by the build itself (`Editor_BuildAndroidAAB.NextVersionCode`),
  once per build. Play rejects duplicate versionCodes.
- The uploader is `Tools\play_publish.py`, run from its own venv at `Tools\publish-venv`
  (never `.venv`, which carries the ml-agents/torch pins):

      py -3 -m venv Tools\publish-venv
      Tools\publish-venv\Scripts\python.exe -m pip install -r Tools\requirements-publish.txt

- Secrets live in the shared vault, never in this repository:
  `C:\Users\punko\OneDrive\VAULT\_CODE\pofootball-upload.jks` / `.pass` (signing) and
  `pofootball-play-service-account.json` (upload). Override the key path with
  `POFOOTBALL_PLAY_CREDENTIALS`. Neither file exists yet; see CLAUDE.md, "Android release".
- Rolling a draft out to testers, content rating and data safety are manual Play Console steps.
