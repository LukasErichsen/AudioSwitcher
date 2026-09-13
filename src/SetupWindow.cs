using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;

namespace HeadphoneSwitcher
{
    internal sealed class SetupWindow : Window
    {
        private readonly Grid root;
        private readonly IAudioBackend audio;
        private readonly string configPath, logPath;
        private SwitcherConfig baseline;
        private readonly TextBox[] names = new TextBox[2];
        private readonly ComboBox[] playback = new ComboBox[2], recording = new ComboBox[2];
        private bool loading, busy;
        private string invalidConfigFingerprint;
        internal Grid View { get { return root; } }

        public SetupWindow(IAudioBackend audio, string configPath, string logPath)
        {
            this.audio = audio; this.configPath = configPath; this.logPath = logPath;
            Title = "AudioSwitcher";
            Width = 940; Height = 920; MinWidth = 720; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AudioSwitcher.SetupWindow.xaml"))
                root = (Grid)XamlReader.Load(stream);
            Content = root;
            for (int i = 0; i < 2; i++)
            {
                names[i] = Get<TextBox>("Name" + (i + 1));
                playback[i] = Get<ComboBox>("Playback" + (i + 1)); recording[i] = Get<ComboBox>("Recording" + (i + 1));
                playback[i].SelectionChanged += SelectionChanged; recording[i].SelectionChanged += SelectionChanged;
                int index = i;
                Get<Button>("Apply" + (i + 1)).Click += async (s, e) => await Apply(index);
                Get<Button>("Preview" + (i + 1)).Click += async (s, e) => await Preview(index + 1);
            }
            Get<Button>("Refresh").Content = "Refresh devices";
            Get<Button>("Refresh").Click += (s, e) => RefreshDevices(true);
            Get<Button>("PreviewError").Click += async (s, e) => await Preview(0);
            Get<Button>("Cancel").Click += (s, e) => Close();
            Get<Button>("Save").Click += async (s, e) => await Save();
            Closing += (s, e) => { if (busy) { e.Cancel = true; Notice("Finishing the current operation. Please wait.", false); } };
            SourceInitialized += (s, e) =>
            {
                try { int corners = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref corners, 4); }
                catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
            };
            Loaded += (s, e) =>
            {
                Rect area = SystemParameters.WorkArea;
                Height = Math.Min(Height, area.Height - 24); Width = Math.Min(Width, area.Width - 24);
                Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
                Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
            };
            LoadConfiguration();
        }
        private T Get<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        private void Log(string text) { Logger.Write(logPath, text); }
        private void Notice(string text, bool error)
        {
            var label = Get<TextBlock>("Message"); label.Text = text;
            label.Foreground = new SolidColorBrush(error ? Color.FromRgb(155, 63, 0) : Color.FromRgb(60, 90, 70));
        }
        private void LoadConfiguration()
        {
            string problem = null;
            try
            {
                using (var gate = new OperationGate(configPath, 1000))
                {
                    if (!gate.Acquired) throw new InvalidOperationException("Another audio operation is running. Reopen setup when it finishes.");
                    if (File.Exists(configPath))
                    {
                        try { baseline = SwitcherConfig.Load(configPath); }
                        catch (Exception ex) { invalidConfigFingerprint = AtomicFile.Hash(File.ReadAllText(configPath)); problem = "The saved configuration could not be read. Choose both pairs and save to repair it. " + ex.Message; }
                    }
                }
            }
            catch (Exception ex) { problem = ex.Message; }
            loading = true;
            for (int i = 0; i < 2; i++)
            {
                names[i].Text = baseline == null ? (i == 0 ? "Headphones" : "Speakers") : baseline.Profiles[i].Name;
                if (baseline != null)
                {
                    AddSaved(playback[i], baseline.Profiles[i].Playback);
                    AddSaved(recording[i], baseline.Profiles[i].Recording);
                }
            }
            Get<CheckBox>("Sounds").IsChecked = baseline == null || baseline.PlaySounds;
            loading = false;
            RefreshDevices(false);
            if (problem != null) Notice(problem, true);
        }
        private static void AddSaved(ComboBox box, DeviceChoice choice)
        {
            var device = new MMDevice(choice.Id, choice.Name) { Available = false };
            box.Items.Add(device); box.SelectedItem = device;
        }
        internal static void FillDevices(ComboBox box, List<MMDevice> devices)
        {
            MMDevice previous = box.SelectedItem as MMDevice;
            box.Items.Clear();
            foreach (var device in devices) box.Items.Add(device);
            if (previous != null)
            {
                var found = devices.FirstOrDefault(d => string.Equals(d.Id, previous.Id, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    found = new MMDevice(previous.Id, previous.Name) { Available = false };
                    box.Items.Add(found);
                }
                box.SelectedItem = found;
            }
            // A new profile needs an explicit choice; never guess which device the user intended.
            else box.SelectedIndex = -1;
        }
        private void RefreshDevices(bool announce)
        {
            try
            {
                var outputs = audio.List(EDataFlow.eRender); var inputs = audio.List(EDataFlow.eCapture);
                loading = true;
                for (int i = 0; i < 2; i++) { FillDevices(playback[i], outputs); FillDevices(recording[i], inputs); }
                loading = false;
                UpdateStates(); CurrentDefaults();
                if (announce) Notice("Devices refreshed. Your selections and unsaved edits have been kept.", false);
                if (outputs.Count == 0 || inputs.Count == 0) Notice("Connect both a playback device and a microphone, then choose Refresh devices. Saved selections are kept.", true);
            }
            catch (Exception ex) { loading = false; Notice("Could not refresh devices. " + ex.Message, true); Logger.Write(logPath, "Device refresh failed.", ex); }
        }
        private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!loading) UpdateStates(); }
        private void UpdateStates()
        {
            for (int i = 0; i < 2; i++)
            {
                var output = playback[i].SelectedItem as MMDevice; var input = recording[i].SelectedItem as MMDevice;
                string text = output == null || input == null ? "Choose both devices" : !output.Available || !input.Available ? "Disconnected device saved" : "Both devices connected";
                var state = Get<TextBlock>("State" + (i + 1)); state.Text = text;
                state.Foreground = new SolidColorBrush(text == "Both devices connected" ? Color.FromRgb(15, 107, 56) : Color.FromRgb(155, 91, 0));
                playback[i].ToolTip = output == null ? "Choose a playback device" : output.Name + "\n" + output.Id;
                recording[i].ToolTip = input == null ? "Choose a microphone" : input.Name + "\n" + input.Id;
            }
        }
        private void CurrentDefaults()
        {
            for (int f = 0; f < 2; f++)
            {
                var label = Get<TextBlock>(f == 0 ? "CurrentPlayback" : "CurrentRecording");
                try { var d = audio.Default((EDataFlow)f, ERole.eConsole); label.Text = (f == 0 ? "Playback: " : "Microphone: ") + (d == null ? "Unavailable" : d.Name); }
                catch (Exception ex) { label.Text = (f == 0 ? "Playback" : "Microphone") + ": unavailable"; Log(ex.Message); }
            }
        }
        private Profile ReadProfile(int i)
        {
            var output = playback[i].SelectedItem as MMDevice; var input = recording[i].SelectedItem as MMDevice;
            if (output == null || input == null) throw new InvalidOperationException("Choose both playback and microphone for profile " + (i + 1) + ".");
            return new Profile { Name = string.IsNullOrWhiteSpace(names[i].Text) ? "Profile " + (i + 1) : names[i].Text.Trim(),
                Playback = new DeviceChoice(output.Id, output.Name), Recording = new DeviceChoice(input.Id, input.Name) };
        }
        private void SetBusy(bool value)
        {
            busy = value; Get<StackPanel>("Settings").IsEnabled = !value; Get<StackPanel>("FooterButtons").IsEnabled = !value;
        }
        private async Task Apply(int index)
        {
            Profile profile = null;
            try { profile = ReadProfile(index); }
            catch (Exception ex) { Notice(ex.Message, true); }
            if (profile == null) { if (Get<CheckBox>("Sounds").IsChecked == true) await Preview(0); return; }
            bool sounds = Get<CheckBox>("Sounds").IsChecked == true;
            SetBusy(true); Notice("Applying " + profile.Name + "...", false);
            try
            {
                string result = await Task.Run(() =>
                {
                    using (var gate = new OperationGate(configPath, 0))
                    {
                        if (!gate.Acquired) throw new InvalidOperationException("Another switch is running. Try again in a moment.");
                        try
                        {
                            var engine = new SwitchEngine(audio, Log, Thread.Sleep);
                            var temporary = new SwitcherConfig { Profiles = new[] { profile, profile.Copy() } };
                            engine.Heal(temporary);
                            engine.Apply(profile);
                            bool played = !sounds || SoundCues.TryPlay(index + 1, profile.Playback.Id, Log);
                            return "Applied " + profile.Name + "." + (played ? "" : " The sound could not play.");
                        }
                        catch { if (sounds) SoundCues.TryPlay(0, null, Log); throw; }
                    }
                });
                loading = true;
                playback[index].Items.Clear(); recording[index].Items.Clear();
                AddSaved(playback[index], profile.Playback); AddSaved(recording[index], profile.Recording);
                loading = false;
                RefreshDevices(false);
                Notice(result + " Profile edits are saved separately.", false);
            }
            catch (Exception ex) { Notice(ex.Message, true); Logger.Write(logPath, "Apply now failed.", ex); }
            finally { SetBusy(false); CurrentDefaults(); }
        }
        private async Task Preview(int cue)
        {
            SetBusy(true);
            try
            {
                bool played = await Task.Run(() => SoundCues.TryPlay(cue, null, Log));
                if (!played) Notice("The preview could not play. Check your current playback device and try again.", true);
            }
            finally { SetBusy(false); }
        }
        private async Task Save()
        {
            SwitcherConfig edited;
            try
            {
                edited = new SwitcherConfig { Profiles = new[] { ReadProfile(0), ReadProfile(1) }, PlaySounds = Get<CheckBox>("Sounds").IsChecked == true };
                if (string.Equals(edited.Profiles[0].Playback.Id, edited.Profiles[1].Playback.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(edited.Profiles[0].Recording.Id, edited.Profiles[1].Recording.Id, StringComparison.OrdinalIgnoreCase))
                { Notice("Both profiles use the same pair. Choose a different playback device or microphone so toggling has an effect.", true); return; }
            }
            catch (Exception ex) { Notice(ex.Message, true); return; }
            SetBusy(true); Notice("Saving profiles...", false);
            bool saved = false;
            try
            {
                await Task.Run(() =>
                {
                    using (var gate = new OperationGate(configPath, 1000))
                    {
                        if (!gate.Acquired) throw new InvalidOperationException("Another audio operation is running. Try saving again in a moment.");
                        Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                        if (invalidConfigFingerprint != null)
                        {
                            if (!File.Exists(configPath) || AtomicFile.Hash(File.ReadAllText(configPath)) != invalidConfigFingerprint)
                                throw new InvalidOperationException("The configuration changed since setup opened. Reopen setup before repairing it.");
                            PrepareSave(edited);
                            edited.UserRevision = Guid.NewGuid().ToString("N"); edited.Save(configPath); baseline = edited;
                        }
                        else baseline = SwitcherConfig.SaveEdited(configPath, edited, baseline, PrepareSave);
                        Log("Configuration saved.");
                    }
                });
                saved = true;
            }
            catch (Exception ex) { Notice("Could not save. " + ex.Message, true); Logger.Write(logPath, "Configuration save failed.", ex); }
            finally { SetBusy(false); }
            if (saved) Close();
        }
        private void PrepareSave(SwitcherConfig config)
        {
            new SwitchEngine(audio, Log, Thread.Sleep).Heal(config);
            if (string.Equals(config.Profiles[0].Playback.Id, config.Profiles[1].Playback.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(config.Profiles[0].Recording.Id, config.Profiles[1].Recording.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Both profiles resolve to the same device pair. Choose a different playback device or microphone for one profile.");
        }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }
}
