namespace Site12.API.Features.OtherStaff
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommandSystem;
using CustomPlayerEffects;
using Exiled.API.Features; // kept for Paths (audio folder)
using Extensions;
using LabApi.Features.Wrappers;
using MEC;
using ProjectMER.Features;
using ProjectMER.Features.Objects;
using UnityEngine;
using PlayerRoles;
using Logger = LabApi.Features.Console.Logger;
using Player = LabApi.Features.Wrappers.Player;
using Room = LabApi.Features.Wrappers.Room;
using Interactables.Interobjects;
using MapGeneration;
using ElevatorDoor = Interactables.Interobjects.ElevatorDoor;

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    public class RealisticWarhead : ICommand
    {
        public string Command => "purge";
        public string[] Aliases { get; } = ["pg"];
        public string Description => "A custom-made Warhead and Decontamination controller.";
        // --- Timers / coroutines ---
        private static CoroutineHandle _alarmLightHandle;
        private static CoroutineHandle _warheadCountdownHandle;
        private static CoroutineHandle _evacWatcher;
        // --- Configurable timings ---
        private const float AlarmCycleSeconds = 2f;
        private const float AlphaCountdownSeconds = 90f; // TODO: Needs to be changed for Adlai's music
        private const float OmegaCountdownSeconds = 300f;
        private const float DeconCountdownSeconds = 180f;
        // --- Audio  ---
        private const string PurgeAudioPlayerName = "PURGE_AUDIO";
        private const string PurgeSpeakerName = "PURGE_SPEAKER";
        // --- Evac ---
        private static SchematicObject _evacSchematic;
        private static readonly HashSet<Player> _evacProcessed = new();
        private const string EvacSchematicName = "KFCHeliEscape";
        private static readonly Vector3 HelipadPosition = new(125f, 295f, -43f);
        private const float EvacRadius = 4.5f; // TODO: Change later
        // --- State ---
        private static string _activeWarheadType = null;
        private static bool _warheadIsActive = false;
        // --- Zone-scoped decon countdowns ---
        private static readonly Dictionary<FacilityZone, CoroutineHandle> DeconCountdownHandles = new();
        // --- Colors for site-wide / zone lighting ---
        private static readonly Dictionary<string, Color> AlarmColorMap =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["red"]    = Color.red,
                ["purple"] = new(0.6f, 0.2f, 0.8f),
            };
        // ======== helpers (small, reuse) ========
        private static string BuildRightHint(string title, string time, string line3, string codeLabel, string codeHex) =>
            "<align=right>"
          + $"<size=36><b>{title}</b></size>\n"
          + $"<size=28><color={codeHex}>{time}</color></size>\n"
          + $"<size=26>{line3}</size>\n"
          + $"<size=26><b><color={codeHex}>{codeLabel}</color></b></size>\n"
          + $"<size=26><i>Evacuate immediately!</i></size>"
          + "</align>";
        
        private static void BroadcastRightHint(string text, float duration, Predicate<Player> filter = null)
        {
            foreach (var p in Player.ReadyList)
                if (filter == null || filter(p))
                    p.SendHint(text, duration);
        }
        private static string FormatCountdown(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}:{ts.Milliseconds:000}";
        }
        // ---------------- Command entry ----------------
        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            response = "You do not have permissions to run this command.";
            if (!sender.CheckRemoteAdmin(out response))
                return false;
            string usage = string.Join("\n", new[]
            {
                "-- Warhead Purge --",
                "Usage: purge warhead arm <alpha|omega>",
                "Usage: purge warhead cancel",
                "Usage: purge warhead list",
                "-- Decontamination Purge --",
                "Usage: purge decon start <lcz|hcz|ez|all>",
                "Usage: purge decon cancel",
                "",
                "Alpha Warhead detonates the Surface Zone in T-90s.",
                "Omega Warhead detonates the entire facility in T-300s."
            });
            if (arguments.Count == 0)
            {
                response = usage;
                return false;
            }
            string sub = arguments.At(0)?.ToLowerInvariant();
            if (sub == "warhead")
            {
                if (arguments.Count == 1)
                {
                    response = usage;
                    return false;
                }
                string arg2 = arguments.At(1)?.ToLowerInvariant();
                switch (arg2)
                {
                    case "arm":
                    {
                        if (arguments.Count == 2)
                        {
                            response = usage;
                            return false;
                        }
                        if (_warheadIsActive)
                        {
                            response = "Warhead Purge is already active.";
                            return false;
                        }
                        string kind = arguments.At(2)?.ToLowerInvariant();
                        if (kind == "alpha" || kind == "omega")
                        {
                            StartWarhead(kind);
                            PlayPurgeAudio(kind == "alpha" ? "KFCAlphaWarhead" : "KFCOmegaWarhead");
                            StartAlarmLightBreathing(AlarmColorMap["red"], "all");
                            _warheadIsActive = true;
                            response = $"{(kind == "alpha" ? "Alpha" : "Omega")} Warhead Purge Started";
                            return true;
                        }
                        response = $"'{arguments.At(2)}' does not exist.";
                        return false;
                    }
                    case "cancel":
                    {
                        if (_warheadIsActive)
                        {
                            CancelWarhead();
                            StopAlarmLight();
                            StopPurgeAudio();
                            _warheadIsActive = false;
                            response = "Warhead Purge Cancelled";
                            return true;
                        }
                        response = "No Warhead Purge is active.";
                        return false;
                    }
                    case "list":
                        response = "Warhead Purge List\nAlpha Warhead - detonates Surface Zone - T-90s\nOmega Warhead - detonates entire Facility - T-300s";
                        return true;
                    default:
                        response = usage;
                        return false;
                }
            }
            else if (sub == "decon")
            {
                if (arguments.Count == 1)
                {
                    response = usage;
                    return false;
                }
                string arg2 = arguments.At(1)?.ToLowerInvariant();
                switch (arg2)
                {
                    case "start":
                    {
                        if (arguments.Count == 2)
                        {
                            response = usage;
                            return false;
                        }
                        string scope = arguments.At(2)?.ToLowerInvariant();
                        switch (scope)
                        {
                            case "lcz": StartAlarmLightBreathing(AlarmColorMap["purple"], "lcz"); StartDecon("lcz"); response = "LCZ Decontamination Purge Started"; return true;
                            case "hcz": StartAlarmLightBreathing(AlarmColorMap["purple"], "hcz"); StartDecon("hcz"); response = "HCZ Decontamination Purge Started"; return true;
                            case "ez":  StartAlarmLightBreathing(AlarmColorMap["purple"], "ez");  StartDecon("ez");  response = "EZ Decontamination Purge Started";  return true;
                            case "all": StartAlarmLightBreathing(AlarmColorMap["purple"], "all"); StartDecon("all"); response = "Site-Wide Decontamination Purges Started"; return true;
                            default: response = $"'{scope}' does not exist."; return false;
                        }
                    }
                    case "cancel":
                        CancelDecon(); StopAlarmLight(); response = "Decontamination Purge Cancelled"; return true;
                    default:
                        response = usage; return false;
                }
            }
            response = usage;
            return false;
        }
        // --------------- Warhead ---------------
        private static void StartWarhead(string kind)
        {
            if (_warheadCountdownHandle.IsRunning)
                Timing.KillCoroutines(_warheadCountdownHandle);
            _activeWarheadType = kind;
            if (kind == "omega") PrepareOmegaLockdown();
            _warheadCountdownHandle = Timing.RunCoroutine(
                WarheadCountdownCoroutine(kind == "alpha" ? "ALPHA WARHEAD" : "OMEGA WARHEAD", kind == "alpha" ? AlphaCountdownSeconds : OmegaCountdownSeconds));
            SpawnEvacZone();
        }
        private static void CancelWarhead()
        {
            if (_warheadCountdownHandle.IsRunning)
                Timing.KillCoroutines(_warheadCountdownHandle);
            if (_activeWarheadType == "omega")
                ReleaseOmegaLocks();
            DespawnEvacZone();
            _activeWarheadType = null;
        }
        private static IEnumerator<float> WarheadCountdownCoroutine(string title, float totalSeconds)
        {
            double remaining = totalSeconds;
            double last = Time.timeAsDouble;
            bool isAlpha = title.IndexOf("ALPHA", StringComparison.OrdinalIgnoreCase) >= 0;
            string target = isAlpha ? "Surface Zone" : "entire Facility";
            bool evacHidden = false;
            double hideAt = isAlpha ? 30.0 : 90.0;
            while (remaining > 0.0)
            {
                double now = Time.timeAsDouble;
                remaining -= (now - last);
                last = now;
                if (!evacHidden && remaining <= hideAt)
                {
                    DespawnEvacZone();
                    evacHidden = true;
                }
                string text = BuildRightHint(
                    title,
                    FormatCountdown(remaining),
                    $"The {target} will be detonated!",
                    "<#66271D>Code BLACK",
                    "#FFD400");
                BroadcastRightHint(text, 0.35f);
                yield return Timing.WaitForSeconds(0.05f);
            }
            _warheadIsActive = false;
            _activeWarheadType = null;
            if (isAlpha) DetonateAlpha();
            else DetonateOmega();
        }
        private static void DetonateAlpha()
        {
            foreach (var p in Player.List)
            {
                var room = p.Room;
                if (room != null && room.Zone == FacilityZone.Surface)
                    p.Kill("Alpha Warhead detonation");
            }
            foreach (var e in Elevator.List.Where(e => e.Group == ElevatorGroup.GateA || e.Group == ElevatorGroup.GateB))
            {
                e.DynamicAdminLock = true;
                foreach (var d in e.Doors) { d.IsOpened = false; d.IsLocked = true; }
            }
            StopAlarmLight();
        }
        private static void DetonateOmega()
        {
            foreach (var p in Player.List)
                p.Kill("Omega Warhead detonation");
            StopAlarmLight();
        }
        // ---------------- Decontamination ----------------
        private static void StartDecon(string scope)
        {
            static void Start(FacilityZone z, float secs) =>
                DeconCountdownHandles[z] = (DeconCountdownHandles.TryGetValue(z, out var h) && h.IsRunning ? (Timing.KillCoroutines(h), Timing.RunCoroutine(DeconCountdownCoroutine(z, secs))).Item2 : Timing.RunCoroutine(DeconCountdownCoroutine(z, secs)));
            switch (scope)
            {
                case "lcz": Start(FacilityZone.LightContainment, DeconCountdownSeconds); break;
                case "hcz": Start(FacilityZone.HeavyContainment, DeconCountdownSeconds); break;
                case "ez":  Start(FacilityZone.Entrance, DeconCountdownSeconds); break;
                case "all":
                    Start(FacilityZone.LightContainment, DeconCountdownSeconds);
                    Start(FacilityZone.HeavyContainment, DeconCountdownSeconds);
                    Start(FacilityZone.Entrance, DeconCountdownSeconds);
                    break;
            }
        }
        private static void CancelDecon()
        {
            foreach (var h in DeconCountdownHandles.Values)
                if (h.IsRunning) Timing.KillCoroutines(h);
            DeconCountdownHandles.Clear();
        }
        private static IEnumerator<float> DeconCountdownCoroutine(FacilityZone zone, float totalSeconds)
        {
            string zoneLabel = zone switch
            {
                FacilityZone.LightContainment => "Light Containment Zone",
                FacilityZone.HeavyContainment => "Heavy Containment Zone",
                FacilityZone.Entrance => "Entrance Zone",
                _ => "The Underground Facility",
            };
            double remaining = totalSeconds;
            double last = Time.timeAsDouble;
            while (remaining > 0.0)
            {
                double now = Time.timeAsDouble;
                remaining -= (now - last);
                last = now;

                string text = BuildRightHint(
                    $"{zoneLabel} DECONTAMINATION",
                    FormatCountdown(remaining),
                    $"{zoneLabel} will be purged!",
                    "Code PURPLE",
                    "#B972FF");
                BroadcastRightHint(text, 0.3f, p => p.Room != null && p.Room.Zone == zone);
                yield return 0;
            }
            foreach (var p in Player.List)
            {
                var room = p.Room;
                if (room != null && room.Zone == zone)
                    p.EnableEffect<Decontaminating>(255, 999f, true);
            }
        }
        // ---------------- Omega lockdown helpers ----------------
        private static void PrepareOmegaLockdown()
        {
            foreach (var door in Door.List)
            {
                if (door.Base is ElevatorDoor)
                    continue;
                door.IsOpened = true;
                door.IsLocked = true;
            }
        }
        private static void ReleaseOmegaLocks()
        {
            foreach (var door in Door.List)
            {
                if (door.Base is ElevatorDoor)
                    continue;
                door.IsLocked = false;
            }
        }
        // ---------------- Site lighting ----------------
        private static void StartAlarmLightBreathing(Color baseColor, string zoneKey)
        {
            if (_alarmLightHandle.IsRunning)
                Timing.KillCoroutines(_alarmLightHandle);
            
            string affected = zoneKey is "lcz" or "hcz" or "ez" ? zoneKey : "all";
            _alarmLightHandle = Timing.RunCoroutine(AlarmLightBreathing(baseColor, AlarmCycleSeconds, affected));
        }
        private static void StopAlarmLight()
        {
            if (_alarmLightHandle.IsRunning)
                Timing.KillCoroutines(_alarmLightHandle);

            foreach (var room in Room.List)
                room.LightController.OverrideLightsColor = Color.black;
        }
        private static IEnumerator<float> AlarmLightBreathing(Color baseColor, float period, string affectedZoneKey)
        {
            bool affectAll = affectedZoneKey == "all";
            FacilityZone targetZone = affectedZoneKey switch
            {
                "lcz" => FacilityZone.LightContainment,
                "hcz" => FacilityZone.HeavyContainment,
                "ez" => FacilityZone.Entrance,
                _ => FacilityZone.Surface,
            };
            float elapsed = 0f;
            float half = Mathf.Max(0.1f, period / 2f);

            while (true)
            {
                elapsed += Timing.DeltaTime;
                float t = Mathf.PingPong(elapsed, half) / half;
                float eased = Mathf.SmoothStep(0f, 1f, t);
                Color c = new(baseColor.r * eased, baseColor.g * eased, baseColor.b * eased, 1f);

                foreach (var room in Room.List)
                    if (affectAll || room.Zone == targetZone)
                        room.LightController.OverrideLightsColor = c;

                yield return Timing.WaitForSeconds(0.05f);
            }
        }
        // ---------------- Audio ----------------
        private static void PlayPurgeAudio(string clipKey, bool loop = false, bool isSpatial = false, float maxDistance = 4000f, float minDistance = 1f, Vector3? position = null)
        {
            StopPurgeAudio();

            string oggPath = Path.Combine(Paths.Plugins, "audio", $"{clipKey}.ogg");
            if (!AudioClipStorage.AudioClips.ContainsKey(clipKey))
            {
                if (!File.Exists(oggPath))
                {
                    Logger.Warn($"Purge audio file not found: {oggPath}");
                    return;
                }
                AudioClipStorage.LoadClip(oggPath, clipKey);
            }
            var audioPlayer = AudioPlayer.CreateOrGet(PurgeAudioPlayerName, destroyWhenAllClipsPlayed: false, controllerId: SpeakerExtensions.GetFreeId());
            if (audioPlayer == null)
            {
                Logger.Error("Failed to create or get purge AudioPlayer.");
                return;
            }
            try { audioPlayer.RemoveSpeaker(PurgeSpeakerName); } catch { /* ignore */ }
            var pos = position ?? Vector3.zero;
            audioPlayer.AddSpeaker(PurgeSpeakerName, pos, isSpatial: isSpatial, maxDistance: maxDistance, minDistance: minDistance);
            audioPlayer.AddClip(clipKey, loop: loop);
        }
        private static void StopPurgeAudio()
        {
            if (AudioPlayer.AudioPlayerByName.TryGetValue(PurgeAudioPlayerName, out var audioPlayer))
            {
                try { audioPlayer.RemoveSpeaker(PurgeSpeakerName); } catch { /* speaker may not exist */ }
                audioPlayer.Destroy();
            }
        }
        // ---------------- evac zone ----------------
        private static void SpawnEvacZone()
        {
            DespawnEvacZone();
            _evacSchematic = ObjectSpawner.SpawnSchematic(EvacSchematicName, HelipadPosition, Vector3.zero, Vector3.one);
            if (_evacSchematic == null)
            {
                Logger.Error($"[Purge] Failed to spawn schematic '{EvacSchematicName}' at {HelipadPosition}.");
                return;
            }
            _evacProcessed.Clear();
            _evacWatcher = Timing.RunCoroutine(EvacZoneWatcher());
        }
        private static void DespawnEvacZone()
        {
            if (_evacWatcher.IsRunning)
                Timing.KillCoroutines(_evacWatcher);
            if (_evacSchematic != null)
            {
                UnityEngine.Object.Destroy(_evacSchematic.gameObject);
                _evacSchematic = null;
            }
            _evacProcessed.Clear();
        }
        private static IEnumerator<float> EvacZoneWatcher()
        {
            while (_evacSchematic != null)
            {
                var center = _evacSchematic.transform.position;
                float r2 = EvacRadius * EvacRadius;
                foreach (var p in Player.List)
                {
                    if (!p.IsAlive) continue;
                    if (_evacProcessed.Contains(p)) continue;

                    if ((p.Position - center).sqrMagnitude <= r2)
                    {
                        _evacProcessed.Add(p);
                        p.ClearInventory();
                        p.Role = RoleTypeId.Spectator;
                    }
                }
                yield return Timing.WaitForSeconds(0.05f);
            }
        }
    }
}