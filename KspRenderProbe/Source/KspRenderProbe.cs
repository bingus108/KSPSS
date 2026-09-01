using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace KspRenderProbe
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class RenderProbe : MonoBehaviour
    {
        private const string Prefix = "[KspRenderProbe] ";
        private const float ScanIntervalSeconds = 1.0f;
        private const int CaptureWidth = 320;
        private const int CaptureHeight = 180;
        private static readonly Vector2[] Halton8 =
        {
            new Vector2(0.0f, -1.0f / 6.0f), new Vector2(-0.25f, 1.0f / 6.0f),
            new Vector2(0.25f, -7.0f / 18.0f), new Vector2(-0.375f, -1.0f / 18.0f),
            new Vector2(0.125f, 5.0f / 18.0f), new Vector2(-0.125f, -5.0f / 18.0f),
            new Vector2(0.375f, 1.0f / 18.0f), new Vector2(-0.4375f, 7.0f / 18.0f)
        };

        private readonly Dictionary<int, CameraRecord> records = new Dictionary<int, CameraRecord>();
        private Camera selectedCamera;
        private CameraRecord selectedRecord;
        private float nextScan;
        private bool overlayVisible = true;
        private bool jitterEnabled;
        private int jitterIndex;
        private GUIStyle overlayStyle;

        public void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Camera.onPreCull += OnCameraPreCull;
            Camera.onPostRender += OnCameraPostRender;
            Log("Stage 1 probe loaded. No camera is probed automatically; select one with F10, then explicitly attach with F7.");
            Log("Runtime: Unity=" + Application.unityVersion + " API=" + SystemInfo.graphicsDeviceType +
                " device=" + SystemInfo.graphicsDeviceName + " MV-support=" + SystemInfo.supportsMotionVectors +
                " screen=" + Screen.width + "x" + Screen.height + " fullscreen=" + Screen.fullScreen);
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                Log("BLOCKER: expected Direct3D11; no DLSS feasibility conclusion should be drawn from this run.");
        }

        public void Start() { ScanAndReport("initial flight-scene scan"); }

        public void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8)) overlayVisible = !overlayVisible;
            if (Input.GetKeyDown(KeyCode.F7)) ToggleProbeAttachment();
            if (Input.GetKeyDown(KeyCode.F9)) ToggleJitter();
            if (Input.GetKeyDown(KeyCode.F10)) SelectRelativeCamera(Input.GetKey(KeyCode.LeftShift) ? -1 : 1);
            if (Input.GetKeyDown(KeyCode.F11)) ScanAndReport("manual report");
            if (Time.unscaledTime >= nextScan) { nextScan = Time.unscaledTime + ScanIntervalSeconds; ScanCameras(); }
        }

        public void OnDisable()
        {
            Camera.onPreCull -= OnCameraPreCull;
            Camera.onPostRender -= OnCameraPostRender;
            foreach (CameraRecord record in records.Values) record.Dispose();
            records.Clear();
        }

        private void ToggleProbeAttachment()
        {
            if (selectedRecord == null) { Log("No camera selected. Use F10 first."); return; }
            if (selectedRecord.IsProbeAttached) { jitterEnabled = false; selectedRecord.DetachProbe(); Log("Probe detached from " + CameraLabel(selectedCamera)); return; }
            if (!selectedRecord.IsSafeSceneProbeCandidate) { Log("REFUSED: " + CameraLabel(selectedCamera) + " is classified as " + selectedRecord.Role + ". Select the observed near-scene candidate instead."); return; }
            selectedRecord.AttachProbe();
        }

        private void ToggleJitter()
        {
            if (!jitterEnabled)
            {
                if (selectedRecord == null || !selectedRecord.IsProbeAttached || !selectedRecord.IsSafeSceneProbeCandidate)
                {
                    Log("REFUSED: jitter requires an explicitly attached near-scene probe camera. UI, canvas, scaled-space, galaxy, marker and FX cameras are blocked.");
                    return;
                }
                jitterEnabled = true; Log("Projection jitter=True on " + CameraLabel(selectedCamera)); return;
            }
            jitterEnabled = false;
            if (selectedRecord != null) selectedRecord.RestoreProjection();
            Log("Projection jitter=False");
        }

        private void ScanAndReport(string reason)
        {
            ScanCameras();
            Log("Camera report: " + reason + "; live=" + records.Count + "; selected=" + CameraLabel(selectedCamera));
            foreach (CameraRecord record in SortedRecords()) Log(record.Describe());
        }

        private void ScanCameras()
        {
            Camera[] cameras = Camera.allCameras;
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (camera == null) continue;
                int id = camera.GetInstanceID(); seen.Add(id);
                CameraRecord record;
                if (!records.TryGetValue(id, out record))
                {
                    record = new CameraRecord(camera); records.Add(id, record);
                    Log("Discovered " + record.Describe());
                }
                record.Refresh();
            }
            List<int> stale = new List<int>();
            foreach (KeyValuePair<int, CameraRecord> pair in records) if (!seen.Contains(pair.Key)) stale.Add(pair.Key);
            for (int i = 0; i < stale.Count; i++) { records[stale[i]].Dispose(); records.Remove(stale[i]); }
            SelectDisplayCameraIfNeeded();
        }

        private void SelectDisplayCameraIfNeeded()
        {
            if (selectedCamera != null && selectedRecord != null) return;
            List<CameraRecord> candidates = SortedRecords();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].IsSafeSceneProbeCandidate) continue;
                SetSelected(candidates[i], "automatic display selection only; no probe attachment");
                return;
            }
        }

        private List<CameraRecord> SortedRecords()
        {
            List<CameraRecord> result = new List<CameraRecord>(records.Values);
            result.Sort(delegate(CameraRecord a, CameraRecord b) { return b.Score.CompareTo(a.Score); });
            return result;
        }

        private void SelectRelativeCamera(int delta)
        {
            List<CameraRecord> sorted = SortedRecords();
            if (sorted.Count == 0) { Log("No camera available to select."); return; }
            int index = sorted.IndexOf(selectedRecord);
            if (index < 0) { SetSelected(sorted[0], "manual tester selection"); return; }
            index = (index + delta + sorted.Count) % sorted.Count;
            SetSelected(sorted[index], "manual tester selection");
        }

        private void SetSelected(CameraRecord next, string reason)
        {
            if (selectedRecord == next) return;
            if (selectedRecord != null) { jitterEnabled = false; selectedRecord.DetachProbe(); }
            selectedRecord = next; selectedCamera = next.Camera;
            Log("Selected " + CameraLabel(selectedCamera) + ": " + reason);
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (camera != selectedCamera || selectedRecord == null || !selectedRecord.IsProbeAttached) return;
            selectedRecord.ObserveGlobals("pre-cull");
            if (!jitterEnabled) return;
            selectedRecord.SaveProjection();
            Vector2 sample = Halton8[jitterIndex++ % Halton8.Length];
            Matrix4x4 jittered = selectedRecord.BaseProjection;
            jittered.m02 += sample.x * 0.5f / Mathf.Max(1, camera.pixelWidth);
            jittered.m12 += sample.y * 0.5f / Mathf.Max(1, camera.pixelHeight);
            camera.nonJitteredProjectionMatrix = selectedRecord.BaseProjection;
            camera.projectionMatrix = jittered;
            selectedRecord.LastJitter = sample;
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (camera != selectedCamera || selectedRecord == null || !selectedRecord.IsProbeAttached) return;
            selectedRecord.ObserveGlobals("post-render");
            if (jitterEnabled) selectedRecord.RestoreProjection();
        }

        private void OnGUI()
        {
            if (!overlayVisible || selectedRecord == null) return;
            if (overlayStyle == null) { overlayStyle = new GUIStyle(GUI.skin.label); overlayStyle.fontSize = 12; overlayStyle.normal.textColor = Color.white; }
            GUI.Box(new Rect(8, 8, 510, 140), "KSP Render Probe — Stage 1 (F7 attach, F8 overlay, F9 jitter, F10 select, F11 report)");
            GUI.Label(new Rect(18, 32, 490, 100), selectedRecord.OverlayText(), overlayStyle);
            DrawTexture("BeforeImageEffects", selectedRecord.BeforeImageEffects, 8, 156);
            DrawTexture("AfterEverything", selectedRecord.AfterEverything, 336, 156);
            DrawTexture("Depth", selectedRecord.Depth, 8, 362);
            DrawTexture("Motion vectors", selectedRecord.MotionVectors, 336, 362);
        }

        private void DrawTexture(string label, Texture texture, float x, float y)
        {
            GUI.Label(new Rect(x, y, 300, 18), label, overlayStyle);
            if (texture != null) GUI.DrawTexture(new Rect(x, y + 18, 320, 180), texture, ScaleMode.ScaleToFit, false);
            else GUI.Label(new Rect(x, y + 40, 300, 24), "not observed for selected camera", overlayStyle);
        }

        private static string CameraLabel(Camera camera) { return camera == null ? "<none>" : camera.name + "#" + camera.GetInstanceID(); }
        private static void Log(string message) { Debug.Log(Prefix + message); }

        private sealed class CameraRecord
        {
            internal readonly Camera Camera;
            internal RenderTexture BeforeImageEffects;
            internal RenderTexture AfterEverything;
            internal Texture Depth;
            internal Texture DepthNormals;
            internal Texture MotionVectors;
            internal Vector2 LastJitter;
            internal int Score;
            private CommandBuffer beforeBuffer;
            private CommandBuffer afterBuffer;
            private DepthTextureMode savedDepthMode;
            private bool attached;
            private bool projectionSaved;
            internal Matrix4x4 BaseProjection;
            internal string Role { get; private set; }
            internal bool IsProbeAttached { get { return attached; } }
            internal bool IsSafeSceneProbeCandidate { get { return Role == "near-scene candidate"; } }

            internal CameraRecord(Camera camera) { Camera = camera; Refresh(); }

            internal void Refresh()
            {
                Score = 0;
                if (Camera.isActiveAndEnabled) Score += 100;
                if (Camera.targetTexture == null) Score += 20;
                if (Camera.cameraType == CameraType.Game) Score += 10;
                if (Camera.depth > 0f) Score += 2;
                string name = Camera.name == null ? "" : Camera.name.ToLowerInvariant();
                if (name.Contains("flight")) Score += 15;
                if (name.Contains("camera")) Score += 2;
                Role = Classify(name);
                if (Role == "ui/canvas") Score -= 1000;
                if (Role == "near-scene candidate") Score += 50;
            }

            private string Classify(string lowerName)
            {
                if (lowerName.Contains("ui") || lowerName.Contains("canvas")) return "ui/canvas";
                if (lowerName.Contains("galaxy")) return "galaxy/background";
                if (lowerName.Contains("scaledspace")) return "scaled-space";
                if (lowerName.Contains("marker")) return "marker";
                if (lowerName.Contains("fx")) return "effects";
                // This is a runtime observation from the initial clean KSP 1.12.5 probe, not a general KSP assumption.
                if (Camera.name == "Camera 00" && Mathf.Approximately(Camera.depth, 0f)) return "near-scene candidate";
                return "unclassified";
            }

            internal void AttachProbe()
            {
                if (attached) return;
                savedDepthMode = Camera.depthTextureMode;
                Camera.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;
                BeforeImageEffects = MakeCapture("KspRenderProbe_BeforeImageEffects");
                AfterEverything = MakeCapture("KspRenderProbe_AfterEverything");
                beforeBuffer = new CommandBuffer(); beforeBuffer.name = "KspRenderProbe: before image effects color copy";
                beforeBuffer.Blit(BuiltinRenderTextureType.CurrentActive, BeforeImageEffects);
                afterBuffer = new CommandBuffer(); afterBuffer.name = "KspRenderProbe: after everything color copy";
                afterBuffer.Blit(BuiltinRenderTextureType.CurrentActive, AfterEverything);
                Camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, beforeBuffer);
                Camera.AddCommandBuffer(CameraEvent.AfterEverything, afterBuffer);
                attached = true;
                Log("Probe attached to " + CameraLabel(Camera) + " role=" + Role + "; requested depth modes=" + Camera.depthTextureMode);
            }

            internal void DetachProbe()
            {
                if (!attached) return;
                RestoreProjection();
                if (beforeBuffer != null) { Camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, beforeBuffer); beforeBuffer.Release(); beforeBuffer = null; }
                if (afterBuffer != null) { Camera.RemoveCommandBuffer(CameraEvent.AfterEverything, afterBuffer); afterBuffer.Release(); afterBuffer = null; }
                Camera.depthTextureMode = savedDepthMode;
                ReleaseCapture(ref BeforeImageEffects); ReleaseCapture(ref AfterEverything);
                attached = false;
            }

            internal void Dispose() { DetachProbe(); }

            internal void ObserveGlobals(string phase)
            {
                Depth = Shader.GetGlobalTexture("_CameraDepthTexture");
                DepthNormals = Shader.GetGlobalTexture("_CameraDepthNormalsTexture");
                MotionVectors = Shader.GetGlobalTexture("_CameraMotionVectorsTexture");
                if (Depth == null || DepthNormals == null || MotionVectors == null)
                    Log("Buffer observation " + CameraLabel(Camera) + " " + phase + ": depth=" + TextureLabel(Depth) + " normals=" + TextureLabel(DepthNormals) + " motion=" + TextureLabel(MotionVectors));
            }

            internal void SaveProjection()
            {
                if (projectionSaved) return;
                BaseProjection = Camera.projectionMatrix; projectionSaved = true;
            }

            internal void RestoreProjection()
            {
                if (!projectionSaved) return;
                Camera.projectionMatrix = BaseProjection; Camera.nonJitteredProjectionMatrix = BaseProjection; projectionSaved = false;
            }

            internal string Describe()
            {
                StringBuilder events = new StringBuilder();
                Array values = Enum.GetValues(typeof(CameraEvent));
                for (int i = 0; i < values.Length; i++)
                {
                    CameraEvent evt = (CameraEvent)values.GetValue(i); CommandBuffer[] buffers = Camera.GetCommandBuffers(evt);
                    if (buffers.Length == 0) continue;
                    if (events.Length > 0) events.Append(", "); events.Append(evt).Append("=").Append(buffers.Length);
                }
                return "Camera name='" + Camera.name + "' id=" + Camera.GetInstanceID() + " enabled=" + Camera.isActiveAndEnabled +
                    " type=" + Camera.cameraType + " depth(order)=" + Camera.depth + " rect=" + Camera.pixelWidth + "x" + Camera.pixelHeight +
                    " target=" + TargetLabel(Camera.targetTexture) + " msaa=" + (Camera.targetTexture == null ? QualitySettings.antiAliasing : Camera.targetTexture.antiAliasing) +
                    " depthModes=" + Camera.depthTextureMode + " role=" + Role + " score=" + Score + " commandBuffers=[" + events + "]";
            }

            internal string OverlayText()
            {
                return CameraLabel(Camera) + "  role=" + Role + "  attached=" + attached + "  order=" + Camera.depth + "\n" +
                    "" + Camera.pixelWidth + "x" + Camera.pixelHeight + " target=" + TargetLabel(Camera.targetTexture) + " MSAA=" + (Camera.targetTexture == null ? QualitySettings.antiAliasing : Camera.targetTexture.antiAliasing) + "\n" +
                    "Depth=" + TextureLabel(Depth) + "\nNormals=" + TextureLabel(DepthNormals) + "  Motion=" + TextureLabel(MotionVectors) + "\n" +
                    "Jitter=" + LastJitter + " (F9 permitted only for attached near-scene candidate)";
            }

            private static RenderTexture MakeCapture(string name)
            {
                RenderTexture rt = new RenderTexture(CaptureWidth, CaptureHeight, 0, RenderTextureFormat.ARGB32);
                rt.name = name; rt.hideFlags = HideFlags.DontSave; rt.Create(); return rt;
            }
            private static void ReleaseCapture(ref RenderTexture capture) { if (capture != null) { capture.Release(); Destroy(capture); capture = null; } }
            private static string TargetLabel(RenderTexture target) { return target == null ? "backbuffer" : target.name + " " + target.width + "x" + target.height + " fmt=" + target.format; }
            private static string TextureLabel(Texture texture) { return texture == null ? "<null>" : texture.name + " " + texture.width + "x" + texture.height + " type=" + texture.GetType().Name; }
        }
    }
}
