namespace BossCam.E2E;

/// <summary>Serializes E2E tests so they do not thrash shared ports/cameras in parallel.</summary>
[CollectionDefinition("BossCamE2E", DisableParallelization = true)]
public sealed class BossCamE2ECollection;
