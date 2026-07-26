// Exposes internal types (currently RunningRecording inside RecordingService) to
// the unit-test project so equality / structural assertions can target the record
// directly. Mirrors the grant set on BossCam.Service via the same assembly attribute.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BossCam.Tests")]
