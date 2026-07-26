using System.Runtime.CompilerServices;

// Grant the unit-test project access to internal security helpers
// (BindAddressInspector, LanBoundTokenGate, etc.) so they can be exercised
// directly without being lifted to public API.
[assembly: InternalsVisibleTo("BossCam.Tests")]
[assembly: InternalsVisibleTo("BossCam.E2E")]
