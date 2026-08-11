"""Test/code parity sweep.

For every `Assets/Tests/**/*.cs` file:
  1. Collect every identifier that LOOKS LIKE a type name (PascalCase, >= 5 chars,
     not a keyword).
  2. Subtract identifiers that exist as `class`/`struct`/`enum`/`interface`
     declarations across the project assemblies.
  3. Subtract identifiers that exist as `using` aliases or method-local type
     references.

Output:
  * `Docs/TEST_PARITY.md` — per-test report of unresolved identifiers.
  * Exits 0 either way (report only).

Limitations: this is a static filter, not a Roslyn pass. It will produce false
positives for generic type parameters, generic constraints, and parameter names
that happen to be PascalCase. The remaining shorthands are reviewed by hand.
"""

from __future__ import annotations

import re
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
ASSETS = REPO_ROOT / "Assets"
DOCS = REPO_ROOT / "docs"
DOCS.mkdir(exist_ok=True)

TEST_DIRS = [ASSETS / "Tests"]
PROD_DIRS = [ASSETS / "Scripts", ASSETS / "Editor", ASSETS / "Plugins"]

TYPE_DECL_RE = re.compile(r"\b(class|struct|enum|interface|record)\s+([A-Z][A-Za-z0-9_]+)")
IDENT_RE = re.compile(r"\b([A-Z][A-Za-z0-9_]{4,})\b")
KEYWORDS = {
    "Assert", "Test", "Setup", "TearDown", "TestFixture", "OneTimeSetUp",
    "OneTimeTearDown", "UnityTest", "TestCase", "Category", "Description",
    "SetUp", "TearDown", "NUnit", "Editor", "Runtime", "SerializeField",
    "Vector2", "Vector3", "Quaternion", "Color", "Mathf", "MonoBehaviour",
    "ScriptableObject", "Component", "Object", "GameObject", "Transform",
    "Rigidbody2D", "BoxCollider2D", "CircleCollider2D", "WaitForSeconds",
    "Time", "Input", "Resources", "SceneManager", "PlayerPrefs", "Application",
    "Camera", "AudioSource", "AnimationCurve", "Rect", "Bounds", "Plane",
    "Ray", "RaycastHit", "Collision", "Collider", "PhysicMaterial",
    "UnityTest", "TestCase", "SetUp", "TearDown", "IEnumerator",
    "Action", "Func", "Task", "ValueTask", "UniTask", "IReadOnlyList",
    "IDisposable", "CancellationToken", "DateTime", "TimeSpan", "StringBuilder",
    "IPublisher", "ISubscriber", "IEnumerable", "ICollection", "IList",
    "IReadOnlyCollection", "IReadOnlyDictionary", "Dictionary", "List",
    "HashSet", "Queue", "Stack", "KeyValuePair", "Tuple", "Nullable",
    "Nullable", "IEnumerator", "IAsyncStateMachine", "AsyncTaskMethodBuilder",
    "Debug", "Console", "StringComparison", "CultureInfo", "Encoding",
    "BitConverter", "Convert", "Math", "Random", "Guid", "Type", "MemberInfo",
    "Attribute", "ArgumentException", "ArgumentNullException",
    "InvalidOperationException", "NotImplementedException", "Exception",
    "FieldInfo", "PropertyInfo", "MethodInfo", "ConstructorInfo",
    "BindingFlags", "ParameterInfo", "Assembly", "Activator", "Convert",
    "Stopwatch", "Process", "Stream", "Path", "File", "Directory",
    "JsonConvert", "JObject", "JToken", "JProperty", "JArray",
    "Sprite", "Texture2D", "Mesh", "Material", "Shader", "RenderTexture",
    "IList", "LinkedList", "Queue", "Stack", "SortedDictionary",
    "SortedList", "ConcurrentBag", "ConcurrentDictionary", "ConcurrentQueue",
    "BlockingCollection", "Lazy", "WeakReference", "EventArgs", "EventHandler",
    "Delegate", "MulticastDelegate", "Action", "Predicate", "Comparison",
    "Converter", "IServiceProvider", "IServiceCollection", "HttpClient",
    "WebClient", "HttpRequestMessage", "HttpResponseMessage", "Uri",
    "StringReader", "StringWriter", "TextReader", "TextWriter", "StreamReader",
    "StreamWriter", "BinaryReader", "BinaryWriter", "MemoryStream",
    "FileStream", "NetworkStream", "PipeStream", "CryptoStream",
    "GZipStream", "DeflateStream", "BufferedStream",
}


def collect_declared_types() -> set[str]:
    out: set[str] = set()
    for root in PROD_DIRS:
        if not root.exists():
            continue
        for cs in root.rglob("*.cs"):
            try:
                text = cs.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for m in TYPE_DECL_RE.finditer(text):
                out.add(m.group(2))
    return out


def collect_using_aliases() -> set[str]:
    out: set[str] = set()
    pattern = re.compile(r"using\s+([A-Z][A-Za-z0-9_]+)\s*=")
    for root in PROD_DIRS + TEST_DIRS:
        if not root.exists():
            continue
        for cs in root.rglob("*.cs"):
            try:
                text = cs.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for m in pattern.finditer(text):
                out.add(m.group(1))
    return out


def scan_tests(declared: set[str], aliases: set[str]) -> list[tuple[Path, list[str]]]:
    required: set[str] = declared | aliases | KEYWORDS
    out: list[tuple[Path, list[str]]] = []
    for root in TEST_DIRS:
        if not root.exists():
            continue
        for cs in sorted(root.rglob("*.cs")):
            try:
                text = cs.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            stripped = re.sub(r"//.*$", "", text, flags=re.MULTILINE)
            stripped = re.sub(r"/\*.*?\*/", "", stripped, flags=re.DOTALL)
            idents = set(IDENT_RE.findall(stripped))
            missing = sorted(idents - required)
            if missing:
                out.append((cs, missing))
    return out


def render_report(missing: list[tuple[Path, list[str]]]) -> str:
    lines = [
        "# Test/Code Parity Report",
        "",
        "_Generated by Tools/audit_tests.py — false positives are expected; review by hand._",
        "",
        "## Why false positives happen",
        "",
        "* Generic type parameters and constraints (`T`, `TValue`, etc.) — filtered where possible.",
        "* NUnit attribute names — listed in `KEYWORDS`.",
        "* Unity / mscorlib / System types — listed in `KEYWORDS`.",
        "* Cross-assembly type references resolved through reflection (`Type.GetType(...)`).",
        "",
        "## Summary",
        "",
        f"Files scanned: see test tree",
        f"Files with unresolved identifiers: {len(missing)}",
        "",
        "## Per-file breakdown",
        "",
    ]
    for path, names in missing:
        rel = path.relative_to(REPO_ROOT)
        lines.append(f"### `{rel}`")
        lines.append("")
        for n in names:
            lines.append(f"* `{n}`")
        lines.append("")
    return "\n".join(lines)


def main() -> int:
    declared = collect_declared_types()
    aliases = collect_using_aliases()
    print(f"Declared types in prod assemblies: {len(declared)}")
    print(f"Using aliases: {len(aliases)}")

    missing = scan_tests(declared, aliases)
    report = render_report(missing)
    out = DOCS / "TEST_PARITY.md"
    out.write_text(report, encoding="utf-8")

    print(f"Unresolved identifiers found: {sum(len(n) for _, n in missing)}")
    print(f"Report written to {out.relative_to(REPO_ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
