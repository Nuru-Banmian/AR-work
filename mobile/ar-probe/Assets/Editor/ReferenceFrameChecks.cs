using System;
using UnityEditor;
using UnityEngine;

public static class ReferenceFrameChecks
{
    public static void Run()
    {
        // A 90 degree rotation about world Y maps local +X to world -Z.
        var card = new Pose(new Vector3(10, 2, -4), Quaternion.Euler(0, 90, 0));
        Check(ReferenceFrame.ToWorld(card, new Vector3(2, 0.5f, 3)), new Vector3(13, 2.5f, -6));
        Check(ReferenceFrame.ToRelative(card, new Vector3(13, 2.5f, -6)), new Vector3(2, 0.5f, 3));
        // The same recorded point in a different session must use that session's card pose.
        Check(ReferenceFrame.ToWorld(new Pose(new Vector3(-5, 1, 8), Quaternion.identity),
            new Vector3(2, 0.5f, 3)), new Vector3(-3, 1.5f, 11));
        Check(ReferenceFrame.ToWorld(new Pose(Vector3.zero, Quaternion.Euler(90, 0, 0)),
            new Vector3(0, 1, 2)), new Vector3(0, -2, 1));
        Debug.Log("ISSUE12_REFERENCE_FRAME_CHECKS_PASSED=4");
    }
    private static void Check(Vector3 actual, Vector3 expected)
    {
        if (Vector3.Distance(actual, expected) > 0.0001f)
            throw new Exception("Reference frame: expected " + expected + ", got " + actual);
    }
}
