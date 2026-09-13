using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace HeadphoneSwitcher
{
    internal interface IAudioBackend
    {
        List<MMDevice> List(EDataFlow flow);
        MMDevice Default(EDataFlow flow, ERole role);
        void Set(string id, ERole role);
    }
    internal sealed class Resolution
    {
        public MMDevice Device;
        public string Method;
        public string Error;
    }
    internal static class DeviceResolver
    {
        public static string CoreName(string name)
        {
            // Only remove numbered endpoint prefixes at the start or inside a parenthesis.
            // Keep model numbers and all other distinguishing words intact.
            return Regex.Replace(Regex.Replace(name ?? "", @"(^|\()\s*\d+\s*-\s*", "$1"), @"\s+", " ").Trim();
        }
        public static Resolution Resolve(DeviceChoice choice, List<MMDevice> devices)
        {
            var exact = devices.FirstOrDefault(d => string.Equals(d.Id, choice.Id, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return new Resolution { Device = exact, Method = "saved ID" };
            var matches = devices.Where(d => string.Equals(d.Name, choice.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            string method = "exact name";
            if (matches.Count == 0)
            {
                matches = devices.Where(d => string.Equals(CoreName(d.Name), CoreName(choice.Name), StringComparison.OrdinalIgnoreCase)).ToList();
                method = "cleaned name";
            }
            if (matches.Count == 1) return new Resolution { Device = matches[0], Method = method };
            return new Resolution { Error = matches.Count > 1
                ? "More than one device matches '" + choice.Name + "'. Choose the correct device in setup."
                : "'" + choice.Name + "' is disconnected or unavailable. Connect it, then try again." };
        }
    }
    internal sealed class SwitchEngine
    {
        private readonly IAudioBackend audio;
        private readonly Action<string> log;
        private readonly Action<int> pause;
        public SwitchEngine(IAudioBackend audio, Action<string> log, Action<int> pause)
        { this.audio = audio; this.log = log; this.pause = pause; }
        public bool Heal(SwitcherConfig config)
        {
            var playback = audio.List(EDataFlow.eRender);
            var recording = audio.List(EDataFlow.eCapture);
            bool changed = false;
            foreach (Profile p in config.Profiles)
            {
                changed |= HealChoice(p.Name + " playback", p.Playback, playback);
                changed |= HealChoice(p.Name + " microphone", p.Recording, recording);
            }
            return changed;
        }
        private bool HealChoice(string label, DeviceChoice choice, List<MMDevice> devices)
        {
            Resolution match = DeviceResolver.Resolve(choice, devices);
            if (match.Device == null) { log(label + ": " + match.Error + " Saved ID: " + choice.Id); return false; }
            if (choice.Id == match.Device.Id && choice.Name == match.Device.Name) return false;
            log("Repaired " + label + " using " + match.Method + ": '" + choice.Name + "' [" + choice.Id + "] -> '" + match.Device.Name + "' [" + match.Device.Id + "].");
            choice.Id = match.Device.Id;
            choice.Name = match.Device.Name;
            return true;
        }
        public int Target(SwitcherConfig config)
        {
            MMDevice playback = audio.Default(EDataFlow.eRender, ERole.eConsole);
            MMDevice recording = audio.Default(EDataFlow.eCapture, ERole.eConsole);
            bool one = playback != null && recording != null && Same(playback.Id, config.Profiles[0].Playback.Id) && Same(recording.Id, config.Profiles[0].Recording.Id);
            bool two = playback != null && recording != null && Same(playback.Id, config.Profiles[1].Playback.Id) && Same(recording.Id, config.Profiles[1].Recording.Id);
            log("Current pair: playback=" + (playback == null ? "unavailable" : playback.Name + " [" + playback.Id + "]") + "; microphone=" + (recording == null ? "unavailable" : recording.Name + " [" + recording.Id + "]") + "; matched " + (one ? "profile 1" : two ? "profile 2" : "neither profile; selecting profile 1") + ".");
            return one ? 1 : 0;
        }
        public void Apply(Profile profile)
        {
            log("Requested profile '" + profile.Name + "': playback '" + profile.Playback.Name + "' [" + profile.Playback.Id + "]; microphone '" + profile.Recording.Name + "' [" + profile.Recording.Id + "].");
            // Re-enumerate immediately before changing anything. Only active devices of the correct flow qualify.
            Require(profile.Playback, EDataFlow.eRender);
            Require(profile.Recording, EDataFlow.eCapture);
            var previous = new string[2, 3];
            for (int f = 0; f < 2; f++) for (int r = 0; r < 3; r++)
            {
                MMDevice device = audio.Default((EDataFlow)f, (ERole)r);
                previous[f, r] = device == null ? null : device.Id;
            }
            try
            {
                for (int r = 0; r < 3; r++) audio.Set(profile.Playback.Id, (ERole)r);
                for (int r = 0; r < 3; r++) audio.Set(profile.Recording.Id, (ERole)r);
                if (!VerifyPair(profile)) throw new InvalidOperationException("Windows did not confirm the complete device pair.");
                log("Confirmed profile '" + profile.Name + "' for playback and microphone across all three Windows roles.");
            }
            catch (Exception original)
            {
                log("Switch failed after changes began: " + original.Message + " Restoring previous Windows defaults.");
                bool restored = true;
                for (int f = 0; f < 2; f++) for (int r = 0; r < 3; r++)
                {
                    string id = previous[f, r];
                    if (id == null) { log("No previous endpoint existed for " + (EDataFlow)f + "/" + (ERole)r + "."); continue; }
                    try { audio.Set(id, (ERole)r); }
                    catch (Exception ex) { restored = false; log("Restore failed for " + (EDataFlow)f + "/" + (ERole)r + " [" + id + "]: " + ex.Message); }
                }
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    bool confirmed = true;
                    for (int f = 0; f < 2; f++) for (int r = 0; r < 3; r++)
                    {
                        try { var d = audio.Default((EDataFlow)f, (ERole)r); string currentId = d == null ? null : d.Id; if (!Same(currentId, previous[f, r])) confirmed = false; }
                        catch { confirmed = false; }
                    }
                    if (confirmed) break;
                    if (attempt == 11) restored = false;
                    else pause(100);
                }
                log(restored ? "Previous defaults restored and verified." : "Previous defaults could not be fully restored. Open Windows sound settings or setup.");
                throw new InvalidOperationException(restored ? "The pair could not be switched. Your previous settings were restored." : "The pair could not be switched, and some previous settings could not be restored. Check your devices in setup.", original);
            }
        }
        private void Require(DeviceChoice choice, EDataFlow flow)
        {
            if (!audio.List(flow).Any(d => Same(d.Id, choice.Id)))
                throw new InvalidOperationException((flow == EDataFlow.eRender ? "Playback " : "Microphone ") + "'" + choice.Name + "' is unavailable. No devices were changed.");
        }
        private bool VerifyPair(Profile profile)
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                bool matches = true;
                for (int f = 0; f < 2; f++) for (int r = 0; r < 3; r++)
                {
                    var d = audio.Default((EDataFlow)f, (ERole)r);
                    if (d == null || !Same(d.Id, f == 0 ? profile.Playback.Id : profile.Recording.Id)) matches = false;
                }
                if (matches) return true;
                if (attempt != 14) pause(100);
            }
            return false;
        }
        private static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
