namespace PhotoSweep.Core.Hashing;

/// <summary>The two 64-bit perceptual hashes of one image. Compare fingerprints with <see cref="Hamming.Distance"/>.</summary>
public readonly record struct ImageFingerprint(ulong PHash, ulong DHash);
