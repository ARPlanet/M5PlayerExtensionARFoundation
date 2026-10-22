using Module5.Player;
using UnityEngine;

namespace Module5.Player.ARFoundation
{
    /// <summary>
    /// 同步 AR Camera 裁剪面與投影矩陣至 VirtualCamera；支援在 FOV 覆蓋模式下回退至自適應 FOV
    /// </summary>
    public class SyncVirtualARCamera : SyncVirtualCamera
    {
        [Header("AR Camera Binding")]
        public Camera arCamera;

        public override void SetFovOverride(bool isOverride, float targetFov = 60f)
        {
            base.SetFovOverride(isOverride, targetFov);
            if (!isOverride && virtualCamera != null && arCamera != null)
            {
                virtualCamera.ResetProjectionMatrix();
                SyncFromARCamera();
            }
        }

        protected override bool ShouldUpdateFovInLateUpdate()
        {
            // 在未綁定 arCamera 或處於 360 覆蓋 FOV 時，才執行短邊自適應 FOV
            return arCamera == null || isOverrideFov;
        }

        protected override void OnCameraSync()
        {
            if (arCamera != null && !isOverrideFov)
            {
                SyncFromARCamera();
            }
            else
            {
                base.OnCameraSync();
            }
        }

        /// <summary>
        /// 同步 ARCamera 的裁剪面 (nearClipPlane, farClipPlane) 與投影矩陣 (projectionMatrix)。
        /// </summary>
        public virtual void SyncFromARCamera()
        {
            if (virtualCamera == null || arCamera == null) return;

            if (!Mathf.Approximately(virtualCamera.nearClipPlane, arCamera.nearClipPlane))
            {
                virtualCamera.nearClipPlane = arCamera.nearClipPlane;
            }

            if (!Mathf.Approximately(virtualCamera.farClipPlane, arCamera.farClipPlane))
            {
                virtualCamera.farClipPlane = arCamera.farClipPlane;
            }

            virtualCamera.projectionMatrix = arCamera.projectionMatrix;
        }
    }
}
