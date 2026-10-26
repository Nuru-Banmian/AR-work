using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Android;
using System.Xml;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARCore;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

public static class ProbeBuild
{
    public static void Android()
    {
        Build(false);
    }

    public static void InspectionAndroid()
    {
        Build(true);
    }

    private static void Build(bool inspection)
    {
        const BuildTargetGroup group = BuildTargetGroup.Android;
        PlayerSettings.companyName = "AR Work";
        PlayerSettings.productName = inspection ? "园区巡检" : "设备检测";
        PlayerSettings.SetApplicationIdentifier(group, inspection ? "com.arwork.inspection" : "com.arwork.deviceprobe");
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        PlayerSettings.bundleVersion = "0.1.0";
        PlayerSettings.Android.bundleVersionCode = 1;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel35;
        PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.Android.useCustomKeystore = false;
        EditorUserBuildSettings.buildAppBundle = false;
        var playerSettings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
        playerSettings.FindProperty("useCustomGradleSettingsTemplate").boolValue = true;
        playerSettings.FindProperty("activeInputHandler").intValue = 0;
        playerSettings.ApplyModifiedPropertiesWithoutUndo();

        Directory.CreateDirectory("Assets/XR");
        AssetDatabase.Refresh();
        var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        if (general == null)
        {
            var perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, "Assets/XR/GeneralSettings.asset");
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            general = perTarget.SettingsForBuildTarget(group);
        }
        general.InitManagerOnStart = !inspection;
        if (!XRPackageMetadataStore.AssignLoader(general.Manager, "UnityEngine.XR.ARCore.ARCoreLoader", group))
            throw new Exception("Cannot assign ARCore loader");
        EditorUtility.SetDirty(general);
        var arcore = ARCoreSettings.GetOrCreateSettings();
        if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(arcore)))
            AssetDatabase.CreateAsset(arcore, "Assets/XR/ARCoreSettings.asset");
        arcore.requirement = ARCoreSettings.Requirement.Optional;
        arcore.depth = ARCoreSettings.Requirement.Optional;
        ARCoreSettings.currentSettings = arcore;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var probe = new GameObject("Device probe").AddComponent<ArProbe>();
        probe.inspectionMode = inspection;
        probe.markerMaterial = CreateMaterial("Assets/Marker.mat", "Unlit/Color", new Color(1f, 0.45f, 0.05f));
        probe.planeMaterial = CreateMaterial("Assets/Plane.mat", "Sprites/Default", new Color(0.1f, 0.8f, 0.35f, 0.25f));
        var scenePath = inspection ? "Assets/Inspection.unity" : "Assets/Probe.unity";
        EditorSceneManager.SaveScene(scene, scenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        AssetDatabase.SaveAssets();
        Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = inspection ? "Builds/AR-Inspection.apk" : "Builds/AR-Device-Probe.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        Debug.Log("AR_PROBE_BUILD_RESULT=" + report.summary.result);
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Android probe build failed: " + report.summary.result);
    }

    private static Material CreateMaterial(string path, string shaderName, Color color)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new Exception("Missing shader: " + shaderName);
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.color = color;
        EditorUtility.SetDirty(material);
        return material;
    }
}

// Apply to the generated Unity manifest without taking ownership of its activity template.
public sealed class InspectionPermissions : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 100;
    public void OnPostGenerateGradleAndroidProject(string path)
    {
        var file = Path.Combine(path, "src/main/AndroidManifest.xml");
        var xml = new XmlDocument(); xml.Load(file);
        var application = xml.SelectSingleNode("/manifest/application");
        const string android = "http://schemas.android.com/apk/res/android";
        var namespaces = new XmlNamespaceManager(xml.NameTable);
        namespaces.AddNamespace("android", android);
        var metadata = application.SelectSingleNode("meta-data[@android:name='unityplayer.SkipPermissionsDialog']", namespaces) as XmlElement;
        if (metadata == null)
        {
            metadata = xml.CreateElement("meta-data");
            application.AppendChild(metadata);
        }
        metadata.SetAttribute("name", android, "unityplayer.SkipPermissionsDialog");
        metadata.SetAttribute("value", android, "true");
        xml.Save(file);
    }
}
