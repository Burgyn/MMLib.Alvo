using System.Runtime.CompilerServices;

// The graders' own suite (test/MMLib.Alvo.Ai.Eval.Tests): the case graders, the diff, the suite bar and the recorder
// are pure, so they are proven in ring0 over hand-built turns rather than first exercised by a paid run.
[assembly: InternalsVisibleTo("MMLib.Alvo.Ai.Eval.Tests")]
