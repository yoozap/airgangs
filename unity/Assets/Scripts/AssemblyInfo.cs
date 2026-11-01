using System.Runtime.CompilerServices;

// Probes live in Assets/Editor, which Unity compiles into Assembly-CSharp-Editor —
// a DIFFERENT assembly from Assembly-CSharp, where the game code lives. So an
// `internal` member is invisible to its own probe, which is how
// DamageProbe.cs failed on PlaneDamage.Feed with CS1061 ("does not contain a
// definition for 'Feed'") even though Feed is right there.
//
// The agents deliberately marked some members `internal` — exposed for probing but
// not public API — and that intent is worth keeping. Granting the editor assembly
// access is the one-line fix that preserves it, rather than widening a dozen
// members to public and losing the distinction.
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
