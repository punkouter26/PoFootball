using System.Runtime.CompilerServices;

// Lets PoFootball.Tests.EditMode reach the internal types in this assembly.
//
// WHY NOT JUST MAKE THEM PUBLIC. .claude/rules/csharp-unity.md is explicit that a
// type is internal unless another assembly consumes it, and that "might be useful
// later" is not a reason to widen anything. A test assembly is not a consumer in
// that sense — it is the same code, checked. Widening Agent_PlayCaller to public so
// a test could see it would put a scripted-heuristic detail into the runtime API
// surface of PoFootball.Agents forever, to buy something this one line buys without
// changing what any other assembly can call.
[assembly: InternalsVisibleTo("PoFootball.Tests.EditMode")]
