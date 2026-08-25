using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Module5.DI;
using UnityEngine.XR.ARFoundation;
using Unity.XR.CoreUtils;

#if (UNITY_ANDROID || UNITY_IOS)
using UnityEngine.XR.ARSubsystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering.Universal;
#endif

namespace Module5.Player.ARFoundation
{
    [Serializable]
    public class ARFoundationAnchorTrackerData
    {
        public ARSession arSession;
        public XROrigin xrOrigin;
        public ARTrackedImageManager imageManager;
    }

    public class ARFoundationAnchorTracker : ARAnchorTracker, ITickable
    {
        private readonly ARFoundationAnchorTrackerData _data;

#if (UNITY_ANDROID || UNITY_IOS)
        private Vector3 _sourceRootPos;
        private Vector3 _targetRootPos;
        private Quaternion _sourceRootRot;
        private Quaternion _targetRootRot;

        private ARSession _arSession => _data?.arSession;
        private XROrigin _xrOrigin => _data?.xrOrigin;
        private ARTrackedImageManager _imageManager => _data?.imageManager;
        private MutableRuntimeReferenceImageLibrary _runtimeLibrary;
        // Keep references to prevent GC while jobs are running
        private List<Texture2D> _activeSanitizedTextures = new List<Texture2D>();

        // Smoothing fields
        private float _transitionDuration = 1.0f;
        private float _transitionTimer = 0f;
        private bool _hasInitialPose = false;
        private bool _isTransitioning = false;
#endif

        public ARFoundationAnchorTracker(IAnchorManager anchorManager, ISceneManager sceneManager, ARFoundationAnchorTrackerData data) : base(anchorManager, sceneManager)
        {
            _data = data;
#if (UNITY_ANDROID || UNITY_IOS)
            _anchorManager.OnAnchorsLoaded += UpdateImageLibrary;
#endif
        }

        public override void StartTracking()
        {
#if (UNITY_ANDROID || UNITY_IOS)
            Debug.Log("[ARAnchorTracker] Starting tracking...");
            InitializeARComponents();

            if (_arSession != null) _arSession.gameObject.SetActive(true);
            if (_xrOrigin != null) _xrOrigin.gameObject.SetActive(true);

            if (_imageManager != null)
            {
                _imageManager.trackablesChanged.AddListener(OnTrackablesChanged);
                UpdateImageLibrary();
            }

            if (_xrOrigin != null && _xrOrigin.Camera != null)
            {
                Debug.Log($"[ARAnchorTracker] AR Camera Clip Planes: Near={_xrOrigin.Camera.nearClipPlane}, Far={_xrOrigin.Camera.farClipPlane}");
            }
#else
            Debug.Log("[ARAnchorTracker] Not on mobile platform, skipping AR initialization.");
#endif
            base.StartTracking();
        }

        public override void StopTracking()
        {
#if (UNITY_ANDROID || UNITY_IOS)
            if (_imageManager != null)
            {
                _imageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
            }

            if (_arSession != null) _arSession.gameObject.SetActive(false);
            if (_xrOrigin != null) _xrOrigin.gameObject.SetActive(false);
#endif
            base.StopTracking();
        }

        public  override void Tick()
        {
#if (UNITY_ANDROID || UNITY_IOS)
            if (!_isTransitioning || _sceneManager.Root == null) return;

            _transitionTimer += Time.deltaTime;
            float t = Mathf.Clamp01(_transitionTimer / _transitionDuration);

            // Simple Ease-Out-Cubic for smoother feel
            float easedT = 1f - Mathf.Pow(1f - t, 3f);

            _sceneManager.Root.position = Vector3.Lerp(_sourceRootPos, _targetRootPos, easedT);
            _sceneManager.Root.rotation = Quaternion.Slerp(_sourceRootRot, _targetRootRot, easedT);

            if (t >= 1.0f)
            {
                _isTransitioning = false;
            }
#endif
        }

#if (UNITY_ANDROID || UNITY_IOS)
        private void InitializeARComponents()
        {
            if (_data.arSession == null)
            {
                GameObject sessionObj = new GameObject("AR Session");
                _data.arSession = sessionObj.AddComponent<ARSession>();
                sessionObj.AddComponent<ARInputManager>();
            }

            Camera arCam = null;

            if (_data.xrOrigin == null)
            {
                GameObject originObj = new GameObject("XR Origin");
                _data.xrOrigin = originObj.AddComponent<XROrigin>();
                
                GameObject cameraOffset = new GameObject("Camera Offset");
                cameraOffset.transform.SetParent(originObj.transform);
                _data.xrOrigin.CameraFloorOffsetObject = cameraOffset;

                // Create a dedicated AR Camera for background rendering
                GameObject arCamObj = new GameObject("AR Camera");
                arCamObj.transform.SetParent(cameraOffset.transform);
                arCam = arCamObj.AddComponent<Camera>();
                arCam.clearFlags = CameraClearFlags.Color;
                arCam.backgroundColor = Color.black;
                arCam.cullingMask = 0; // Don't render any layers, just background
                arCam.depth = -1;      // Render background FIRST

                _data.xrOrigin.Camera = arCam;
                arCamObj.AddComponent<ARCameraManager>();
                arCamObj.AddComponent<ARCameraBackground>();
                
                // Add TrackedPoseDriver or ARPoseDriver to drive the camera from device movement
                var poseDriver = arCamObj.AddComponent<TrackedPoseDriver>();
                
                poseDriver.positionAction = new UnityEngine.InputSystem.InputAction("Position", binding: "<XRHMD>/centerEyePosition");
                poseDriver.positionAction = new InputAction("Position", binding: "<HandheldARInputDevice>/devicePosition");
                poseDriver.rotationAction = new InputAction("Rotation", binding: "<HandheldARInputDevice>/deviceRotation");
                
                poseDriver.positionAction.Enable();
                poseDriver.rotationAction.Enable();

                Debug.Log($"[ARAnchorTracker] TrackedPoseDriver initialized with HandheldARInputDevice bindings.");
            }
            else
            {
                arCam = _data.xrOrigin.Camera;
                if (arCam == null)
                {
                    var bg = _data.xrOrigin.GetComponentInChildren<ARCameraBackground>();
                    if (bg != null) arCam = bg.GetComponent<Camera>();
                }
            }

            // Setup Virtual Camera to sync with AR Camera
            Camera virtualCam = _sceneManager.VirtualCamera;
            if (virtualCam != null && arCam != null)
            {
                // Setup URP Camera Stacking: AR Camera (Base) -> Virtual Camera (Overlay)
                var arCameraData = arCam.GetUniversalAdditionalCameraData();
                var virtualCameraData = virtualCam.GetUniversalAdditionalCameraData();
                
                if (virtualCameraData != null)
                {
                    virtualCameraData.renderType = CameraRenderType.Overlay;
                    if (arCameraData != null && !arCameraData.cameraStack.Contains(virtualCam))
                    {
                        arCameraData.cameraStack.Add(virtualCam);
                    }
                }

                SyncVirtualCamera sync = virtualCam.gameObject.GetComponent<SyncVirtualCamera>();
                if (sync == null)
                {
                    // Ensure Virtual Camera clears to depth only to see AR background
                    virtualCam.clearFlags = CameraClearFlags.Depth;
                    virtualCam.depth = 0; // Render on top of AR Camera
                    
                    Debug.Log($"[ARAnchorTracker] Virtual Camera Setup: Mask={virtualCam.cullingMask}, Depth={virtualCam.depth}, ClearFlags={virtualCam.clearFlags}");

                    // Parent Virtual Camera to AR Camera for perfect pose sync
                    //virtualCam.transform.SetParent(arCam.transform);
                    //virtualCam.transform.localPosition = Vector3.zero;
                    //virtualCam.transform.localRotation = Quaternion.identity;

                    sync = virtualCam.gameObject.AddComponent<SyncVirtualCamera>();
                    sync.arCamera = arCam;
                    sync.virtualCamera = virtualCam;

                    // Manual sync immediately since OnEnable might have skipped it due to null fields
                    virtualCam.ResetProjectionMatrix();
                    virtualCam.projectionMatrix = arCam.projectionMatrix;
                }
                else
                {
                    Debug.Log($"[ARAnchorTracker] Virtual Camera sync is manually configured. Skipping auto-hierarchy setup.");
                    if (sync.arCamera == null) sync.arCamera = arCam;
                    if (sync.virtualCamera == null) sync.virtualCamera = virtualCam;
                    
                    // Parent Virtual Camera to AR Camera for perfect pose sync
                    //virtualCam.transform.SetParent(arCam.transform);
                    //virtualCam.transform.localPosition = Vector3.zero;
                    //virtualCam.transform.localRotation = Quaternion.identity;
                }
            }

            if (_data.imageManager == null && _data.xrOrigin != null)
            {
                _data.imageManager = _data.xrOrigin.gameObject.GetComponent<ARTrackedImageManager>();
                if (_data.imageManager == null)
                {
                    _data.imageManager = _data.xrOrigin.gameObject.AddComponent<ARTrackedImageManager>();
                }
            }
            
            if (_data.imageManager != null)
            {
                // Ensure we can track moving images (or at least more than 0)
                _data.imageManager.requestedMaxNumberOfMovingImages = 4;
            }

            // Ensure near clipping plane is small enough for close-up AR interaction
            if (_data.xrOrigin != null && _data.xrOrigin.Camera != null)
            {
                _data.xrOrigin.Camera.nearClipPlane = 0.01f;
            }

            // Reset smoothing state on re-init
            _hasInitialPose = false;
            _isTransitioning = false;
        }

        private async void UpdateImageLibrary()
        {
            if (_imageManager == null) return;

            // Check if there are any image tracking providers at all
            var descriptors = new List<XRImageTrackingSubsystemDescriptor>();
            SubsystemManager.GetSubsystemDescriptors(descriptors);
            if (descriptors.Count == 0)
            {
                Debug.LogError("[ARAnchorTracker] NO XRImageTrackingSubsystem descriptors found. This means no ARCore/ARKit provider is installed or enabled in Project Settings.");
                return;
            }
            else
            {
                Debug.Log($"[ARAnchorTracker] Found {descriptors.Count} image tracking descriptors. First: {descriptors[0].id}");
            }

            // Setup library first. Subsystem might need this to start.
            if (_runtimeLibrary == null)
            {
                try
                {
                    _runtimeLibrary = _imageManager.CreateRuntimeLibrary() as MutableRuntimeReferenceImageLibrary;
                    _imageManager.referenceLibrary = _runtimeLibrary;
                    Debug.Log("[ARAnchorTracker] Created and assigned MutableRuntimeReferenceImageLibrary.");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ARAnchorTracker] Failed to create runtime library: {e.Message}");
                    return;
                }
            }

            // Force the manager to "re-discover" the subsystem now that the library is set
            _imageManager.enabled = false;
            await Task.Yield(); // Wait one frame
            _imageManager.enabled = true;
            Debug.Log("[ARAnchorTracker] Toggled ImageManager to force subsystem initialization.");

            // Now wait for subsystem to start
            float waitTimeout = 10f;
            float waitElapsed = 0f;
            while ((_imageManager.subsystem == null || !_imageManager.subsystem.running) && waitElapsed < waitTimeout)
            {
                if (_arSession != null && ARSession.state == ARSessionState.SessionTracking && _imageManager.subsystem == null)
                {
                    // If session is tracking but image subsystem is still null, try forcing it again
                    if (waitElapsed > 3f && waitElapsed < 3.3f)
                    {
                        Debug.Log("[ARAnchorTracker] Session is tracking but Image Subsystem is null. Retrying manager toggle...");
                        _imageManager.enabled = false;
                        _imageManager.enabled = true;
                    }
                }

                if (waitElapsed > 1f && waitElapsed % 1f < 0.2f)
                {
                    string subRunning = _imageManager.subsystem != null ? _imageManager.subsystem.running.ToString() : "null";
                    Debug.Log($"[ARAnchorTracker] Waiting for Subsystem... Elapsed: {waitElapsed:F1}s, Image Subsystem Running: {subRunning}, Session: {ARSession.state}");
                }
                await Task.Delay(200);
                waitElapsed += 0.2f;
            }

            if (_imageManager.subsystem == null || !_imageManager.subsystem.running)
            {
                Debug.LogError($"[ARAnchorTracker] Subsystem failed to start within {waitTimeout}s. Session State: {ARSession.state}, Subsystem Null: {_imageManager.subsystem == null}");
                return;
            }

            Debug.Log("[ARAnchorTracker] Subsystem is RUNNING. Waiting 1s for it to settle...");
            await Task.Delay(1000); // Wait 1 second after "Running" for native drive to settle
            Debug.Log("[ARAnchorTracker] Settle period complete. Processing anchors...");

            // Note: AR Foundation 6.0 docs: "You do not need to disable the manager to add images."
            // Disabling it might actually stop the subsystem and cause failures.

            if (_anchorManager.Anchors != null)
            {
                int addedCount = 0;
                foreach (var anchor in _anchorManager.Anchors)
                {
                    if (anchor is AnchorImageData imageData && imageData.Image is Texture2D tex)
                    {
                        // Diagnostic logging
                        Debug.Log($"[ARAnchorTracker] Preparing to add image: {imageData.Name}. Texture: {tex.width}x{tex.height}, Format: {tex.format}, Mipmaps: {tex.mipmapCount}, Readable: {tex.isReadable}");
                        
                        float physicalWidth = imageData.Size;
                        if (physicalWidth <= 0)
                        {
                            physicalWidth = 0.2f; // 20cm default
                            Debug.LogWarning($"[ARAnchorTracker] Image {imageData.Name} has invalid physical size ({imageData.Size}). Falling back to 0.2m.");
                        }
                        else
                        {
                            // User specified: size is the "shortest side length".
                            // AR Foundation wants "widthInMeters".
                            if (tex.width > tex.height)
                            {
                                // Landscape: Height is shortest. Width = size * (aspect ratio)
                                float aspectRatio = (float)tex.width / tex.height;
                                physicalWidth = imageData.Size * aspectRatio;
                                Debug.Log($"[ARAnchorTracker] Landscape image detected. Calculating width from shortest side ({imageData.Size}m) -> {physicalWidth}m");
                            }
                            else
                            {
                                // Portrait or Square: Width is shortest (or equal). Width = size
                                physicalWidth = imageData.Size;
                                Debug.Log($"[ARAnchorTracker] Portrait/Square image detected. Physical width: {physicalWidth}m");
                            }
                        }

                        if (!tex.isReadable)
                        {
                            Debug.LogError($"[ARAnchorTracker] Texture {tex.name} is NOT readable! Tracking will fail.");
                            continue;
                        }

                        try
                        {
                            // Sanitization: ARCore prefers standard formats (RGBA32/RGB24) and NO mipmaps for dynamic libraries.
                            // NPOT + Mipmaps + Odd dimensions often causes ErrorUnknown in native code.
                            // We now also force Power-of-Two (POT) for maximum compatibility.
                            Texture2D sanitizedTex = CreateSanitizedTexture(tex);
                            _activeSanitizedTextures.Add(sanitizedTex);
                            
                            var job = _runtimeLibrary.ScheduleAddImageWithValidationJob(sanitizedTex, imageData.Name, physicalWidth);
                            addedCount++;
                            Debug.Log($"[ARAnchorTracker] Scheduled adding POT SANITIZED image: {imageData.Name} ({sanitizedTex.width}x{sanitizedTex.height}, No Mipmaps, {sanitizedTex.format})");
                            MonitorJobStatus(job, imageData.Name, sanitizedTex);
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[ARAnchorTracker] Error adding image {imageData.Name}: {e.Message}");
                        }
                    }
                }
                Debug.Log($"[ARAnchorTracker] Total images scheduled: {addedCount}");
            }
            else
            {
                Debug.LogWarning("[ARAnchorTracker] No anchors found in AnchorManager.");
            }
        }

        private Texture2D CreateSanitizedTexture(Texture2D tex)
        {
            // Super Sanitizer for ARCore:
            // 1. Max resolution 512 (High enough for tracking, low enough for stable memory).
            // 2. Multiples of 8 for both dimensions (Standard alignment).
            // 3. Forced RGBA32, No Mipmaps.
            
            float scale = 1.0f;
            int maxDim = Mathf.Max(tex.width, tex.height);
            if (maxDim > 512)
            {
                scale = 512f / maxDim;
            }
            
            int targetWidth = Mathf.RoundToInt(tex.width * scale);
            int targetHeight = Mathf.RoundToInt(tex.height * scale);
            
            // Snap to multiples of 8
            targetWidth = (targetWidth / 8) * 8;
            targetHeight = (targetHeight / 8) * 8;
            
            // Ensure at least 8x8
            if (targetWidth < 8) targetWidth = 8;
            if (targetHeight < 8) targetHeight = 8;

            Debug.Log($"[ARAnchorTracker] Super Sanitizing: {tex.width}x{tex.height} -> {targetWidth}x{targetHeight} (Multiple of 8, Limit 512)");

            Texture2D cleanTex = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            cleanTex.name = tex.name + "_sanitized_super";
            
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);
            
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            cleanTex.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            cleanTex.Apply(false, false); // No mipmaps, readable
            
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            
            return cleanTex;
        }

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> args)
        {
            // First time we get an event, we know the subsystem is active
            foreach (var trackedImage in args.added)
            {
                Debug.Log($"[ARAnchorTracker] Image DETECTED: {trackedImage.referenceImage.name}, State: {trackedImage.trackingState}, Pose: {trackedImage.transform.position}");
                UpdateScenePosition(trackedImage);
            }

            foreach (var trackedImage in args.updated)
            {
                if (trackedImage.trackingState == TrackingState.Tracking)
                {
                    UpdateScenePosition(trackedImage);
                }
            }

            foreach (var pair in args.removed)
            {
                if (pair.Value != null && pair.Value.referenceImage != null)
                    Debug.Log($"[ARAnchorTracker] Image REMOVED: {pair.Value.referenceImage.name}");
                else
                    Debug.Log($"[ARAnchorTracker] Image REMOVED ID: {pair.Key}");
            }
        }

        // Monitoring job status (called when anchors are refreshed)
        private async void MonitorJobStatus(AddReferenceImageJobState jobState, string imageName, Texture2D texToRelease)
        {
            float timeout = 5f;
            float elapsed = 0f;
            while ((jobState.status == AddReferenceImageJobStatus.None || jobState.status == AddReferenceImageJobStatus.Pending) && elapsed < timeout)
            {
                await Task.Delay(100);
                elapsed += 0.1f;
            }

            if (jobState.status != AddReferenceImageJobStatus.Success)
            {
                Debug.LogError($"[ARAnchorTracker] Failed to add image {imageName}. Status: {jobState.status}");
            }
            else
            {
                Debug.Log($"[ARAnchorTracker] Successfully added image {imageName} to library.");
            }

            // Remove from list so it can be GC'd now that native code has it
            if (texToRelease != null)
            {
                _activeSanitizedTextures.Remove(texToRelease);
            }
        }

        private void UpdateScenePosition(ARTrackedImage trackedImage)
        {
            string imageName = trackedImage.referenceImage.name;
            bool found = false;
            AnchorImageData targetAnchor = null;

            if (_anchorManager.Anchors != null)
            {
                foreach (var anchor in _anchorManager.Anchors)
                {
                    if (anchor is AnchorImageData imageData && imageData.Name == imageName)
                    {
                        targetAnchor = imageData;
                        found = true;
                        break;
                    }
                }
            }

            if (found && _sceneManager.Root != null)
            {
                // Math for origin correction:
                // We want: trackedPos = sceneRootPos + sceneRootRot * anchorPos
                //          trackedRot = sceneRootRot * anchorRot
                
                Quaternion anchorRot = Quaternion.Euler(targetAnchor.Rotation);
                Vector3 anchorPos = targetAnchor.Position;

                // AR Foundation's ARTrackedImage has its normal (the vector pointing "out" of the image) along the Y axis.
                // In many cases, users expect the normal to be along the Z axis (Forward).
                // We apply a 90 degree rotation on X to align AR Foundation's orientation with a standard Z-forward orientation.
                // We also add a 180 degree rotation on Y so the scene faces the user (out of the image).
                Quaternion arFoundationToUnity = Quaternion.Euler(90, 0, 0) * Quaternion.Euler(0, 180, 0);
                Quaternion correctedTrackedRot = trackedImage.transform.rotation * arFoundationToUnity;
                Vector3 correctedTrackedPos = trackedImage.transform.position;

                // R_root = R_tracked * Inverse(R_anchor)
                Quaternion rootRot = correctedTrackedRot * Quaternion.Inverse(anchorRot);
                
                // P_root = P_tracked - R_root * P_anchor
                Vector3 rootPos = correctedTrackedPos - (rootRot * anchorPos);

                if (!_hasInitialPose)
                {
                    // Snap the first detection
                    _sceneManager.Root.position = rootPos;
                    _sceneManager.Root.rotation = rootRot;
                    _hasInitialPose = true;
                    Debug.Log($"[ARAnchorTracker] Initial Scene Root snapped to {imageName}. Pos: {rootPos}");
                }
                else
                {
                    // Start smooth transition for subsequent updates
                    _sourceRootPos = _sceneManager.Root.position;
                    _sourceRootRot = _sceneManager.Root.rotation;
                    _targetRootPos = rootPos;
                    _targetRootRot = rootRot;
                    _transitionTimer = 0f;
                    _isTransitioning = true;
                    
                    // Optional: only log when transition starts to avoid log spam
                    // Debug.Log($"[ARAnchorTracker] Smoothing Scene Root correction for {imageName}...");
                }
            }
        }
#endif
    }
}
