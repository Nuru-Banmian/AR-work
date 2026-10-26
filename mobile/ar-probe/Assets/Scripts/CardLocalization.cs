using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

// A tracked image pose is usable only in the current camera session while Tracking.
public sealed class CardLocalization : MonoBehaviour
{
    private ARTrackedImageManager images;
    private bool ready;
    private Unity.Jobs.JobHandle pendingJob;
    private bool jobPending;
    private ReferenceTarget target;
    private Texture2D texture;
    private readonly HashSet<TrackableId> observed = new HashSet<TrackableId>();
    public string Message { get; private set; } = "请选择并扫描已登记的固定图案。";
    public void Initialize(ARTrackedImageManager manager)
    {
        images = manager;
        images.trackedImagesChanged += changes => {
            if (!ready) return;
            foreach (var image in changes.added) observed.Add(image.trackableId);
            foreach (var image in changes.updated) observed.Add(image.trackableId);
            foreach (var image in changes.removed) observed.Remove(image.trackableId);
        };
    }
    public void Clear() { ready = false; observed.Clear(); if (jobPending) { pendingJob.Complete(); jobPending = false; } }
    public IEnumerator Prepare(ReferenceTarget target)
    {
        Clear();
        this.target = target;
        Message = "正在加载离线参照图案…";
        AddReferenceImageJobState job;
        try
        {
            images.enabled = false;
            var library = images.CreateRuntimeLibrary() as MutableRuntimeReferenceImageLibrary;
            if (library == null) throw new NotSupportedException("Mutable image library unavailable");
            if (texture != null) Destroy(texture);
            texture = target.DecodeImage();
            if (texture == null) throw new InvalidOperationException("Missing card texture");
            job = library.ScheduleAddImageWithValidationJob(texture, target.ImageName, target.widthMeters);
            pendingJob = job.jobHandle; jobPending = true;
            images.referenceLibrary = library;
        }
        catch (Exception error)
        {
            Message = "此设备无法加载参照图案，不能采集或恢复；可返回查记录。";
            Debug.LogException(error); yield break;
        }
        while (!job.jobHandle.IsCompleted) yield return null;
        job.jobHandle.Complete(); jobPending = false;
        if (job.status != AddReferenceImageJobStatus.Success)
        { Message = "参照图案图案验证失败，不能采集或恢复。"; yield break; }
        images.enabled = true;
        ready = true;
        Message = "请扫描：" + target.Caption + "。找不到或误扫其他图案时无法定位。";
    }
    private void OnDestroy() { Clear(); if (texture != null) Destroy(texture); }
    public IEnumerator ValidateImage(Texture2D image, float width, Action<bool> completed)
    {
        AddReferenceImageJobState validation;
        try
        {
            var library = images.CreateRuntimeLibrary() as MutableRuntimeReferenceImageLibrary;
            if (library == null) { completed(false); yield break; }
            validation = library.ScheduleAddImageWithValidationJob(image, "enrollment-check", width);
            pendingJob = validation.jobHandle; jobPending = true;
        }
        catch (Exception error) { Debug.LogException(error); completed(false); yield break; }
        while (!pendingJob.IsCompleted) yield return null;
        pendingJob.Complete(); jobPending = false;
        completed(validation.status == AddReferenceImageJobStatus.Success);
    }
    public bool TryPose(out Pose pose)
    {
        pose = default;
        if (!ready || !images.isActiveAndEnabled || ARSession.state != ARSessionState.SessionTracking) return false;
        foreach (var image in images.trackables)
        {
            if (!observed.Contains(image.trackableId) || image.referenceImage.name != target.ImageName || image.trackingState != TrackingState.Tracking) continue;
            pose = new Pose(image.transform.position, image.transform.rotation);
            return true;
        }
        return false;
    }
}
