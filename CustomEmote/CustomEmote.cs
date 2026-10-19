using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using System.IO;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Photon.Pun;
using RecRoom.Players;
using UnityEngine;
using System.Globalization;

namespace CustomEmote
{
    [BepInPlugin("com.reccroom.customemote", "CustomEmote", "3.3.0")]
    public class CustomEmotePlugin : BasePlugin
    {
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        // Configuration
        internal static new ConfigFile Config;
        internal static ConfigEntry<string> KeyframesJson;
        internal static ConfigEntry<EmotePlaybackMode> PlaybackMode;

        /// <summary>
        /// Extra height (in meters) added ONLY to the network snapshot.
        /// Increase/decrease this if the hands appear too low/high for OTHER players.
        /// The local display uses GetEmoteLocalOffset unchanged.
        /// </summary>
        internal const float NETWORK_HEIGHT_OFFSET = 1.0f;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("CustomEmote v3.3 loading...");

            // Initialize configuration
            Config = new ConfigFile(Path.Combine(Paths.ConfigPath, "CustomEmote.cfg"), true);
            KeyframesJson = Config.Bind("Emote", "KeyframesJson", "", "JSON array of keyframes. Each keyframe: {time, headPos, headRot, leftHandPos, leftHandRot, rightHandPos, rightHandRot}. Vectors as [x,y,z], quaternions as [x,y,z,w]. Time in seconds.");
            PlaybackMode = Config.Bind("Emote", "PlaybackMode", EmotePlaybackMode.Loop, "Playback mode: Loop, PingPong, Single.");

            try
            {
                _harmony = new Harmony("com.reccroom.customemote");
                _harmony.PatchAll();
                Log.LogInfo("CustomEmote v3.3 patches applied. Press F9 to toggle.");
            }
            catch (Exception ex)
            {
                Log.LogError($"CustomEmote FAILED to patch: {ex}");
            }
        }

        public override bool Unload()
        {
            _harmony?.UnpatchSelf();
            return true;
        }

        internal enum EmotePlaybackMode
        {
            Loop,
            PingPong,
            Single
        }

    // ── Shared emote state ─────────────────────────────────────────────────
    internal static class EmoteState
    {
        public static bool Active { get => _active; set { _active = value; if (value) _activationTime = Time.time; else _activationTime = -1f; } }
        private static bool _active;
        private static bool _loggedError;
        internal static bool _loggedSnapshot;
        private static float _activationTime = -1f;

        // Head movement: side-to-side sway and yaw rotation (fallback procedural)
        private static readonly float HEAD_SWAY_AMPLITUDE = 5.10f; // meters left/right
        private static readonly float HEAD_SWAY_SPEED = 1.0f;      // cycles/sec
        private static readonly float HEAD_YAW_AMPLITUDE = 15.0f;  // degrees left/right
        private static readonly float HEAD_YAW_SPEED = 2.0f;       // cycles/sec
        // Additional minor motion for head
        private static readonly float HEAD_UPDOWN_AMPLITUDE = 0.02f; // meters
        private static readonly float HEAD_UPDOWN_SPEED = 3.0f;      // cycles/sec
        private static readonly float HEAD_PITCH_AMPLITUDE = 5.0f;   // degrees
        private static readonly float HEAD_PITCH_SPEED = 3.0f;       // cycles/sec
        // Fixed upward offset for head (to raise it relative to body)
        private const float HEAD_HEIGHT_OFFSET = 0.5f; // meters upward
        internal static readonly float BODY_OFFSET_UNDER_HEAD = 0.5f; // meters
        internal static readonly float TORSO_OFFSET_UNDER_HEAD = 0.0f; // meters

        // Keyframe animation
        private static List<Keyframe> _keyframes = new List<Keyframe>();
        private static float _duration = 0f;
        private static CustomEmotePlugin.EmotePlaybackMode _playbackMode = CustomEmotePlugin.EmotePlaybackMode.Loop;
        private static bool _hasKeyframes = false;

        public static void LogErrorOnce(Exception ex, string context)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                CustomEmotePlugin.Log.LogError($"CustomEmote error in {context}: {ex}");
            }
        }

        /// <summary>
        /// Load keyframes from plugin configuration.
        /// Called when the emote is activated.
        /// </summary>
        public static void LoadKeyframesFromConfig()
        {
            _keyframes.Clear();
            _hasKeyframes = false;

            string json = CustomEmotePlugin.KeyframesJson.Value;
            if (string.IsNullOrEmpty(json))
            {
                CustomEmotePlugin.Log.LogInfo("No keyframes configured, using procedural animation.");
                return;
            }

            try
            {
                // Remove whitespace and newlines for easier parsing.
                json = json.Trim();

                // Find the first '[' and last ']' to extract the JSON array.
                int start = json.IndexOf('[');
                int end = json.LastIndexOf(']');
                if (start < 0 || end < 0 || end <= start)
                {
                    CustomEmotePlugin.Log.LogError("Keyframes JSON must contain an array.");
                    return;
                }
                json = json.Substring(start, end - start + 1);

                if (!json.StartsWith("[") || !json.EndsWith("]"))
                {
                    CustomEmotePlugin.Log.LogError("Keyframes JSON must be an array.");
                    return;
                }
                string inner = json.Substring(1, json.Length - 2).Trim();
                if (inner.Length == 0)
                {
                    CustomEmotePlugin.Log.LogInfo("Empty keyframe array.");
                    return;
                }

                // Split into objects: we assume objects are separated by '}, {' but only at depth 0 of [] and {}.
                string[] objectStrings = SplitTopLevelByChar(inner, ',');
                foreach (string objStr in objectStrings)
                {
                    string cleaned = objStr.Trim();
                    // Ensure the object string starts with '{' and ends with '}'
                    if (!cleaned.StartsWith("{")) cleaned = "{" + cleaned;
                    if (!cleaned.EndsWith("}")) cleaned = cleaned + "}";

                    Keyframe kf = new Keyframe();
                    bool hasTime = false, hasHeadPos = false, hasHeadRot = false, hasLeftHandPos = false, hasLeftHandRot = false, hasRightHandPos = false, hasRightHandRot = false;

                    // Remove the outer braces of the object.
                    string objInner = cleaned.Substring(1, cleaned.Length - 2).Trim();
                    if (objInner.Length == 0)
                    {
                        CustomEmotePlugin.Log.LogWarning("Empty keyframe object.");
                        continue;
                    }

                    // Split into key-value pairs by commas at depth 0 (inside the object, we track [] and {} depth).
                    string[] pairs = SplitTopLevelByChar(objInner, ',');
                    foreach (string pair in pairs)
                    {
                        string trimmed = pair.Trim();
                        if (trimmed.Length == 0) continue;
                        string[] keyValue = trimmed.Split(new[] { ':' }, 2);
                        if (keyValue.Length != 2) continue;
                        string key = keyValue[0].Trim().Trim('\"');
                        string value = keyValue[1].Trim();

                        switch (key)
                        {
                            case "time":
                                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float time))
                                {
                                    kf.time = time;
                                    hasTime = true;
                                }
                                break;
                            case "headPos":
                                kf.headPosLocal = ParseVector3(value);
                                hasHeadPos = true;
                                break;
                            case "headRot":
                                kf.headRotLocal = ParseQuaternion(value);
                                hasHeadRot = true;
                                break;
                            case "leftHandPos":
                                kf.leftHandPosLocal = ParseVector3(value);
                                hasLeftHandPos = true;
                                break;
                            case "leftHandRot":
                                kf.leftHandRotLocal = ParseQuaternion(value);
                                hasLeftHandRot = true;
                                break;
                            case "rightHandPos":
                                kf.rightHandPosLocal = ParseVector3(value);
                                hasRightHandPos = true;
                                break;
                            case "rightHandRot":
                                kf.rightHandRotLocal = ParseQuaternion(value);
                                hasRightHandRot = true;
                                break;
                        }
                    }

                    // Check for missing fields
                    if (!hasTime || !hasHeadPos || !hasHeadRot || !hasLeftHandPos || !hasLeftHandRot || !hasRightHandPos || !hasRightHandRot)
                    {
                        string missing = "";
                        if (!hasTime) missing += "time ";
                        if (!hasHeadPos) missing += "headPos ";
                        if (!hasHeadRot) missing += "headRot ";
                        if (!hasLeftHandPos) missing += "leftHandPos ";
                        if (!hasLeftHandRot) missing += "leftHandRot ";
                        if (!hasRightHandPos) missing += "rightHandPos ";
                        if (!hasRightHandRot) missing += "rightHandRot ";
                        CustomEmotePlugin.Log.LogWarning($"Keyframe missing fields: {missing.Trim()}");
                        // We still add the keyframe, but missing fields are left at default (zero/identity).
                    }

                    _keyframes.Add(kf);
                }

                if (_keyframes.Count == 0)
                {
                    CustomEmotePlugin.Log.LogError("No valid keyframes parsed.");
                    return;
                }

                // Sort by time
                _keyframes.Sort((a, b) => a.time.CompareTo(b.time));
                // Ensure first keyframe at time 0? Not required, but we can shift.
                float minTime = _keyframes[0].time;
                if (minTime > 0f)
                {
                    // Shift all times so first starts at 0
                    for (int i = 0; i < _keyframes.Count; i++)
                    {
                        Keyframe kf = _keyframes[i];
                        kf.time -= minTime;
                        _keyframes[i] = kf;
                    }
                }
                else if (minTime < 0f)
                {
                    CustomEmotePlugin.Log.LogWarning("Keyframe time negative, shifting to start at 0.");
                    for (int i = 0; i < _keyframes.Count; i++)
                    {
                        Keyframe kf = _keyframes[i];
                        kf.time -= minTime;
                        _keyframes[i] = kf;
                    }
                }

                _duration = _keyframes[_keyframes.Count - 1].time;
                if (_duration <= 0f)
                {
                    _duration = 1f; // avoid division by zero
                    CustomEmotePlugin.Log.LogWarning("Duration zero, setting to 1 second.");
                }

                _playbackMode = CustomEmotePlugin.PlaybackMode.Value;
                _hasKeyframes = true;

                CustomEmotePlugin.Log.LogInfo($"Loaded {_keyframes.Count} keyframes, duration {_duration}s, mode {_playbackMode}.");
            }
            catch (Exception ex)
            {
                CustomEmotePlugin.Log.LogError($"Failed to parse keyframes: {ex}");
                _hasKeyframes = false;
            }
        }

        private static string[] SplitTopLevel(string str, string separator)
        {
            List<string> parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < str.Length; i++)
            {
                if (str[i] == '[' || str[i] == '{')
                    depth++;
                else if (str[i] == ']' || str[i] == '}')
                    depth--;
                else if (depth == 0 && i + separator.Length <= str.Length && str.Substring(i, separator.Length) == separator)
                {
                    parts.Add(str.Substring(start, i - start).Trim());
                    start = i + separator.Length;
                    i += separator.Length - 1; // because the for loop will increment i
                }
            }
            parts.Add(str.Substring(start).Trim());
            return parts.ToArray();
        }

        private static string[] SplitTopLevelByChar(string str, char separator)
        {
            List<string> parts = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < str.Length; i++)
            {
                if (str[i] == '[' || str[i] == '{')
                    depth++;
                else if (str[i] == ']' || str[i] == '}')
                    depth--;
                else if (depth == 0 && str[i] == separator)
                {
                    parts.Add(str.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(str.Substring(start).Trim());
            return parts.ToArray();
        }

        private static Vector3 ParseVector3(string value)
        {
            // Expected format: [x,y,z] or (x,y,z) or x,y,z
            value = value.Trim();
            if (value.StartsWith("[")) value = value.Substring(1);
            if (value.EndsWith("]")) value = value.Substring(0, value.Length - 1);
            if (value.StartsWith("(")) value = value.Substring(1);
            if (value.EndsWith(")")) value = value.Substring(0, value.Length - 1);

            string[] parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                CustomEmotePlugin.Log.LogError($"Invalid vector3: {value}");
                return Vector3.zero;
            }
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                return new Vector3(x, y, z);
            }
            CustomEmotePlugin.Log.LogError($"Failed to parse vector3: {value}");
            return Vector3.zero;
        }

        private static Quaternion ParseQuaternion(string value)
        {
            // Expected format: [x,y,z,w] or (x,y,z,w) or x,y,z,w
            value = value.Trim();
            if (value.StartsWith("[")) value = value.Substring(1);
            if (value.EndsWith("]")) value = value.Substring(0, value.Length - 1);
            if (value.StartsWith("(")) value = value.Substring(1);
            if (value.EndsWith(")")) value = value.Substring(0, value.Length - 1);

            string[] parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4)
            {
                CustomEmotePlugin.Log.LogError($"Invalid quaternion: {value}");
                return Quaternion.identity;
            }
            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
            {
                return new Quaternion(x, y, z, w);
            }
            CustomEmotePlugin.Log.LogError($"Failed to parse quaternion: {value}");
            return Quaternion.identity;
        }

        /// <summary>
        /// Get the animated pose at the given time in seconds since activation.
        /// Returns true if keyframes are used, false if fallback to procedural.
        /// </summary>
        public static bool TryGetAnimatedPose(float time, out Vector3 headPos, out Quaternion headRot, out Vector3 leftHandPos, out Quaternion leftHandRot, out Vector3 rightHandPos, out Quaternion rightHandRot)
        {
            headPos = Vector3.zero;
            headRot = Quaternion.identity;
            leftHandPos = Vector3.zero;
            leftHandRot = Quaternion.identity;
            rightHandPos = Vector3.zero;
            rightHandRot = Quaternion.identity;

            if (!_hasKeyframes || _keyframes.Count == 0)
                return false;

            float t = time;
            switch (_playbackMode)
            {
                case CustomEmotePlugin.EmotePlaybackMode.Loop:
                    t = Mathf.Repeat(t, _duration);
                    break;
                case CustomEmotePlugin.EmotePlaybackMode.PingPong:
                    t = Mathf.PingPong(t, _duration);
                    break;
                case CustomEmotePlugin.EmotePlaybackMode.Single:
                    t = Mathf.Clamp(t, 0f, _duration);
                    break;
            }

            // Find the segment we're in
            int index = 0;
            while (index < _keyframes.Count - 1 && _keyframes[index + 1].time < t)
                index++;

            if (index < 0) index = 0;
            if (index >= _keyframes.Count - 1)
            {
                // Use the last keyframe
                Keyframe kf = _keyframes[_keyframes.Count - 1];
                headPos = kf.headPosLocal;
                headRot = kf.headRotLocal;
                leftHandPos = kf.leftHandPosLocal;
                leftHandRot = kf.leftHandRotLocal;
                rightHandPos = kf.rightHandPosLocal;
                rightHandRot = kf.rightHandRotLocal;
                return true;
            }

            // Interpolate between keyframes[index] and keyframes[index+1]
            Keyframe kf1 = _keyframes[index];
            Keyframe kf2 = _keyframes[index + 1];
            float segmentTime = kf2.time - kf1.time;
            if (segmentTime <= 0f)
                segmentTime = 1f;
            float lerp = (t - kf1.time) / segmentTime;

            headPos = Vector3.Lerp(kf1.headPosLocal, kf2.headPosLocal, lerp);
            headRot = Quaternion.Slerp(kf1.headRotLocal, kf2.headRotLocal, lerp);
            leftHandPos = Vector3.Lerp(kf1.leftHandPosLocal, kf2.leftHandPosLocal, lerp);
            leftHandRot = Quaternion.Slerp(kf1.leftHandRotLocal, kf2.leftHandRotLocal, lerp);
            rightHandPos = Vector3.Lerp(kf1.rightHandPosLocal, kf2.rightHandPosLocal, lerp);
            rightHandRot = Quaternion.Slerp(kf1.rightHandRotLocal, kf2.rightHandRotLocal, lerp);

            return true;
        }


        /// <summary>
        /// The emote offset in BODY-LOCAL space (X=right, Y=up, Z=forward).
        /// This is what the network snapshot should contain — the remote client
        /// will apply the body's rotation through the parent hierarchy.
        /// </summary>
        public static Vector3 GetEmoteLocalOffset(bool isLeft)
        {
            if (!Active || !_hasKeyframes)
            {
                // Fallback to original procedural
                float t = Time.time;
                float phase = isLeft ? 0f : Mathf.PI;
                float wave = Mathf.Sin(t * 6.0f + phase);
                float bob  = Mathf.Sin(t * 3.0f + phase);

                float sideX = isLeft ? -0.30f : 0.30f;

                return new Vector3(
                    sideX + 0.08f * wave,
                    0.50f + 0.15f * bob,
                    0.0f + 0.04f * Mathf.Cos(t * 3.0f + phase)
                );
            }

            // We have keyframes and the emote is active
            if (TryGetAnimatedPose(Time.time - _activationTime, out Vector3 headPos, out Quaternion headRot, out Vector3 leftHandPos, out Quaternion leftHandRot, out Vector3 rightHandPos, out Quaternion rightHandRot))
            {
                return isLeft ? leftHandPos : rightHandPos;
            }
            else
            {
                // Fallback to original procedural (should not happen if we have keyframes, but safe)
                float t = Time.time;
                float phase = isLeft ? 0f : Mathf.PI;
                float wave = Mathf.Sin(t * 6.0f + phase);
                float bob  = Mathf.Sin(t * 3.0f + phase);

                float sideX = isLeft ? -0.30f : 0.30f;

                return new Vector3(
                    sideX + 0.08f * wave,
                    0.50f + 0.15f * bob,
                    0.0f + 0.04f * Mathf.Cos(t * 3.0f + phase)
                );
            }
        }

        /// <summary>
        /// The emote rotation in BODY-LOCAL space (Euler angles relative to body).
        /// The remote client will apply the body's rotation through the hierarchy.
        /// </summary>
        public static Quaternion GetEmoteLocalRot(bool isLeft)
        {
            if (!Active || !_hasKeyframes)
            {
                // Fallback to original procedural (no rotation)
                return Quaternion.identity;
            }

            // We have keyframes and the emote is active
            if (TryGetAnimatedPose(Time.time - _activationTime, out Vector3 headPos, out Quaternion headRot, out Vector3 leftHandPos, out Quaternion leftHandRot, out Vector3 rightHandPos, out Quaternion rightHandRot))
            {
                return isLeft ? leftHandRot : rightHandRot;
            }
            else
            {
                // Fallback to original procedural (should not happen if we have keyframes, but safe)
                return Quaternion.identity;
            }
        }

        // The Keyframe struct is defined below.
        private struct Keyframe
        {
            public float time;
            public Vector3 headPosLocal;
            public Quaternion headRotLocal;
            public Vector3 leftHandPosLocal;
            public Quaternion leftHandRotLocal;
            public Vector3 rightHandPosLocal;
            public Quaternion rightHandRotLocal;

            public Keyframe()
            {
                headRotLocal = Quaternion.identity;
                leftHandRotLocal = Quaternion.identity;
                rightHandRotLocal = Quaternion.identity;
            }
        }
        public static Vector3 GetHeadEmoteLocalOffset()
        {
            if (Active && _hasKeyframes &&
                TryGetAnimatedPose(Time.time - _activationTime, out Vector3 headPos, out _, out _, out _, out _, out _))
                return headPos;
            return Vector3.zero;
        }

        public static Quaternion GetHeadEmoteLocalRot()
        {
            if (Active && _hasKeyframes &&
                TryGetAnimatedPose(Time.time - _activationTime, out _, out Quaternion headRot, out _, out _, out _, out _))
                return headRot;
            return Quaternion.identity;
        }
        public static Vector3 GetEmoteWorldPos(Vector3 bodyPos, Quaternion bodyRot, Vector3 localOffset)
        {
            return bodyPos + bodyRot * localOffset;
        }
        public static Quaternion GetEmoteWorldRot(Quaternion bodyRot, Quaternion localRot)
        {
            return bodyRot * localRot;
        }
        public static bool SamePtr(object a, object b) => ReferenceEquals(a, b);
    }

    // ── F9 toggle ─────────────────────────────────────────────────────
    [HarmonyPatch(typeof(RecRoomSceneManager), "Update")]
    internal static class TogglePatch
    {
        private static void Postfix()
        {
            if (!Input.GetKeyDown(KeyCode.F9)) return;

            // Re-read CustomEmote.cfg from disk so edits made while the game is running are picked up
            try
            {
                CustomEmotePlugin.Config.Reload();
            }
            catch (Exception ex)
            {
                CustomEmotePlugin.Log.LogError($"Config reload failed: {ex}");
            }

            EmoteState.Active = !EmoteState.Active;
            if (EmoteState.Active)
            {
                EmoteState.LoadKeyframesFromConfig();
            }
            CustomEmotePlugin.Log.LogInfo(
                EmoteState.Active ? "Custom emote ACTIVATED (F9)" : "Custom emote DEACTIVATED (F9)");
        }
    }

    // ── Local display: PlayerBody.LateUpdate Postfix ─────────────────────
    // Sets hand transforms to the emote pose AFTER the game's animator runs.
    // Also applies head movement (world-space) and positions torso under head with fixed offset.
    // Uses world-space positions (Unity handles world→local internally) for hands and head.
    [HarmonyPatch(typeof(PlayerBody), "LateUpdate")]
    internal static class PlayerBodyLateUpdatePatch
    {
        private static void Postfix(PlayerBody __instance)
        {
            try
            {
                if (!EmoteState.Active) return;

                Player localPlayer = Player.MDMMDPEKICF;
                if (localPlayer == null) return;
                if (!EmoteState.SamePtr(localPlayer.body, __instance)) return;

                // --- Get body transform (current from game) ---
                Vector3 bodyPos = __instance.transform.position;
                Quaternion bodyRot = __instance.transform.rotation;

                // --- Head emote: set head world position to emote pose ---
                Vector3 headEmoteOffset = EmoteState.GetHeadEmoteLocalOffset();
                Quaternion headEmoteRot = EmoteState.GetHeadEmoteLocalRot();
                Vector3 headWorldPos = bodyPos + bodyRot * headEmoteOffset;
                Quaternion headWorldRot = bodyRot * headEmoteRot;
                if (localPlayer.head != null)
                {
                    Transform headTrans = localPlayer.head.transform;
                    headTrans.position = headWorldPos;
                    headTrans.rotation = headWorldRot;
                }

                // --- Body position: place body under head with fixed offset ---
                // We want the body to be below the head by BODY_OFFSET_UNDER_HEAD along the head's local up axis.
                // Assuming the head's local up axis is (0,1,0) in its local space.
                Vector3 bodyOffset = new Vector3(0f, EmoteState.BODY_OFFSET_UNDER_HEAD, 0f); // meters upward in head's local space
                Vector3 bodyToHead = - (headWorldRot * bodyOffset);
                Vector3 newBodyPos = headWorldPos + bodyToHead;
                // Note: We do *not* change the body rotation here to avoid
                // interfering with the character controller’s facing direction.

                // --- Hand emote (world-space) ---
                PlayerHand left = localPlayer.leftHand;
                PlayerHand right = localPlayer.rightHand;

                if (left == null || right == null) return;
                // Use the updated body position and rotation (after we set torso position)
                Vector3 bodyPos2 = __instance.transform.position;
                Quaternion bodyRot2 = __instance.transform.rotation;

                // Left hand
                Vector3 leftHandLocalOffset = EmoteState.GetEmoteLocalOffset(true)
                    + new Vector3(0f, EmoteState.TORSO_OFFSET_UNDER_HEAD, 0f);
                left.transform.position  = EmoteState.GetEmoteWorldPos(bodyPos2, bodyRot2, leftHandLocalOffset);
                left.transform.rotation  = EmoteState.GetEmoteWorldRot(bodyRot2, EmoteState.GetEmoteLocalRot(true));

                // Right hand
                Vector3 rightHandLocalOffset = EmoteState.GetEmoteLocalOffset(false)
                    + new Vector3(0f, EmoteState.TORSO_OFFSET_UNDER_HEAD, 0f);
                right.transform.position = EmoteState.GetEmoteWorldPos(bodyPos2, bodyRot2, rightHandLocalOffset);
                right.transform.rotation = EmoteState.GetEmoteWorldRot(bodyRot2, EmoteState.GetEmoteLocalRot(false));

                __instance.transform.position = newBodyPos;
            }
            catch (Exception ex)
            {
                EmoteState.LogErrorOnce(ex, "PlayerBodyLateUpdate");
            }
        }
    }

    // ── Network sync: NetworkTransform.OnPhotonSerializeView Postfix ─────
    // The PDICIMDPPII snapshot in the stream carries hand/head pos/rot to others.
    //
    // KEY INSIGHT: The remote client applies the snapshot as a LOCAL position
    // relative to the hand's/head's parent in the avatar hierarchy. That parent
    // already has the body's rotation in its transform chain. So we must send
    // the BODY-LOCAL offset (WITHOUT body rotation), because the remote will
    // add the body rotation through the hierarchy.
    //
    // If we sent bodyRot * offset (world-rotated), the remote would apply
    // bodyRot again through the parent → DOUBLED rotation/position.
    [HarmonyPatch(typeof(NetworkTransform), "OnPhotonSerializeView")]
    internal static class NetworkTransformSerializePatch
    {
        private static void Postfix(NetworkTransform __instance, CIJGCGJKPEN DMMGOMOKEKK)
        {
            try
            {
                if (!EmoteState.Active) return;

                Player local = Player.MDMMDPEKICF;
                if (local == null) return;

                PlayerHand leftHand = local.leftHand;
                PlayerHand rightHand = local.rightHand;
                PlayerHead head = local.head;

                NetworkTransform leftNt  = leftHand?._networkTransform;
                NetworkTransform rightNt = rightHand?._networkTransform;
                NetworkTransform headNt  = head?.networkTransform; // or IFKEHHEEKLE

                bool isLeft  = EmoteState.SamePtr(leftNt, __instance);
                bool isRight = EmoteState.SamePtr(rightNt, __instance);
                bool isHead  = EmoteState.SamePtr(headNt, __instance);
                if (!isLeft && !isRight && !isHead) return;

                bool isWriting = DMMGOMOKEKK._AHCOKGFADIG_k__BackingField;
                if (!isWriting) return;

                var list = DMMGOMOKEKK.NJKMIECLPMP;
                if (list == null) return;
                int count = list.Count;
                if (count == 0) return;

                // Write the BODY-LOCAL offset and rotation directly.
                // The remote client's parent hierarchy will apply bodyRot.
                Vector3 localOffset = Vector3.zero;
                Quaternion localRot = Quaternion.identity;

                if (isLeft || isRight)
                {
                    bool isLeftHand = isLeft;
                    // Adjusted hand local offset: compensate for torso following head
                    Vector3 handLocalOffset = EmoteState.GetEmoteLocalOffset(isLeftHand)
                        + new Vector3(0f, CustomEmotePlugin.NETWORK_HEIGHT_OFFSET, 0f); // Note: we keep the NETWORK_HEIGHT_OFFSET for the snapshot as before
                    Quaternion handLocalRot = EmoteState.GetEmoteLocalRot(isLeftHand);
                    localOffset = handLocalOffset;
                    localRot = handLocalRot;
                }
                else if (isHead)
                {
                    // For head, we treat it like an extra hand: use body-local offset from EmoteState head functions
                    Vector3 headLocalOffset = EmoteState.GetHeadEmoteLocalOffset()
                        + new Vector3(0f, CustomEmotePlugin.NETWORK_HEIGHT_OFFSET, 0f); // same height offset as hands?
                    Quaternion headLocalRot = EmoteState.GetHeadEmoteLocalRot();
                    localOffset = headLocalOffset;
                    localRot = headLocalRot;
                }

                IntPtr snapshotClass = Il2CppClassPointerStore<NetworkTransform.PDICIMDPPII>.NativeClassPtr;

                for (int i = 0; i < count; i++)
                {
                    var item = list[i];
                    if (item == null) continue;

                    IntPtr itemPtr = IL2CPP.Il2CppObjectBaseToPtr((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)item);
                    if (itemPtr == IntPtr.Zero) continue;

                    IntPtr itemClass = IL2CPP.il2cpp_object_get_class(itemPtr);
                    if (itemClass == snapshotClass)
                    {
                        var snapshot = new NetworkTransform.PDICIMDPPII(itemPtr);
                        snapshot.EPKNJJIGHLN = localOffset;
                        snapshot.OKINPJLFKEJ = localRot;

                        if (!EmoteState._loggedSnapshot)
                        {
                            EmoteState._loggedSnapshot = true;
                            string part = isLeft ? "LEFT" : (isRight ? "RIGHT" : "HEAD");
                            CustomEmotePlugin.Log.LogInfo(
                                $"SNAPSHOT ({part}): localOffset={localOffset}, localRot={localRot.eulerAngles}");
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                EmoteState.LogErrorOnce(ex, "NetworkTransformSerialize");
            }
        }
    }
}
}
