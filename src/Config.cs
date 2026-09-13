using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HeadphoneSwitcher
{
    internal sealed class DeviceChoice
    {
        public string Id;
        public string Name;
        public DeviceChoice(string id, string name) { Id = id; Name = name; }
        public DeviceChoice Copy() { return new DeviceChoice(Id, Name); }
        public bool Same(DeviceChoice other) { return other != null && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase); }
    }
    internal sealed class Profile
    {
        public string Name;
        public DeviceChoice Playback;
        public DeviceChoice Recording;
        public Profile Copy() { return new Profile { Name = Name, Playback = Playback.Copy(), Recording = Recording.Copy() }; }
    }
    internal sealed class SwitcherConfig
    {
        public Profile[] Profiles = new Profile[2];
        public bool PlaySounds = true;
        // Repairs retain this revision; only explicit user edits advance it.
        public string UserRevision;
        public Dictionary<string, string> Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public SwitcherConfig Copy()
        {
            return new SwitcherConfig { Profiles = new[] { Profiles[0].Copy(), Profiles[1].Copy() }, PlaySounds = PlaySounds,
                UserRevision = UserRevision, Extra = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase) };
        }
        public static SwitcherConfig Load(string path)
        {
            string text = File.ReadAllText(path);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int separator = line.IndexOf('=');
                if (line.StartsWith("#") || separator <= 0) continue;
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
            var config = new SwitcherConfig { Extra = values };
            config.UserRevision = Get(values, "user_revision", AtomicFile.Hash(text));
            bool sounds;
            if (values.ContainsKey("play_sound_cues") && !bool.TryParse(values["play_sound_cues"], out sounds))
                throw new InvalidOperationException("The sound setting is invalid. Open setup to repair the configuration.");
            config.PlaySounds = !values.ContainsKey("play_sound_cues") || bool.Parse(values["play_sound_cues"]);
            for (int i = 0; i < 2; i++)
            {
                string prefix = "profile_" + (i + 1) + "_";
                config.Profiles[i] = new Profile { Name = Get(values, prefix + "name", "Profile " + (i + 1)),
                    Playback = new DeviceChoice(Required(values, prefix + "playback_id"), Get(values, prefix + "playback_name", "Playback " + (i + 1))),
                    Recording = new DeviceChoice(Required(values, prefix + "recording_id"), Get(values, prefix + "recording_name", "Microphone " + (i + 1))) };
            }
            return config;
        }
        public void Save(string path)
        {
            var values = new Dictionary<string, string>(Extra, StringComparer.OrdinalIgnoreCase);
            values["user_revision"] = UserRevision ?? Guid.NewGuid().ToString("N");
            values["play_sound_cues"] = PlaySounds ? "true" : "false";
            for (int i = 0; i < 2; i++)
            {
                string prefix = "profile_" + (i + 1) + "_";
                Profile p = Profiles[i];
                values[prefix + "name"] = p.Name;
                values[prefix + "playback_id"] = p.Playback.Id;
                values[prefix + "playback_name"] = p.Playback.Name;
                values[prefix + "recording_id"] = p.Recording.Id;
                values[prefix + "recording_name"] = p.Recording.Name;
            }
            var lines = new List<string> { "# AudioSwitcher configuration", "# Playback and microphone are always paired." };
            foreach (var item in values)
            {
                if (item.Value == null || item.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new InvalidOperationException("Configuration values must occupy a single line: " + item.Key);
                lines.Add(item.Key + "=" + item.Value);
            }
            AtomicFile.Write(path, string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine, true);
            UserRevision = values["user_revision"];
        }
        // Caller holds OperationGate across the complete read/merge/write operation.
        public static SwitcherConfig SaveEdited(string path, SwitcherConfig edited, SwitcherConfig baseline, Action<SwitcherConfig> prepare = null)
        {
            SwitcherConfig latest = File.Exists(path) ? Load(path) : null;
            if ((baseline == null) != (latest == null) || (baseline != null && latest.UserRevision != baseline.UserRevision))
                throw new InvalidOperationException("Another setup window changed the configuration. Reopen setup before saving so those changes are not overwritten.");
            var merged = edited.Copy();
            if (latest != null)
            {
                merged.Extra = latest.Extra;
                for (int i = 0; i < 2; i++)
                {
                    if (edited.Profiles[i].Playback.Same(baseline.Profiles[i].Playback)) merged.Profiles[i].Playback = latest.Profiles[i].Playback.Copy();
                    if (edited.Profiles[i].Recording.Same(baseline.Profiles[i].Recording)) merged.Profiles[i].Recording = latest.Profiles[i].Recording.Copy();
                }
            }
            if (prepare != null) prepare(merged);
            merged.UserRevision = Guid.NewGuid().ToString("N");
            merged.Save(path);
            return merged;
        }
        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        }
        private static string Required(Dictionary<string, string> values, string key)
        {
            string value = Get(values, key, null);
            if (value == null) throw new InvalidOperationException("Missing configuration setting: " + key);
            return value;
        }
    }
    internal static class AtomicFile
    {
        public static string Hash(string text)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
        }
        public static void Write(string path, string text, bool backup)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(text);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
