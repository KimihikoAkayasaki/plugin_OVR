// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amethyst.Plugins.Contract;
using plugin_OVR.Utils;
using Valve.VR;

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace plugin_OVR;

[Export(typeof(ITrackingDevice))]
[ExportMetadata("Name", "SteamVR")]
[ExportMetadata("Guid", "K2VRTEAM-AME2-APII-TDPR-TPROVDOPENVR")]
[ExportMetadata("Publisher", "公彦赤屋先")]
[ExportMetadata("Version", "1.0.0.3")]
[ExportMetadata("Website", "https://github.com/KimihikoAkayasaki/plugin_OVR")]
public class SteamVR : ITrackingDevice
{
    private ulong _vrOverlayHandle = OpenVR.k_ulOverlayHandleInvalid;
    public static bool Initialized { get; private set; }
    private static object InitLock { get; } = new();
    private bool PluginLoaded { get; set; }

    private Vector3 VrPlayspaceTranslation =>
        OpenVR.System.GetRawZeroPoseToStandingAbsoluteTrackingPose().GetPosition();

    private Quaternion VrPlayspaceOrientationQuaternion =>
        OpenVR.System.GetRawZeroPoseToStandingAbsoluteTrackingPose().GetOrientation();

    [Import(typeof(IAmethystHost))] private IAmethystHost Host { get; set; }

    private List<int> TrackedVrIndexes { get; set; } = new();

    public bool IsSettingsDaemonSupported => false;
    public object SettingsInterfaceRoot => null;
    public int DeviceStatus { get; private set; } = 1;

    [DefaultValue("Not Defined\nE_NOT_DEFINED\nStatus message not defined!")]
    public string DeviceStatusString => PluginLoaded
        ? DeviceStatus switch
        {
            0 => Host.RequestLocalizedString("/ServerStatuses/Success")
                .Replace("{0}", DeviceStatus.ToString()),

            1 when VrHelper.IsOpenVrElevated() =>
                Host.RequestLocalizedString("/ServerStatuses/OpenVRElevatedError")
                    .Replace("{0}", "0x80070005"),

            1 when VrHelper.IsCurrentProcessElevated() =>
                Host.RequestLocalizedString("/ServerStatuses/AmethystElevatedError")
                    .Replace("{0}", "0x80080017"),

            1 => Host.RequestLocalizedString("/ServerStatuses/OpenVRError")
                .Replace("{0}", DeviceStatus.ToString()),

            _ => Host.RequestLocalizedString("/ServerStatuses/WTF")
        }
        : $"Undefined: {DeviceStatus}\nE_UNDEFINED\nSomething weird has happened, though we can't tell what.";

    public Uri ErrorDocsUri => new(DeviceStatus switch
    {
        -10 => $"https://docs.k2vr.tech/{Host?.DocsLanguageCode ?? "en"}/app/steamvr-driver-codes/#2",
        -1 => $"https://docs.k2vr.tech/{Host?.DocsLanguageCode ?? "en"}/app/steamvr-driver-codes/#3",
        _ => $"https://docs.k2vr.tech/{Host?.DocsLanguageCode ?? "en"}/app/steamvr-driver-codes/#6"
    });

    public void OnLoad()
    {
        PluginLoaded = true;
    }

    public void Initialize()
    {
        // Check if Amethyst is running as admin
        // Check if OpenVR is running as admin
        // Initialize OpenVR if we're ready to go

        DeviceStatus = VrHelper.IsCurrentProcessElevated() !=
            VrHelper.IsOpenVrElevated() || !OpenVrStartup()
                ? 1 // Either running as admin or failed to start
                : 0; // Connected to OpenVR successfully

        RefreshObjectList();
    }

    public void Shutdown()
    {
        lock (InitLock)
        lock (Host.UpdateThreadLock)
        {
            OpenVR.Shutdown(); // Shutdown OpenVR
            Initialized = false; // vrClient dll unloaded

            DeviceStatus = 1; // Update VR status
            Host?.RefreshStatusInterface(); // Reload
        }
    }

    public void Update()
    {
        if (!Initialized || OpenVR.System is null) return; // Sanity check

        try
        {
            lock (InitLock)
            {
                // Update vr events
                ParseVrEvents();

                // Stub check
                IsSkeletonTracked = false;

                // Get all poses
                var devicePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
                OpenVR.System.GetDeviceToAbsoluteTrackingPose(
                    ETrackingUniverseOrigin.TrackingUniverseStanding, 0, devicePoses);

                // Update all poses
                var enumerator = TrackedJoints.GetEnumerator();
                foreach (var vrObjectPose in TrackedVrIndexes
                             .Where(vrObject => enumerator.MoveNext() && enumerator.Current is not null)
                             .Select(vrObject => devicePoses.ElementAtOrDefault(vrObject)))
                {
                    // Note we're all fine
                    IsSkeletonTracked = true;

                    // Copy pose data from the object
                    enumerator.Current.Position = vrObjectPose.mDeviceToAbsoluteTracking.GetPosition();
                    enumerator.Current.Orientation = vrObjectPose.mDeviceToAbsoluteTracking.GetOrientation();

                    // Copy physics data from the object
                    enumerator.Current.Velocity = vrObjectPose.vVelocity.GetVector();
                    enumerator.Current.AngularVelocity = vrObjectPose.vAngularVelocity.GetVector();

                    // Parse/copy the tracking state
                    enumerator.Current.TrackingState = TrackedJointState.StateTracked;
                }
            }
        }
        catch (Exception e)
        {
            Host?.Log("Exception processing a heart beat call! " +
                      $"Message: {e.Message}", LogSeverity.Error);
        }
    }

    public void SignalJoint(int jointId)
    {
        if (!Initialized || OpenVR.System is null) return; // Sanity check

        try
        {
            // Trigger a haptic pulse on the device Amethyst requested it to be, unwrap to OpenVR ID
            OpenVR.System.TriggerHapticPulse((uint)TrackedVrIndexes.ElementAt(jointId), 0, 200);
        }
        catch (Exception e)
        {
            Host?.Log("Exception processing a heart beat call! " +
                      $"Message: {e.Message}", LogSeverity.Error);
        }
    }

    public ObservableCollection<TrackedJoint> TrackedJoints { get; } = new();
    public bool IsInitialized => Initialized;
    public bool IsSkeletonTracked { get; set; } = false;
    public bool IsPositionFilterBlockingEnabled => true;
    public bool IsPhysicsOverrideEnabled => true;
    public bool IsSelfUpdateEnabled => false;
    public bool IsFlipSupported => false;
    public bool IsAppOrientationSupported => false;

    private List<(TrackedJoint Joint, int Index)> GetTrackedVrObjects()
    {
        if (!Initialized || OpenVR.System is null)
            return new List<(TrackedJoint Joint, int Index)>(); // Sanity check

        string GetObjectSerial(int i)
        {
            StringBuilder serialStringBuilder = new(1024);
            var serialError = ETrackedPropertyError.TrackedProp_Success;
            OpenVR.System.GetStringTrackedDeviceProperty((uint)i,
                ETrackedDeviceProperty.Prop_SerialNumber_String,
                serialStringBuilder, (uint)serialStringBuilder.Capacity, ref serialError);

            return serialStringBuilder.ToString();
        }

        var devicePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(
            ETrackingUniverseOrigin.TrackingUniverseStanding, 0, devicePoses);

        var devicePosesList = devicePoses.ToList();
        return devicePosesList
            .Select(x => (GetObjectSerial(devicePosesList.IndexOf(x)), x, devicePosesList.IndexOf(x)))
            .Where(x => OpenVR.System.GetTrackedDeviceClass((uint)x.Item3) is not ETrackedDeviceClass.Max
                and not ETrackedDeviceClass.Invalid and not ETrackedDeviceClass.TrackingReference)
            .Where(x => !x.Item1.StartsWith("AME-"))
            .Select(x => (new TrackedJoint
            {
                Name = x.Item1,
                Role = TrackedJointType.JointManual,
                TrackingState = TrackedJointState.StateTracked,

                Position = Vector3.Transform(
                    x.x.mDeviceToAbsoluteTracking.GetPosition() - VrPlayspaceTranslation,
                    Quaternion.Inverse(VrPlayspaceOrientationQuaternion)),

                Orientation = Quaternion.Inverse(VrPlayspaceOrientationQuaternion) *
                              x.x.mDeviceToAbsoluteTracking.GetOrientation(),

                Velocity = x.x.vVelocity.GetVector(),
                AngularVelocity = x.x.vAngularVelocity.GetVector()
            }, x.Item3)).ToList();
    }


    private void RefreshObjectList()
    {
        // Try polling controllers and starting their streams
        try
        {
            Host?.Log("Locking the update thread...");
            lock (Host!.UpdateThreadLock)
            {
                Host?.Log("Emptying the tracked joints list...");
                TrackedJoints.Clear(); // Delete literally everything

                Host?.Log("Searching for tracked objects...");
                var trackedObjects = GetTrackedVrObjects();

                TrackedVrIndexes = trackedObjects.Select(x => x.Index).ToList();
                TrackedJoints.AddRange(trackedObjects.Select(x => x.Joint));
            }

            // Refresh everything after the change
            Host?.Log("Refreshing the UI...");
            Host?.RefreshStatusInterface();
        }
        catch (Exception e)
        {
            Host?.Log($"Couldn't connect to the PSM Service! {e.Message}");
        }
    }

    #region OpenVR Interfacing Methods

    private bool OpenVrStartup()
    {
        // Only re-init VR if needed
        if (OpenVR.System is null)
        {
            Host.Log("Attempting connection to VRSystem... ");

            try
            {
                Host.Log("Creating a cancellation token...");
                using var cancellationTokenSource = new CancellationTokenSource();
                cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(7));

                Host.Log("Waiting for the VR System to initialize...");
                var eError = EVRInitError.None;

                OpenVR.Init(ref eError, EVRApplicationType.VRApplication_Overlay);
                Initialized = true; // vrClient dll loaded

                Host.Log("The VRSystem finished initializing...");
                if (eError != EVRInitError.None)
                {
                    Host.Log($"IVRSystem could not be initialized: EVRInitError Code {eError}", LogSeverity.Error);
                    return false; // Catastrophic failure!
                }
            }
            catch (Exception e)
            {
                Host.Log($"The VR System failed to initialize ({e.Message}), giving up!", LogSeverity.Error);
                return false; // Took too long to initialize, abort!
            }
        }

        // Re-check
        if (OpenVR.System is null) return false;

        // We're good to go!
        Host.Log("Looks like the VR System is ready to go!");

        // Initialize the overlay
        OpenVR.Overlay?.DestroyOverlay(_vrOverlayHandle); // Destroy the overlay in case it somehow exists
        OpenVR.Overlay?.CreateOverlay("amethyst.plugins.ovr", "plugin_OVR", ref _vrOverlayHandle);

        Host.Log($"VR Playspace translation: \n{VrPlayspaceTranslation}");
        Host.Log($"VR Playspace orientation: \n{VrPlayspaceOrientationQuaternion}");

        Initialized = true; // vrClient dll loaded
        return true; // OK
    }

    private void ParseVrEvents()
    {
        // Poll and parse all needed VR (overlay) events
        if (!Initialized || OpenVR.System is null || OpenVR.Overlay is null) return;

        var vrEvent = new VREvent_t();
        while (OpenVR.Overlay.PollNextOverlayEvent(_vrOverlayHandle, ref vrEvent, (uint)Marshal.SizeOf<VREvent_t>()))
            switch (vrEvent.eventType)
            {
                case (uint)EVREventType.VREvent_Quit:
                    Host.Log("VREvent_Quit has been called, unloading vrclient not to be killed...");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500); // Wait for Amethyst to register this event
                        Shutdown(); // Unload vrclient.dll and change the status to error
                    });
                    break;

                case (uint)EVREventType.VREvent_TrackedDeviceActivated:
                case (uint)EVREventType.VREvent_TrackedDeviceDeactivated:
                    Host.Log($"{(EVREventType)vrEvent.eventType} has been called, reloading vr objects!");
                    RefreshObjectList(); // Refresh all objects and put them in our internal list
                    break;
            }
    }

    #endregion
}

public static class OvrExtensions
{
    public static Vector3 GetVector(this HmdVector3_t v)
    {
        return new Vector3(v.v0, v.v1, v.v2);
    }

    public static Vector3 GetPosition(this HmdMatrix34_t mat)
    {
        return new Vector3(mat.m3, mat.m7, mat.m11);
    }

    private static bool IsOrientationValid(this HmdMatrix34_t mat)
    {
        return (mat.m2 != 0 || mat.m6 != 0 || mat.m10 != 0) &&
               (mat.m1 != 0 || mat.m5 != 0 || mat.m9 != 0);
    }

    public static Quaternion GetOrientation(this HmdMatrix34_t mat)
    {
        if (!mat.IsOrientationValid()) return Quaternion.Identity;

        var q = new Quaternion
        {
            W = MathF.Sqrt(MathF.Max(0, 1 + mat.m0 + mat.m5 + mat.m10)) / 2,
            X = MathF.Sqrt(MathF.Max(0, 1 + mat.m0 - mat.m5 - mat.m10)) / 2,
            Y = MathF.Sqrt(MathF.Max(0, 1 - mat.m0 + mat.m5 - mat.m10)) / 2,
            Z = MathF.Sqrt(MathF.Max(0, 1 - mat.m0 - mat.m5 + mat.m10)) / 2
        };

        q.X = MathF.CopySign(q.X, mat.m9 - mat.m6);
        q.Y = MathF.CopySign(q.Y, mat.m2 - mat.m8);
        q.Z = MathF.CopySign(q.Z, mat.m4 - mat.m1);
        return q; // Extracted, fixed ovr quaternion!
    }

    public static void AddRange<T>(this ObservableCollection<T> collection, IEnumerable<T> items)
    {
        items.ToList().ForEach(collection.Add);
    }
}