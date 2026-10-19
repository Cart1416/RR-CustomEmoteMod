using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Photon.Pun;
using RecRoom.Players;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;

namespace PoseRecorder
{
    [BepInPlugin("com.recroom.poserecorder", "PoseRecorder", "1.1.0")]
    public class PoseRecorderPlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        // Own config file
        internal static ConfigFile PluginConfig;
        internal static ConfigEntry<string> KeyComboSetting;

        // Parsed key codes from the config string
        internal static List<KeyCode> ParsedKeys = new List<KeyCode>();

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("PoseRecorder v1.1.0 loading...");

            // Create our own config file
            PluginConfig = new ConfigFile(Path.Combine(Paths.ConfigPath, "PoseRecorder.cfg"), true);
            KeyComboSetting = PluginConfig.Bind(
                "Recording",
                "KeyCombo",
                "LeftControl+LeftAlt+LeftWindows+Alpha5",
                "Key combination to toggle recording. Separate keys with '+'. Use Unity KeyCode names (e.g. LeftControl, RightControl, LeftAlt, RightAlt, LeftWindows, RightWindows, LeftCommand, RightCommand, Alpha0-Alpha9, F1-F12, etc).");

            LoadKeyCombo();

            // Subscribe to scene loaded to reload config on room change
            SceneManager.add_sceneLoaded(new Action<Scene, LoadSceneMode>(OnSceneLoaded));

            _harmony = new Harmony("com.recroom.poserecorder");
            _harmony.PatchAll();
            Log.LogInfo("PoseRecorder patched.");
        }

        public override bool Unload()
        {
            SceneManager.remove_sceneLoaded(new Action<Scene, LoadSceneMode>(OnSceneLoaded));
            _harmony.UnpatchSelf();
            Log.LogInfo("PoseRecorder unpatched.");
            return true;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo($"Scene loaded: {scene.name} ({mode}). Reloading keybind config...");
            LoadKeyCombo();
        }

        internal static void LoadKeyCombo()
        {
            try
            {
                PluginConfig.Reload();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Config reload failed: {ex.Message}");
            }

            string combo = KeyComboSetting.Value;
            ParsedKeys.Clear();

            string[] parts = combo.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (Enum.TryParse<KeyCode>(trimmed, true, out KeyCode kc))
                {
                    ParsedKeys.Add(kc);
                }
                else
                {
                    Log.LogWarning($"Unknown KeyCode in config: '{trimmed}'. Skipping.");
                }
            }

            if (ParsedKeys.Count == 0)
            {
                Log.LogWarning("No valid keys parsed from config. Defaulting to LeftControl+LeftAlt+LeftWindows+Alpha5.");
                ParsedKeys.AddRange(new[] { KeyCode.LeftControl, KeyCode.LeftAlt, KeyCode.LeftWindows, KeyCode.Alpha5 });
            }

            Log.LogInfo($"Keybind loaded: {string.Join(" + ", ParsedKeys.Select(k => k.ToString()))}");
        }

        internal static bool IsComboPressed()
        {
            if (ParsedKeys.Count == 0) return false;
            foreach (KeyCode kc in ParsedKeys)
            {
                if (!Input.GetKey(kc)) return false;
            }
            return true;
        }

        // --- Recorder state shared between patches ---
        public static class RecorderState
        {
            public static bool IsRecording = false;
            public static float RecordInterval = 1f / 15f; // 15 fps (configurable)
            public static float NextRecordTime = 0f;
            public static List<Keyframe> Keyframes = new List<Keyframe>();
            public static float RecordingStartTime = 0f;
            public static float LastToggleTime = 0f;
            public const float ToggleCooldown = 0.5f; // seconds
        }

        // --- Patch to check for key combination and toggle recording ---
        [HarmonyPatch(typeof(RecRoomSceneManager), "Update")]
        private static class UpdatePatch
        {
            private static void Postfix()
            {
                bool comboPressed = IsComboPressed();
                if (comboPressed && Time.time - RecorderState.LastToggleTime > RecorderState.ToggleCooldown)
                {
                    RecorderState.LastToggleTime = Time.time;
                    if (!RecorderState.IsRecording)
                    {
                        StartRecording();
                    }
                    else
                    {
                        StopRecording();
                    }
                }
            }

            private static void StartRecording()
            {
                PoseRecorderPlugin.Log.LogInfo("Starting pose recording...");
                RecorderState.IsRecording = true;
                RecorderState.Keyframes.Clear();
                RecorderState.RecordingStartTime = Time.time;
                RecorderState.NextRecordTime = Time.time; // record immediately
            }

            private static void StopRecording()
            {
                PoseRecorderPlugin.Log.LogInfo("Stopping pose recording and saving to CustomEmote.cfg...");
                RecorderState.IsRecording = false;
                SaveKeyframes();
            }
        }

        // --- Patch to record the current frame at the desired interval ---
        [HarmonyPatch(typeof(PlayerBody), "LateUpdate")]
        private static class LateUpdatePatch
        {
            private static void Postfix(PlayerBody __instance)
            {
                if (!RecorderState.IsRecording)
                    return;

                if (Time.time < RecorderState.NextRecordTime)
                    return;

                // Get local player
                Player localPlayer = Player.MDMMDPEKICF;
                if (localPlayer == null)
                {
                    PoseRecorderPlugin.Log.LogWarning("Local player not found");
                    return;
                }

                // Get body transform
                PlayerBody body = localPlayer.body;
                if (body == null)
                {
                    PoseRecorderPlugin.Log.LogWarning("Player body not found");
                    return;
                }
                Transform bodyTransform = body.transform;
                if (bodyTransform == null)
                {
                    PoseRecorderPlugin.Log.LogWarning("Body transform not found");
                    return;
                }

                // Get head transform
                Transform headTransform = localPlayer.head?.transform;
                if (headTransform == null)
                {
                    PoseRecorderPlugin.Log.LogWarning("Head transform not found");
                }

                // Get left hand transform
                Transform leftHandTransform = localPlayer.leftHand?.transform;
                if (leftHandTransform == null)
                {
                    PoseRecorderPlugin.Log.LogWarning("Left hand transform not found");
                }

                // Get right hand transform
                Transform rightHandTransform = localPlayer.rightHand?.transform;
                if (rightHandTransform == null)
                {
                    PoseRecorderPlugin.Log.LogWarning("Right hand transform not found");
                }

                // Compute local positions and rotations relative to body
                Vector3 headPosLocal = Vector3.zero;
                Quaternion headRotLocal = Quaternion.identity;
                Vector3 leftHandPosLocal = Vector3.zero;
                Quaternion leftHandRotLocal = Quaternion.identity;
                Vector3 rightHandPosLocal = Vector3.zero;
                Quaternion rightHandRotLocal = Quaternion.identity;

                if (headTransform != null)
                {
                    headPosLocal = Quaternion.Inverse(bodyTransform.rotation) * (headTransform.position - bodyTransform.position);
                    headRotLocal = Quaternion.Inverse(bodyTransform.rotation) * headTransform.rotation;
                }

                if (leftHandTransform != null)
                {
                    leftHandPosLocal = Quaternion.Inverse(bodyTransform.rotation) * (leftHandTransform.position - bodyTransform.position);
                    leftHandRotLocal = Quaternion.Inverse(bodyTransform.rotation) * leftHandTransform.rotation;
                }

                if (rightHandTransform != null)
                {
                    rightHandPosLocal = Quaternion.Inverse(bodyTransform.rotation) * (rightHandTransform.position - bodyTransform.position);
                    rightHandRotLocal = Quaternion.Inverse(bodyTransform.rotation) * rightHandTransform.rotation;
                }

                // Create keyframe
                Keyframe kf = new Keyframe
                {
                    time = Time.time - RecorderState.RecordingStartTime,
                    headPosLocal = headPosLocal,
                    headRotLocal = headRotLocal,
                    leftHandPosLocal = leftHandPosLocal,
                    leftHandRotLocal = leftHandRotLocal,
                    rightHandPosLocal = rightHandPosLocal,
                    rightHandRotLocal = rightHandRotLocal
                };

                RecorderState.Keyframes.Add(kf);
                RecorderState.NextRecordTime += RecorderState.RecordInterval;
            }
        }

        // --- Save keyframes to CustomEmote.cfg ---
        private static void SaveKeyframes()
        {
            if (RecorderState.Keyframes.Count == 0)
            {
                PoseRecorderPlugin.Log.LogWarning("No keyframes recorded");
                return;
            }

            // Build JSON string
            StringBuilder json = new StringBuilder();
            json.Append('[');
            for (int i = 0; i < RecorderState.Keyframes.Count; i++)
            {
                Keyframe kf = RecorderState.Keyframes[i];
                json.Append("{");
                json.Append($"\"time\":{kf.time.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                json.Append($"\"headPos\":[{kf.headPosLocal.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.headPosLocal.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.headPosLocal.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}],");
                json.Append($"\"headRot\":[{kf.headRotLocal.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.headRotLocal.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.headRotLocal.z.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.headRotLocal.w.ToString(System.Globalization.CultureInfo.InvariantCulture)}],");
                json.Append($"\"leftHandPos\":[{kf.leftHandPosLocal.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.leftHandPosLocal.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.leftHandPosLocal.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}],");
                json.Append($"\"leftHandRot\":[{kf.leftHandRotLocal.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.leftHandRotLocal.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.leftHandRotLocal.z.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.leftHandRotLocal.w.ToString(System.Globalization.CultureInfo.InvariantCulture)}],");
                json.Append($"\"rightHandPos\":[{kf.rightHandPosLocal.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.rightHandPosLocal.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.rightHandPosLocal.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}],");
                json.Append($"\"rightHandRot\":[{kf.rightHandRotLocal.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.rightHandRotLocal.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.rightHandRotLocal.z.ToString(System.Globalization.CultureInfo.InvariantCulture)},{kf.rightHandRotLocal.w.ToString(System.Globalization.CultureInfo.InvariantCulture)}]");
                json.Append('}');
                if (i < RecorderState.Keyframes.Count - 1)
                    json.Append(',');
            }
            json.Append(']');

            // Update the CustomEmote.cfg file, preserving the PlaybackMode.
            string configPath = Path.Combine(Paths.ConfigPath, "CustomEmote.cfg");
            try
            {
                string[] lines = File.ReadAllLines(configPath);
                StringBuilder newConfig = new StringBuilder();
                bool replaced = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("KeyframesJson ="))
                    {
                        newConfig.AppendLine($"KeyframesJson = {json}");
                        replaced = true;
                    }
                    else
                    {
                        newConfig.AppendLine(lines[i]);
                    }
                }

                if (!replaced)
                {
                    newConfig.Clear();
                    newConfig.AppendLine("[Emote]");
                    newConfig.AppendLine();
                    newConfig.AppendLine("## JSON array of keyframes. Each keyframe: {time, headPos, headRot, leftHandPos, leftHandRot, rightHandPos, rightHandRot}. Vectors as [x,y,z], quaternions as [x,y,z,w]. Time in seconds.");
                    newConfig.AppendLine("# Setting type: String");
                    newConfig.AppendLine("# Default value: ");
                    newConfig.AppendLine($"KeyframesJson = {json}");
                    newConfig.AppendLine();
                    newConfig.AppendLine("## Playback mode: Loop, PingPong, Single.");
                    newConfig.AppendLine("# Setting type: EmotePlaybackMode");
                    newConfig.AppendLine("# Default value: Loop");
                    newConfig.AppendLine("# Acceptable values: Loop, PingPong, Single");
                    newConfig.AppendLine("PlaybackMode = Loop");
                }

                File.WriteAllText(configPath, newConfig.ToString());
                PoseRecorderPlugin.Log.LogInfo($"Saved {RecorderState.Keyframes.Count} keyframes to CustomEmote.cfg");
            }
            catch (Exception ex)
            {
                PoseRecorderPlugin.Log.LogError($"Failed to save keyframes: {ex}");
            }
        }

        // Define a keyframe struct for recording
        public struct Keyframe
        {
            public float time;
            public Vector3 headPosLocal;
            public Quaternion headRotLocal;
            public Vector3 leftHandPosLocal;
            public Quaternion leftHandRotLocal;
            public Vector3 rightHandPosLocal;
            public Quaternion rightHandRotLocal;
        }
    }
}
