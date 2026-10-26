using System;
using UnityEngine;

[Serializable]
public sealed class CardReference
{
    // JSON field cardId is retained for compatibility; it identifies any registered fixed target.
    public string cardId;
    public int version;
    public float widthMeters, heightMeters;
    public RelativePoint relativePosition;
    [Serializable] public sealed class RelativePoint { public float x, y, z; }
    public bool IsSupported => !string.IsNullOrEmpty(cardId) && version > 0 && widthMeters >= 0.05f && widthMeters <= 5 &&
        heightMeters >= 0.05f && heightMeters <= 5 && relativePosition != null &&
        Finite(relativePosition.x) && Finite(relativePosition.y) && Finite(relativePosition.z);
    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) <= 100;
    public Vector3 Point => new Vector3(relativePosition.x, relativePosition.y, relativePosition.z);
}

public static class ReferenceFrame
{
    public static Vector3 ToRelative(Pose card, Vector3 world) => Quaternion.Inverse(card.rotation) * (world - card.position);
    public static Vector3 ToWorld(Pose card, Vector3 relative) => card.position + card.rotation * relative;
    public static CardReference Capture(ReferenceTarget target, Pose card, Vector3 world)
    {
        var p = ToRelative(card, world);
        return new CardReference { cardId = target.id, version = target.version, widthMeters = target.widthMeters, heightMeters = target.heightMeters,
            relativePosition = new CardReference.RelativePoint { x = p.x, y = p.y, z = p.z } };
    }
}
