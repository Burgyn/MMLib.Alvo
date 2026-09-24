using System.Runtime.CompilerServices;

// The agent's internals are its tool set, its chat client and its prompt — the three things the tests
// measure and nothing a host is meant to reach. The grant names one assembly, and the caveat every other
// grant in this family carries applies here too: these assemblies are unsigned, so InternalsVisibleTo stops
// nothing a determined caller could not do anyway. It states intent, which is what keeps the public surface
// honest.
[assembly: InternalsVisibleTo("MMLib.Alvo.Ai.Tests")]

// The standalone host's suite, for the same one path the dashboard grants it: the real assistant over the
// real management surface, with only the model scripted. The constructor that takes a chat-client delegate is
// what it needs, and publishing that would hand every host a way to swap the client this package owns.
[assembly: InternalsVisibleTo("MMLib.Alvo.Host.Tests")]
