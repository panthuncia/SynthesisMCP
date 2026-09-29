using System.Security.Cryptography;

namespace SafePatch.Host;

/// <summary>A package whose program matches its manifest, with the policy narrowed to what it requested.</summary>
public sealed record VerifiedPackage(Manifest Manifest, byte[] Program, PatchPolicy Policy);

public static class Preflight
{
    public static VerifiedPackage Verify(string manifestJson, byte[] program, PatchPolicy publisherPolicy)
    {
        var manifest = Manifest.Parse(manifestJson);
        if (program.Length == 0 || program.Length > publisherPolicy.MaxProgramBytes)
            throw new SafePatchException($"Program size {program.Length} is out of range.");

        var actual = Convert.ToHexStringLower(SHA256.HashData(program));
        if (actual != manifest.ProgramSha256)
            throw new SafePatchException("Program hash does not match the manifest.");

        return new VerifiedPackage(manifest, program, publisherPolicy.NarrowTo(manifest));
    }
}
