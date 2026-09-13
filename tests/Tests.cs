using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HeadphoneSwitcher
{
    internal sealed class FakeAudio : IAudioBackend
    {
        public List<MMDevice>[] Devices = { new List<MMDevice> { new MMDevice("p1", "Headphones (USB Audio)"), new MMDevice("p2", "Speakers (Realtek Audio)") },
            new List<MMDevice> { new MMDevice("m1", "Headset microphone (USB Audio)"), new MMDevice("m2", "Microphone (USB Desk Mic)") } };
        public string[,] Current = { { "p1", "p1", "p2" }, { "m1", "m1", "m2" } };
        public int Writes;
        public Action<string, ERole> BeforeSet;
        public bool IgnoreCommunications;
        public List<MMDevice> List(EDataFlow flow) { return new List<MMDevice>(Devices[(int)flow]); }
        public MMDevice Default(EDataFlow flow, ERole role)
        {
            string id = Current[(int)flow, (int)role];
            return Devices[(int)flow].FirstOrDefault(d => d.Id == id);
        }
        public void Set(string id, ERole role)
        {
            Writes++;
            if (BeforeSet != null) BeforeSet(id, role);
            if (IgnoreCommunications && role == ERole.eCommunications && id == "p2") return;
            int flow = Devices[0].Any(d => d.Id == id) ? 0 : Devices[1].Any(d => d.Id == id) ? 1 : -1;
            if (flow < 0) throw new InvalidOperationException("Device disconnected during switch.");
            Current[flow, (int)role] = id;
        }
    }
    internal static class Tests
    {
        private static int passed;
        private static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "checks");
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
        private static void Throws(Action action, string contains)
        {
            try { action(); } catch (Exception ex) { Check(ex.Message.Contains(contains), "Unexpected error: " + ex); return; }
            throw new Exception("Expected failure: " + contains);
        }
        private static SwitcherConfig Config()
        {
            var a = new FakeAudio();
            return new SwitcherConfig { Profiles = new[] {
                new Profile { Name = "Headphones", Playback = new DeviceChoice("p1", a.Devices[0][0].Name), Recording = new DeviceChoice("m1", a.Devices[1][0].Name) },
                new Profile { Name = "Speakers", Playback = new DeviceChoice("p2", a.Devices[0][1].Name), Recording = new DeviceChoice("m2", a.Devices[1][1].Name) } } };
        }
        private static string PathFor(string name) { return Path.Combine(Root, Guid.NewGuid().ToString("N") + "-" + name); }
        private static SwitchEngine Engine(FakeAudio audio, List<string> log) { return new SwitchEngine(audio, log.Add, ms => { }); }
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--probe-lock") { using (var g = new OperationGate(args[1], 0)) return g.Acquired ? 3 : 0; }
            try
            {
                Directory.CreateDirectory(Root);
                if (args.Contains("--native")) { Native(); return 0; }
                Test("exact ID wins over duplicate names", () => {
                    var ds = new List<MMDevice> { new MMDevice("a", "Same"), new MMDevice("b", "Same") };
                    Check(DeviceResolver.Resolve(new DeviceChoice("b", "Same"), ds).Device.Id == "b", "ID precedence");
                });
                Test("exact friendly name repairs changed ID", () => Check(DeviceResolver.Resolve(new DeviceChoice("old", "Headphones"), new List<MMDevice> { new MMDevice("new", "Headphones") }).Method == "exact name", "Name match"));
                Test("Windows numbered prefixes are cleaned conservatively", () => {
                    Check(DeviceResolver.CoreName("2- Speakers (3- USB Audio)") == "Speakers (USB Audio)", "Prefix cleanup");
                    Check(DeviceResolver.CoreName("WH-1000XM5") == "WH-1000XM5", "Model number preserved");
                    Check(DeviceResolver.CoreName("Speakers 2") != DeviceResolver.CoreName("Speakers 3"), "Numbers preserved");
                });
                Test("ambiguous exact names are refused", () => Check(DeviceResolver.Resolve(new DeviceChoice("gone", "USB"), new List<MMDevice> { new MMDevice("a", "USB"), new MMDevice("b", "USB") }).Error.Contains("More than one"), "Must not guess"));
                Test("ambiguous cleaned names are refused", () => Check(DeviceResolver.Resolve(new DeviceChoice("gone", "USB"), new List<MMDevice> { new MMDevice("a", "2- USB"), new MMDevice("b", "3- USB") }).Device == null, "Must not guess"));
                Test("repair heals current-state detection and target devices", () => {
                    var a = new FakeAudio(); var c = Config(); var log = new List<string>();
                    c.Profiles[0].Playback.Id = "old-p1"; c.Profiles[0].Recording.Id = "old-m1";
                    c.Profiles[1].Playback.Id = "old-p2";
                    var e = Engine(a, log); Check(e.Heal(c), "Healing detected"); Check(e.Target(c) == 1, "Current pair recognized");
                    e.Apply(c.Profiles[1]); Check(a.Writes == 6, "All roles applied"); Check(log.Any(s => s.Contains("exact name")), "Repair reason logged");
                });
                Test("mixed current pair selects profile one", () => { var a = new FakeAudio(); a.Current[1, 0] = "m2"; Check(Engine(a, new List<string>()).Target(Config()) == 0, "Fallback"); });
                Test("profile two toggles to profile one", () => { var a = new FakeAudio(); a.Current[0, 0] = "p2"; a.Current[1, 0] = "m2"; Check(Engine(a, new List<string>()).Target(Config()) == 0, "Alternation"); });
                Test("missing microphone changes nothing", () => {
                    var a = new FakeAudio(); a.Devices[1].RemoveAt(1);
                    Throws(() => Engine(a, new List<string>()).Apply(Config().Profiles[1]), "Microphone"); Check(a.Writes == 0, "No writes before validation");
                });
                Test("wrong device flow changes nothing", () => {
                    var a = new FakeAudio(); var p = Config().Profiles[1]; p.Recording.Id = "p2";
                    Throws(() => Engine(a, new List<string>()).Apply(p), "Microphone"); Check(a.Writes == 0, "Flow checked");
                });
                Test("partial switch restores distinct defaults for every role", () => {
                    var a = new FakeAudio(); var old = (string[,])a.Current.Clone(); var log = new List<string>(); bool failed = false;
                    a.BeforeSet = (id, role) => { if (id == "m2" && !failed) { failed = true; throw new Exception("USB vanished"); } };
                    Throws(() => Engine(a, log).Apply(Config().Profiles[1]), "were restored");
                    for (int f = 0; f < 2; f++) for (int r = 0; r < 3; r++) Check(a.Current[f, r] == old[f, r], "Each previous role restored");
                    Check(log.Any(s => s.Contains("restored and verified")), "Restoration logged");
                });
                Test("silent Windows failure is detected by verification", () => {
                    var a = new FakeAudio { IgnoreCommunications = true }; a.Current[0, 2] = "p1";
                    Throws(() => Engine(a, new List<string>()).Apply(Config().Profiles[1]), "were restored"); Check(a.Current[0, 0] == "p1", "Rollback on failed verification");
                });
                Test("failed rollback is reported honestly", () => {
                    var a = new FakeAudio(); bool failure = false;
                    a.BeforeSet = (id, role) => { if (id == "m2") { failure = true; throw new Exception("Unavailable"); } if (failure && id == "p1") throw new Exception("Also unplugged"); };
                    Throws(() => Engine(a, new List<string>()).Apply(Config().Profiles[1]), "could not be restored");
                });
                Test("legacy configuration loads with sounds enabled", () => {
                    var c = Config(); string path = PathFor("legacy.config"); c.Save(path);
                    File.WriteAllLines(path, File.ReadAllLines(path).Where(s => !s.StartsWith("user_revision=") && !s.StartsWith("play_sound_cues=")).ToArray());
                    var loaded = SwitcherConfig.Load(path); Check(loaded.PlaySounds && loaded.Profiles[0].Playback.Id == "p1", "Legacy compatibility");
                });
                Test("safe save keeps the previous complete configuration", () => {
                    string path = PathFor("save.config"); var c = Config(); c.Save(path); string old = File.ReadAllText(path);
                    c.Profiles[0].Name = "Work = calls # 1"; c.PlaySounds = false; c.Save(path);
                    Check(File.ReadAllText(path + ".bak") == old, "Backup"); Check(SwitcherConfig.Load(path).Profiles[0].Name == c.Profiles[0].Name, "Name roundtrip");
                    Check(!SwitcherConfig.Load(path).PlaySounds, "Checkbox persisted");
                });
                Test("invalid value leaves the existing file intact", () => {
                    string path = PathFor("invalid.config"); var c = Config(); c.Save(path); string old = File.ReadAllText(path); c.Profiles[0].Name = "bad\nname";
                    Throws(() => c.Save(path), "single line"); Check(File.ReadAllText(path) == old, "Existing config safe");
                });
                Test("file sharing failure leaves original config intact", () => {
                    string path = PathFor("locked.config"); var c = Config(); c.Save(path); string old = File.ReadAllText(path);
                    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        bool failed = false; try { c.Save(path); } catch (IOException) { failed = true; } Check(failed, "Save should fail on denied replace");
                    }
                    Check(File.ReadAllText(path) == old, "Original survived");
                });
                Test("automatic repairs merge with unsaved setup edits", () => {
                    string path = PathFor("merge.config"); var c = Config(); c.Save(path); var baseline = SwitcherConfig.Load(path); var edit = baseline.Copy(); edit.PlaySounds = false;
                    var repaired = baseline.Copy(); repaired.Profiles[0].Playback.Id = "new-id"; repaired.Save(path);
                    var merged = SwitcherConfig.SaveEdited(path, edit, baseline);
                    Check(merged.Profiles[0].Playback.Id == "new-id" && !merged.PlaySounds, "Both changes preserved");
                });
                Test("a stale setup window cannot overwrite another save", () => {
                    string path = PathFor("conflict.config"); var c = Config(); c.Save(path); var baseline = SwitcherConfig.Load(path);
                    var edit = baseline.Copy(); edit.Profiles[1].Name = "New name"; SwitcherConfig.SaveEdited(path, edit, baseline);
                    Throws(() => SwitcherConfig.SaveEdited(path, baseline, baseline), "Another setup");
                    Check(SwitcherConfig.Load(path).Profiles[1].Name == "New name", "Latest remains");
                });
                Test("log retention keeps whole errors and old dot timestamps", () => {
                    string input = "[2024-01-01 10.00.00] Old error\nOld details\n[2025-09-13 10.00.00] Boundary\nBoundary details\n[2026-01-01 10:00:00.123] Recent\nRecent details\n[unknown] Keep me\nUnknown details\n";
                    string kept = Logger.Retain(input, new DateTime(2025, 9, 13, 10, 0, 0));
                    Check(!kept.Contains("Old") && kept.Contains("Boundary details") && kept.Contains("Recent details") && kept.Contains("Unknown details"), "Whole entry retention");
                });
                Test("duplicate trigger guard accepts legitimate later presses", () => {
                    string path = PathFor("trigger"); var now = DateTime.UtcNow;
                    Check(TriggerGuard.Accept(path, now), "First"); Check(!TriggerGuard.Accept(path, now.AddMilliseconds(100)), "Duplicate"); Check(TriggerGuard.Accept(path, now.AddMilliseconds(600)), "Later");
                });
                Test("operation gate excludes another process", () => {
                    string path = PathFor("mutex"); using (var gate = new OperationGate(path, 0))
                    {
                        var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--probe-lock \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true };
                        using (var p = Process.Start(start)) { Check(p.WaitForExit(5000), "Child timeout"); Check(p.ExitCode == 0, "Child excluded"); }
                    }
                });
                Test("profile two starts sooner with an unchanged first strike", () => {
                    SoundCues.WaveFormat f1, f2, fe; var one = SoundCues.Samples(1, out f1); var two = SoundCues.Samples(2, out f2); var error = SoundCues.Samples(0, out fe);
                    int secondStart = (int)(f1.Rate * 0.18) * f1.BlockAlign;
                    Check(two.Take(secondStart).SequenceEqual(one.Take(secondStart)), "First strike unchanged before the overlap");
                    Check(two.Length < 1.5 * one.Length, "Tighter timing than back-to-back playback");
                    Check(!two.Skip(secondStart).Take(one.Length - secondStart).SequenceEqual(one.Skip(secondStart)), "Second strike overlaps the fading first note");
                    Check(!error.SequenceEqual(one), "Distinct error");
                    foreach (var bytes in new[] { one, two, error }) for (int i = 0; i < bytes.Length; i += 2) Check(Math.Abs((int)BitConverter.ToInt16(bytes, i)) <= 6554, "Conservative peak");
                    WritePreview("profile-1.wav", one, f1); WritePreview("profile-2.wav", two, f2); WritePreview("error.wav", error, fe);
                });
                Test("the second strike is one whole tone higher", () => {
                    var format = new SoundCues.WaveFormat { Tag = 1, Channels = 1, Rate = 48000, Bits = 16, BlockAlign = 2, BytesPerSecond = 96000 };
                    var tone = new byte[19200 * 2];
                    for (int i = 0; i < 19200; i++) { var sample = BitConverter.GetBytes((short)Math.Round(1000 * Math.Sin(2 * Math.PI * 1000 * i / 48000))); Buffer.BlockCopy(sample, 0, tone, i * 2, 2); }
                    var pair = SoundCues.RisingPair(tone, format);
                    int start = 8640, crossings = 0, previous = 0;
                    for (int i = 0; i < 4800; i++) {
                        int value = BitConverter.ToInt16(pair, (start + i) * 2) - BitConverter.ToInt16(tone, (start + i) * 2);
                        if (previous <= 0 && value > 0) crossings++;
                        previous = value;
                    }
                    Check(crossings >= 111 && crossings <= 114, "Expected about 112 cycles in 100 ms after lifting 1000 Hz by two semitones");
                });
                Test("original cues are embedded without depending on Windows recordings", () => {
                    var resources = Assembly.GetExecutingAssembly().GetManifestResourceNames();
                    Check(resources.Contains("AudioSwitcher.Confirm.wav") && resources.Contains("AudioSwitcher.Error.wav"), "Self-contained original sounds");
                });
                Test("clip preparation removes leading and trailing silence", () => {
                    var format = new SoundCues.WaveFormat { Tag = 1, Channels = 2, Rate = 1000, Bits = 16, BlockAlign = 4, BytesPerSecond = 4000 };
                    var samples = new byte[40 * format.BlockAlign];
                    for (int frame = 10; frame < 30; frame++) {
                        // Only the right channel is audible: silence detection must inspect every channel.
                        byte[] value = BitConverter.GetBytes((short)1000); Buffer.BlockCopy(value, 0, samples, frame * format.BlockAlign + 2, 2);
                    }
                    var trimmed = SoundCues.PrepareClip(samples, format);
                    Check(trimmed.Length == 20 * format.BlockAlign, "Both silent edges removed");
                    Check(BitConverter.ToInt16(trimmed, 2) > 0 && BitConverter.ToInt16(trimmed, trimmed.Length - 2) > 0, "No silent frames added by fades");
                });
                Test("a silent or malformed clip fails cleanly", () => {
                    var format = new SoundCues.WaveFormat { Tag = 1, Channels = 1, Rate = 44100, Bits = 16, BlockAlign = 2 };
                    Throws(() => SoundCues.PrepareClip(new byte[20], format), "only silence");
                    Throws(() => SoundCues.PrepareClip(new byte[3], format), "layout");
                });
                Test("COM signatures preserve HRESULT and PROPVARIANT has x64 size", () => {
                    foreach (var type in new[] { typeof(IMMDeviceEnumerator), typeof(IMMDeviceCollection), typeof(IMMDevice), typeof(IPropertyStore), typeof(IPolicyConfig) })
                        foreach (var m in type.GetMethods()) Check((m.GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig) != 0, type.Name + "." + m.Name);
                    Check(Marshal.SizeOf(typeof(PROPVARIANT)) == 24, "PROPVARIANT size");
                });
                new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                Test("refresh preserves disconnected selection and reconnects by ID", () => {
                    var box = new ComboBox(); var saved = new MMDevice("saved", "Headset"); box.Items.Add(saved); box.SelectedItem = saved;
                    SetupWindow.FillDevices(box, new List<MMDevice> { new MMDevice("other", "Other device") });
                    Check(((MMDevice)box.SelectedItem).Id == "saved" && !((MMDevice)box.SelectedItem).Available, "Preserve disconnected");
                    SetupWindow.FillDevices(box, new List<MMDevice> { new MMDevice("saved", "Headset") });
                    Check(((MMDevice)box.SelectedItem).Available, "Reconnected");
                });
                Test("new profiles do not silently pick the first device", () => { var box = new ComboBox(); SetupWindow.FillDevices(box, new FakeAudio().Devices[0]); Check(box.SelectedItem == null, "Explicit selection"); });
                Test("setup refresh preserves names, sound setting and selected devices", () => {
                    string path = PathFor("ui.config"); Config().Save(path); var a = new FakeAudio(); var window = new SetupWindow(a, path, PathFor("ui.log"));
                    var view = window.View; ((TextBox)view.FindName("Name1")).Text = "Unsaved custom name"; ((CheckBox)view.FindName("Sounds")).IsChecked = false;
                    a.Devices[0].RemoveAt(0); window.RefreshDevices(true);
                    Check(((TextBox)view.FindName("Name1")).Text == "Unsaved custom name" && ((CheckBox)view.FindName("Sounds")).IsChecked == false, "Edits preserved");
                    Check(!((MMDevice)((ComboBox)view.FindName("Playback1")).SelectedItem).Available, "Saved device retained");
                    Render(view, 940, 888, "setup-disconnected.png"); window.Close();
                });
                Test("setup renders at normal and compact sizes", () => {
                    string path = PathFor("render.config"); Config().Save(path); var window = new SetupWindow(new FakeAudio(), path, PathFor("render.log"));
                    Render(window.View, 940, 888, "setup.png"); Render(window.View, 720, 530, "setup-compact.png");
                    var save = (Button)window.View.FindName("Save");
                    var position = save.TransformToAncestor(window.View).Transform(new Point(0, 0));
                    Check(save.ActualHeight > 0 && position.Y + save.ActualHeight <= 530, "Save stays inside compact viewport"); window.Close();
                });
                Test("setup stays usable with no audio devices", () => {
                    var a = new FakeAudio(); a.Devices[0].Clear(); a.Devices[1].Clear();
                    var window = new SetupWindow(a, PathFor("empty.config"), PathFor("empty.log"));
                    Check(((TextBlock)window.View.FindName("Message")).Text.Contains("Connect both"), "Visible explanation");
                    window.Close();
                });
                Test("Apply now changes the fake pair without saving edits", () => {
                    string path = PathFor("apply.config"); var c = Config(); c.PlaySounds = false; c.Save(path); string saved = File.ReadAllText(path);
                    var a = new FakeAudio(); var window = new SetupWindow(a, path, PathFor("apply.log"));
                    ClickAndWait(window.View, "Apply2");
                    Check(a.Writes == 6 && a.Current[0, 0] == "p2" && a.Current[1, 0] == "m2", "Complete pair applied");
                    Check(File.ReadAllText(path) == saved, "Apply does not save");
                    Check(((TextBlock)window.View.FindName("Message")).Text.Contains("Applied Speakers"), "Visible confirmation"); window.Close();
                });
                Test("Save button persists names and sound preference", () => {
                    string path = PathFor("save-ui.config"); Config().Save(path); var window = new SetupWindow(new FakeAudio(), path, PathFor("save-ui.log"));
                    ((TextBox)window.View.FindName("Name1")).Text = "My headphones"; ((CheckBox)window.View.FindName("Sounds")).IsChecked = false;
                    ClickAndWait(window.View, "Save");
                    var saved = SwitcherConfig.Load(path); Check(saved.Profiles[0].Name == "My headphones" && !saved.PlaySounds, "Save button works");
                });
                Test("failed Save keeps setup open with edits and an explanation", () => {
                    string path = PathFor("save-fail.config"); Config().Save(path); var window = new SetupWindow(new FakeAudio(), path, PathFor("save-fail.log"));
                    bool closed = false; window.Closed += (s, e) => closed = true;
                    ((TextBox)window.View.FindName("Name1")).Text = "Still unsaved";
                    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) ClickAndWait(window.View, "Save");
                    Check(!closed && ((TextBlock)window.View.FindName("Message")).Text.Contains("Could not save"), "Failure visible, window remains");
                    Check(((TextBox)window.View.FindName("Name1")).Text == "Still unsaved", "Edits retained"); window.Close();
                });
                Test("identical pairs are explained before saving", () => {
                    string path = PathFor("same.config"); var c = Config(); c.Profiles[1] = c.Profiles[0].Copy(); c.Save(path);
                    var window = new SetupWindow(new FakeAudio(), path, PathFor("same.log")); ClickAndWait(window.View, "Save");
                    Check(((TextBlock)window.View.FindName("Message")).Text.Contains("same pair"), "Duplicate warning"); window.Close();
                });
                Console.WriteLine(passed + " checks passed. No system audio settings were changed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static void Render(Grid view, int width, int height, string name)
        {
            // A hidden HWND initializes text editors and their rendering pipeline without taking focus.
            var owner = Window.GetWindow(view);
            if (owner != null) owner.Content = null;
            using (var source = new HwndSource(new HwndSourceParameters("AudioSwitcher layout check") {
                Width = width, Height = height, PositionX = -20000, PositionY = -20000, WindowStyle = unchecked((int)0x80000000) }))
            {
            source.RootVisual = view;
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            view.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))) encoder.Save(stream);
            source.RootVisual = null;
            }
            if (owner != null) owner.Content = view;
        }
        private static void WritePreview(string name, byte[] data, SoundCues.WaveFormat format)
        {
            using (var writer = new BinaryWriter(File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name))))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write(format.Tag); writer.Write(format.Channels); writer.Write(format.Rate);
                writer.Write(format.BytesPerSecond); writer.Write(format.BlockAlign); writer.Write(format.Bits);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(data.Length); writer.Write(data);
            }
        }
        private static void ClickAndWait(Grid view, string name)
        {
            ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var timer = Stopwatch.StartNew();
            while (!((StackPanel)view.FindName("Settings")).IsEnabled)
            {
                Check(timer.ElapsedMilliseconds < 8000, "UI operation timed out");
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame); Thread.Sleep(5);
            }
        }
        private static void Native()
        {
            var audio = new WindowsAudioBackend();
            for (int f = 0; f < 2; f++)
            {
                var devices = audio.List((EDataFlow)f); Console.WriteLine((EDataFlow)f + ": " + devices.Count + " active endpoints");
                for (int r = 0; r < 3; r++) { var d = audio.Default((EDataFlow)f, (ERole)r); Console.WriteLine("  " + (ERole)r + ": " + (d == null ? "Unavailable" : d.Name)); }
            }
            var output = audio.Default(EDataFlow.eRender, ERole.eConsole);
            if (output != null) Console.WriteLine("Exact output sound mapping: " + SoundCues.FindOutput(output.Id));
            Console.WriteLine("Native read-only audio checks passed.");
        }
    }
}
