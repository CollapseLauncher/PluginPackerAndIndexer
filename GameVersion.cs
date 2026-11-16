using System.Runtime.InteropServices;
// ReSharper disable UnusedMember.Global
// ReSharper disable InvocationIsCultureSensitive
// ReSharper disable StructCanBeMadeReadOnly
#pragma warning disable IDE0290

namespace Indexer;

/// <summary>
/// Represents a version of API, Game Installation or Assets.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public readonly struct GameVersion
{
    public GameVersion(int major, int minor, int build, int revision)
    {
        Major    = major;
        Minor    = minor;
        Build    = build;
        Revision = revision;
    }

    public override string ToString() => $"{Major}.{Minor}.{Build}.{Revision}";

    public readonly int Major;
    public readonly int Minor;
    public readonly int Build;
    public readonly int Revision;
}
