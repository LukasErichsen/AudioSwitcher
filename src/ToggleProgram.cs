using System;
using System.IO;
using System.Threading;

namespace HeadphoneSwitcher
{
    internal static class ToggleProgram
    {
        [STAThread]
        private static int Main()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDirectory);
                using (var gate = new OperationGate(AppPaths.ConfigPath, 0))
                {
                    if (!gate.Acquired) { Log("Toggle ignored: another operation is in progress."); return 0; }
                    if (!TriggerGuard.Accept(Path.Combine(AppPaths.AppDirectory, "last-trigger"), DateTime.UtcNow))
                    { Log("Toggle ignored: duplicate trigger within 550 ms."); return 0; }
                    bool sounds = true;
                    try
                    {
                        if (!File.Exists(AppPaths.ConfigPath)) throw new InvalidOperationException("No configuration exists. Run AudioSwitcher.Config.exe first.");
                        var config = SwitcherConfig.Load(AppPaths.ConfigPath);
                        sounds = config.PlaySounds;
                        var engine = new SwitchEngine(new WindowsAudioBackend(), Log, Thread.Sleep);
                        bool healed = engine.Heal(config);
                        if (string.Equals(config.Profiles[0].Playback.Id, config.Profiles[1].Playback.Id, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(config.Profiles[0].Recording.Id, config.Profiles[1].Recording.Id, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Both profiles use the same device pair. Choose different pairs in setup.");
                        if (healed)
                        {
                            try { config.Save(AppPaths.ConfigPath); Log("Repaired device IDs saved."); }
                            catch (Exception ex) { Logger.Write(AppPaths.LogPath, "Could not save repaired IDs; using the repaired pair for this switch.", ex); }
                        }
                        int target = engine.Target(config);
                        engine.Apply(config.Profiles[target]);
                        Log("Switched to " + config.Profiles[target].Name + ".");
                        if (sounds)
                        {
                            string secondary = config.PlaySoundsOnBothDevices ? config.Profiles[1 - target].Playback.Id : config.Profiles[target].Playback.Id;
                            SoundCues.TryPlayOnBoth(target + 1, config.Profiles[target].Playback.Id, secondary, Log);
                        }
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Logger.Write(AppPaths.LogPath, "Switch failed.", ex);
                        if (sounds) SoundCues.TryPlay(0, null, Log);
                        return 1;
                    }
                }
            }
            catch (Exception ex) { Logger.Write(AppPaths.LogPath, "Toggle entrypoint failed.", ex); return 1; }
        }
        private static void Log(string message) { Logger.Write(AppPaths.LogPath, message); }
    }
}
